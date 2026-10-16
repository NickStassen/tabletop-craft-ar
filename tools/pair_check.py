"""Pairing and the automatic game files against a running plugin (PROTOCOL.md "Pairing", "Asset delivery").

A fake headset pairs through the server console (the FIFO that tools/e2e.sh-style test servers read their stdin from),
then checks what a paired headset gets:
  1. hello with pair_v1 and no credential: a closed server (auth.token or auth.require-pairing) answers pair_code at
     once and waits; an open one answers welcome (untrusted: no assets_v1, no command_v1) and pair_code after a
     pair_request. A second pair_request replaces the code; anything else while waiting is bad_message.
  2. "tabletop pair <code>" in the console: paired (token, device, server_id); a closed server continues with welcome on
     the same connection, an open one is reconnected with device_token. The paired session gets assets_v1 and
     command_v1, welcome.pairing.paired and the same server_id.
  3. The game files: assets offers until pack.zip, entity_models.json and sounds.zip are there (state ready), then
     entity_models.json downloads whole and matches its SHA-256 and pack.zip's first chunk is a zip.
  4. "tabletop unpair <device>": the connected headset gets auth_failed and close 4003; the old token then gets a new
     code (token_rejected) instead of a welcome.
  5. A hello without pair_v1 and no credential on a closed server: auth_failed, 4003.

  python tools/pair_check.py --url ws://127.0.0.1:8765/tabletop --console tools/run/x/stdin.fifo [--closed] [--report out.json]
"""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import pathlib
import sys
import time

import websockets

from tabletop_protocol import ASSET_NAMES, CAP_PAIR, Envelope, parse_asset_frame, validate

CAPS = ["rle_palette_v1", "avatar", "entities_v1", "sound_v1", "assets_v1", "command_v1", "chat_v1", "status_v1", CAP_PAIR]


class Headset:
    def __init__(self, url: str, caps: list[str] = CAPS, token: str = "", device_token: str | None = None) -> None:
        self.url, self.caps, self.token, self.device_token = url, caps, token, device_token
        self.env, self.msgs, self.frames = Envelope(), [], []
        self.closed: int | None = None
        self.pinged = 0.0

    async def open(self) -> None:
        self.ws = await websockets.connect(self.url, max_size=16 * 1024 * 1024)
        kw = {"device_name": "pair-check"}
        if self.device_token:
            kw["device_token"] = self.device_token
        await self.send("hello", client="pair-check", client_version="1.1.0", protocols=[1], token=self.token, caps=self.caps, **kw)
        self.task = asyncio.create_task(self.reader())

    async def send(self, t: str, **kw) -> int:
        m = self.env.wrap(t, **kw)
        validate(m)
        await self.ws.send(json.dumps(m))
        return m["seq"]

    async def reader(self) -> None:
        try:
            async for raw in self.ws:
                if isinstance(raw, bytes):
                    self.frames.append(parse_asset_frame(raw))
                    continue
                m = json.loads(raw)
                validate(m)
                self.msgs.append(m)
        except websockets.ConnectionClosed as e:
            self.closed = e.rcvd.code if e.rcvd else -1
        else:
            self.closed = self.ws.close_code

    def of(self, t: str) -> list[dict]:
        return [m for m in self.msgs if m["type"] == t]

    async def until(self, pred, timeout: float = 5.0):
        """Waits for pred, pinging every 2 s like the app (the server closes a session after 15 s of silence)."""
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            r = pred()
            if r:
                return r
            if self.closed is None and time.monotonic() - self.pinged > 2:
                self.pinged = time.monotonic()
                try:
                    await self.send("ping", id=1)
                except websockets.ConnectionClosed:
                    pass
            await asyncio.sleep(0.02)
        return None

    async def close(self) -> None:
        await self.ws.close()
        self.task.cancel()


def console(fifo: pathlib.Path, line: str) -> None:
    with open(fifo, "w") as f:
        f.write(line + "\n")


