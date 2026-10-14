"""Block editing from the headset against a running plugin (PROTOCOL.md 4.20).

Connects like the Quest (hello caps edit_v1), finds the surface block in the middle of the region by looking down
through air and water (as the headset's ray does), then:
  1. breaks it and waits for the block_update, puts it back, and places a gold block on top;
  2. checks that a second, fresh connection sees the gold block in its snapshot (the world really changed) and that
     it can't edit without edit_v1;
  3. error paths: occupied cell, outside the region, unknown state, a deny-listed block, a malformed face;
  4. a burst of 40 edits: about 20 pass the rate limit (edit.max-per-second), the rest get rate_limited;
  5. breaks the gold block again, so the world ends as it started (apart from water that flowed).

  python tools/edit_check.py --url ws://127.0.0.1:8765/tabletop [--token T] [--report out.json]
"""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time

import websockets

from tabletop_protocol import CAP_EDIT, Envelope, WorldMirror, block_edit, validate

SKIP = {"air", "cave_air", "void_air", "barrier", "light", "structure_void", "water", "lava", "bubble_column"}


def name(state: str) -> str:
    return state.split("[", 1)[0].split(":", 1)[-1]


class Client:
    def __init__(self, url: str, token: str, caps: list[str]) -> None:
        self.url, self.token, self.caps = url, token, caps
        self.env, self.mirror, self.msgs = Envelope(), WorldMirror(), []
        self.welcome: dict | None = None

    async def open(self) -> None:
        self.ws = await websockets.connect(self.url, max_size=8 * 1024 * 1024)
        await self.send("hello", client="edit-check", client_version="0.1.0", protocols=[1], token=self.token, caps=self.caps)
        self.task = asyncio.create_task(self.reader())

    async def send(self, t: str, **kw) -> int:
        m = self.env.wrap(t, **kw)
        validate(m)
        await self.ws.send(json.dumps(m))
        return m["seq"]

    async def reader(self) -> None:
        async for raw in self.ws:
            m = json.loads(raw)
            validate(m)
            if m["type"] == "welcome":
                self.welcome = m
            elif m["type"] == "chunk_snapshot":
                self.mirror.apply_snapshot(m)
            elif m["type"] == "block_update":
                self.mirror.apply_block_update(m)
            self.msgs.append(m)

    async def until(self, pred, timeout: float = 5.0):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            r = pred()
            if r:
                return r
            await asyncio.sleep(0.005)
        return None

    def errors(self, code: str | None = None, pos=None, ref_seq: int | None = None) -> list[dict]:
        return [m for m in self.msgs if m["type"] == "error" and (code is None or m["code"] == code)
                and (pos is None or m.get("pos") == list(pos)) and (ref_seq is None or m.get("ref_seq") == ref_seq)]

    async def close(self) -> None:
        await self.ws.close()
        self.task.cancel()