async def run(a: argparse.Namespace) -> dict:
    report: dict = {"checks": []}
    fails = 0

    def check(name: str, ok, detail="") -> None:
        nonlocal fails
        ok = bool(ok)
        report["checks"].append({"name": name, "ok": ok, "detail": str(detail)})
        print(("PASS " if ok else "FAIL ") + name + (f" ({detail})" if detail else ""))
        if not ok:
            fails += 1

    fifo = pathlib.Path(a.console)
    h = Headset(a.url)
    await h.open()
    first = await h.until(lambda: h.of("pair_code") or h.of("welcome"), 10)
    closed = bool(h.of("pair_code")) and not h.of("welcome")
    check("closed server: pair_code at once, no welcome" if a.closed else "open server: welcome first, untrusted",
          closed == a.closed and first, [m["type"] for m in h.msgs[:3]])
    if not closed:
        w = h.of("welcome")[0]
        caps = w.get("caps", [])
        untrusted = "assets_v1" not in caps and "command_v1" not in caps
        check("open: welcome.pairing says not paired; no assets_v1 or command_v1 before pairing",
              w.get("pairing", {}).get("paired") is False and "pair_v1" in caps and untrusted, caps)
        await h.send("pair_request", device_name="pair-check")
        await h.until(lambda: h.of("pair_code"), 5)
    else:
        s = await h.send("subscribe", origin=[0, 60, 0], dims=[16, 16, 16])
        err = await h.until(lambda: [m for m in h.of("error") if m.get("ref_seq") == s], 5)
        check("waiting: anything but ping/pair_request is bad_message",
              err and err[0]["code"] == "bad_message", err and err[0]["message"])
    codes = h.of("pair_code")
    check("pair_code: 6 digits, 300 s", codes and len(codes[-1]["code"]) == 6 and codes[-1]["expires_s"] == 300, codes and codes[-1])
    await h.send("pair_request", device_name="pair-check")
    await h.until(lambda: len(h.of("pair_code")) >= len(codes) + 1, 5)
    code = h.of("pair_code")[-1]["code"]
    check("a second pair_request gives a fresh code", len(h.of("pair_code")) == len(codes) + 1, f"{len(h.of('pair_code'))} codes")
    console(fifo, "tabletop pair 000000" if code != "000000" else "tabletop pair 000001")
    console(fifo, f"tabletop pair {code} {a.player}")
    paired = await h.until(lambda: h.of("paired"), 10)
    check("console 'tabletop pair <code>': paired with token, device, server_id",
          paired and len(paired[0]["token"]) == 43, paired and paired[0]["device"])
    if not paired:
        return report | {"fails": fails + 1}
    token, device, server_id = paired[0]["token"], paired[0]["device"], paired[0]["server_id"]
    if closed:
        w = await h.until(lambda: h.of("welcome"), 10)
        check("closed: welcome follows on the same connection", w, h.of("welcome") and h.of("welcome")[0]["caps"])
        p = h
    else:
        await h.close()
        p = Headset(a.url, device_token=token)
        await p.open()
        await p.until(lambda: p.of("welcome"), 10)
    w = p.of("welcome")[0] if p.of("welcome") else {}
    caps = w.get("caps", [])
    check("paired session: welcome.pairing.paired, same server_id, assets_v1 and command_v1 granted",
          w.get("pairing", {}).get("paired") is True and w.get("pairing", {}).get("device") == device and w.get("server_id") == server_id
          and "assets_v1" in caps and "command_v1" in caps, f"{w.get('pairing')} {caps}")
    report["server_id"] = server_id

    # the automatic game files
    t0 = time.monotonic()

    def ready():
        offers = p.of("assets")
        if not offers:
            return None
        last = offers[-1]
        names = {f["name"] for f in last["files"]}
        return last if last.get("state") in ("ready", "failed", "missing") and names else None
    offer = await p.until(ready, a.build_timeout)
    offers = p.of("assets")
    states = [o.get("state") for o in offers]
    report["asset_states"] = states
    report["asset_files"] = offer and offer["files"]
    names = {f["name"] for f in offer["files"]} if offer else set()
    took = time.monotonic() - t0
    check("game files built from the PC's own install and offered (state ready)",
          offer and offer.get("state") == "ready" and names == set(ASSET_NAMES), f"{states} {sorted(names)} after {took:.0f} s")
    if offer and "entity_models.json" in names:
        f = next(x for x in offer["files"] if x["name"] == "entity_models.json")
        data = bytearray()
        off = 0
        while off < f["size"]:
            ln = min(offer["chunk"], f["size"] - off)
            await p.send("asset_get", name=f["name"], offset=off, length=ln)
            got = await p.until(lambda at=off: [fr for fr in p.frames if fr[0] == "entity_models.json" and fr[1] == at], 10)
            if not got:
                break
            data += got[0][2]
            off += len(got[0][2])
        check("entity_models.json downloads whole and matches its SHA-256",
              hashlib.sha256(bytes(data)).hexdigest() == f["sha256"], f"{len(data)} bytes")
        try:
            doc = json.loads(bytes(data))
            n = len(doc.get("models", {}))
            check("entity_models.json is the dumper's format with models", doc.get("format") == 1 and n > 100, n)
        except ValueError as e:
            check("entity_models.json parses", False, e)
    if offer and "pack.zip" in names:
        await p.send("asset_get", name="pack.zip", offset=0, length=1024)
        got = await p.until(lambda: [fr for fr in p.frames if fr[0] == "pack.zip"], 10)
        check("pack.zip starts like a zip", got and got[0][2][:2] == b"PK", got and got[0][2][:4])
    sizes = {f["name"]: f["size"] for f in offer["files"]} if offer else {}
    report["sizes_mb"] = {k: round(v / 1e6, 1) for k, v in sizes.items()}

    # untrusted second headset on an open server: no game files
    if not closed:
        u = Headset(a.url, caps=[c for c in CAPS if c != CAP_PAIR])
        await u.open()
        await u.until(lambda: u.of("welcome"), 10)
        uc = u.of("welcome")[0]["caps"] if u.of("welcome") else []
        s = await u.send("asset_get", name="pack.zip", offset=0, length=1024)
        e = await u.until(lambda: [m for m in u.of("error") if m.get("ref_seq") == s], 5)
        check("an unpaired 1.0-style headset gets no game files",
              "assets_v1" not in uc and e and e[0]["code"] == "asset_denied", e and e[0]["message"])
        await u.close()

    # unpair: the connected headset is closed with 4003; its old token gets a new code
    console(fifo, f"tabletop unpair {device}")
    await p.until(lambda: p.closed is not None, 10)
    errs = [m for m in p.of("error") if m["code"] == "auth_failed"]
    check("'tabletop unpair': auth_failed and close 4003", p.closed == 4003 and errs, f"close {p.closed}")
    q = Headset(a.url, device_token=token)
    await q.open()
    await q.until(lambda: q.of("pair_code") or q.of("welcome"), 10)
    if closed:
        pc = q.of("pair_code")
        check("the old token gets a new code (token_rejected)",
              pc and pc[0].get("token_rejected") is True and not q.of("welcome"), pc and pc[0])
    else:
        w2 = q.of("welcome")[0] if q.of("welcome") else {}
        check("the old token is rejected in welcome.pairing",
              w2.get("pairing", {}).get("token_rejected") is True and "assets_v1" not in w2.get("caps", []), w2.get("pairing"))
    await q.close()

    # no pair_v1, no credential
    if closed:
        r = Headset(a.url, caps=[c for c in CAPS if c != CAP_PAIR])
        await r.open()
        await r.until(lambda: r.closed is not None, 10)
        e = r.of("error")
        check("closed: a headset without pair_v1 gets auth_failed and 4003",
              r.closed == 4003 and e and e[0]["code"] == "auth_failed", e and e[0]["message"])
    return report | {"fails": fails}


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--url", required=True)
    p.add_argument("--console", required=True, help="the server's stdin FIFO")
    p.add_argument("--closed", action="store_true", help="the server needs a credential (auth.require-pairing or auth.token)")
    p.add_argument("--player", default="PairCheck", help="the Java player the pairing names")
    p.add_argument("--build-timeout", type=float, default=240, help="seconds to wait for the game files to be built")
    p.add_argument("--report", default=None)
    a = p.parse_args()
    report = asyncio.run(run(a))
    if a.report:
        pathlib.Path(a.report).write_text(json.dumps(report, indent=1))
    print("ALL PASS" if report["fails"] == 0 else f"{report['fails']} FAILED")
    return 0 if report["fails"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