async def run(a: argparse.Namespace) -> dict:
    rep: dict = {"checks": []}

    def check(ok: bool, what: str, **info) -> None:
        rep["checks"].append({"ok": bool(ok), "what": what, **info})
        print(("PASS " if ok else "FAIL ") + what + (f" {info}" if info else ""), flush=True)

    c = Client(a.url, a.token, ["rle_palette_v1", CAP_EDIT])
    await c.open()
    await c.until(lambda: c.mirror.loaded and c.welcome)
    caps = (c.welcome or {}).get("caps") or []
    check(CAP_EDIT in caps, "welcome grants edit_v1", caps=caps)
    m = c.mirror
    ox, oy, oz = m.origin
    dx, dy, dz = m.dims
    lx, lz = dx // 2, dz // 2
    ly = next((y for y in range(dy - 1, -1, -1) if name(m.palette[m.grid[m.index(lx, y, lz)]]) not in SKIP), None)
    if ly is None or ly + 2 >= dy:
        check(False, "found a surface block with room above it in the middle column")
        return rep
    pos, above = (ox + lx, oy + ly, oz + lz), (ox + lx, oy + ly + 1, oz + lz)
    original = m.at_world(*pos)
    rep["surface"] = {"pos": pos, "state": original, "above": m.at_world(*above)}

    t0 = time.perf_counter()
    await c.send("block_edit", **block_edit("break", pos, face="up"))
    got = await c.until(lambda: m.at_world(*pos) != original and m.at_world(*pos))
    check(got is not None and name(got) in ("air", "water"), "break -> block_update", pos=pos, was=original, now=got,
          ms=round((time.perf_counter() - t0) * 1000))
    t0 = time.perf_counter()
    await c.send("block_edit", **block_edit("place", pos, original, "up"))
    got = await c.until(lambda: m.at_world(*pos) == original)
    check(got is not None, "place the original back -> block_update", ms=round((time.perf_counter() - t0) * 1000))
    t0 = time.perf_counter()
    await c.send("block_edit", **block_edit("place", above, "minecraft:gold_block", "up"))
    got = await c.until(lambda: m.at_world(*above) == "minecraft:gold_block")
    check(got is not None, "place gold_block -> block_update", pos=above,
          ms=round((time.perf_counter() - t0) * 1000))

    # A fresh connection reads the world itself: the gold block is really there. Without edit_v1 it can't edit.
    b = Client(a.url, a.token, ["rle_palette_v1"])
    await b.open()
    await b.until(lambda: b.mirror.loaded)
    check(b.mirror.loaded and b.mirror.at_world(*above) == "minecraft:gold_block", "second connection's snapshot has the gold block")
    check(CAP_EDIT not in (b.welcome.get("caps") or []), "no edit_v1 granted without asking")
    await b.send("block_edit", **block_edit("break", above))
    check(await b.until(lambda: b.errors("edit_denied", above)) is not None, "edit without edit_v1 -> edit_denied")
    await b.close()

    cases = [
        ("edit_denied", block_edit("place", above, "minecraft:stone", "up")),                 # occupied by gold
        ("out_of_region", block_edit("place", (ox - 1, oy + ly, oz), "minecraft:stone")),
        ("bad_state", block_edit("place", (above[0], above[1] + 1, above[2]), "minecraft:not_a_block")),
        ("edit_denied", block_edit("place", (above[0], above[1] + 1, above[2]), "minecraft:command_block")),
    ]
    for code, payload in cases:
        await c.send("block_edit", **payload)
        check(await c.until(lambda code=code, payload=payload: c.errors(code, payload["pos"])) is not None,
              f"{payload['op']} {payload.get('state')} at {payload['pos']} -> {code}")
    bad = c.env.wrap("block_edit", op="place", pos=list(above), state="minecraft:stone", face="sideways")
    await c.ws.send(json.dumps(bad))
    check(await c.until(lambda: c.errors("bad_message", ref_seq=bad["seq"])) is not None, "bad face -> bad_message")

    await asyncio.sleep(1.2)   # let the bucket refill
    n0 = len(c.errors("edit_denied", above))
    for _ in range(40):
        await c.send("block_edit", **block_edit("place", above, "minecraft:stone", "up"))
    await c.until(lambda: len(c.errors("rate_limited", above)) + len(c.errors("edit_denied", above)) - n0 >= 40)
    limited, passed = len(c.errors("rate_limited", above)), len(c.errors("edit_denied", above)) - n0
    check(15 <= limited <= 22 and limited + passed == 40, "burst of 40 edits: about 20 rate_limited", rate_limited=limited, passed=passed)

    await asyncio.sleep(1.1)
    t0 = time.perf_counter()
    await c.send("block_edit", **block_edit("break", above, face="up"))
    got = await c.until(lambda: m.at_world(*above) != "minecraft:gold_block" and m.at_world(*above))
    check(got is not None, "break gold_block again", now=got, ms=round((time.perf_counter() - t0) * 1000))
    await c.close()
    rep["ok"] = all(x["ok"] for x in rep["checks"])
    return rep


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default="ws://127.0.0.1:8765/tabletop")
    ap.add_argument("--token", default="")
    ap.add_argument("--report")
    a = ap.parse_args()
    rep = asyncio.run(run(a))
    if a.report:
        with open(a.report, "w") as f:
            json.dump(rep, f, indent=2)
    print("ALL PASS" if rep.get("ok") else "FAILED")
    return 0 if rep.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
