"""Sound and asset delivery against a running plugin (PROTOCOL.md "Sound", "Asset delivery"; caps sound_v1, assets_v1).

Connects like the Quest (sound_v1, assets_v1, actions_v1, interact_v1 with the token), moves the shared region onto the
surface of a flat world and checks:
  1. sound_groups arrives after the registries, before the first snapshot, listing every block (stone, wood, an anvil
     at volume 0.3) with sound ids;
  2. a note block played from the headset (interact) comes back as a sound action block.note_block.* at the block
     centre with volume 3; a door opened from the headset as its door sound; TNT lit with flint and steel explodes
     with entity.generic.explode (volume 4) about 4 s later;
  3. a second client without sound_v1 gets the same actions without any sound or sound_stop, and no sound_groups;
  4. with --assets-dir (the folder the server's assets.dir points at, holding DUMMY files): the offer lists exactly
     the files there with their size and SHA-256, every file downloads in binary frames and matches, an unknown name
     is not_found, a 2 MiB length is bad_message, and a client that didn't ask for assets_v1 gets asset_denied.

  python tools/sound_check.py --url ws://127.0.0.1:8765/tabletop --token T [--assets-dir DIR] [--report out.json] [--at X,Z]
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

from tabletop_protocol import (
    ASSET_NAMES,
    CAP_ACTIONS,
    CAP_ASSETS,
    CAP_EDIT,
    CAP_ENTITIES,
    CAP_INTERACT,
    CAP_SOUND,
    Envelope,
    WorldMirror,
    block_edit,
    interact,
    parse_asset_frame,
    validate,
)


class Client:
    def __init__(self, url: str, token: str, caps: list[str]) -> None:
        self.url, self.token, self.caps = url, token, caps
        self.env, self.mirror, self.msgs, self.order = Envelope(), WorldMirror(), [], []
        self.actions: list[dict] = []
        self.frames: list[tuple[str, int, bytes]] = []
        self.welcome: dict | None = None

    async def open(self) -> None:
        self.ws = await websockets.connect(self.url, max_size=16 * 1024 * 1024)
        await self.send("hello", client="sound-check", client_version="0.6.0", protocols=[1], token=self.token, caps=self.caps)
        self.task = asyncio.create_task(self.reader())

    async def send(self, t: str, **kw) -> int:
        m = self.env.wrap(t, **kw)
        validate(m)
        await self.ws.send(json.dumps(m))
        return m["seq"]

    async def reader(self) -> None:
        async for raw in self.ws:
            if isinstance(raw, bytes):
                self.frames.append(parse_asset_frame(raw))
                continue
            m = json.loads(raw)
            validate(m)
            if len(self.order) < 12:
                self.order.append(m["type"])
            if m["type"] == "welcome":
                self.welcome = m
            elif m["type"] == "chunk_snapshot":
                self.mirror.apply_snapshot(m)
            elif m["type"] == "block_update":
                self.mirror.apply_block_update(m)
            elif m["type"] == "actions":
                self.actions += m["actions"]
            self.msgs.append(m)

    def of(self, t: str) -> list[dict]:
        return [m for m in self.msgs if m["type"] == t]

    def errors(self, ref_seq: int) -> list[dict]:
        return [m for m in self.msgs if m["type"] == "error" and m.get("ref_seq") == ref_seq]

    async def until(self, pred, timeout: float = 5.0):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            r = pred()
            if r:
                return r
            await asyncio.sleep(0.01)
        return None

    async def close(self) -> None:
        await self.ws.close()
        self.task.cancel()


def sounds(c: Client, prefix: str, since: int = 0) -> list[dict]:
    return [a for a in c.actions[since:] if a["kind"] == "sound" and a["sound"].startswith(prefix)]


async def run(a: argparse.Namespace) -> dict:
    checks: dict[str, bool] = {}
    info: dict = {}

    def check(name: str, ok: bool, detail="") -> None:
        checks[name] = bool(ok)
        print(("PASS " if ok else "FAIL ") + name + (f" ({detail})" if detail != "" else ""), flush=True)

    caps = ["rle_palette_v1", CAP_EDIT, CAP_ENTITIES, CAP_ACTIONS, CAP_INTERACT, CAP_SOUND, CAP_ASSETS]
    c = Client(a.url, a.token, caps)
    await c.open()
    await c.until(lambda: c.of("chunk_snapshot"), 15)
    wc = c.welcome["caps"] if c.welcome else []
    check("welcome grants sound_v1", CAP_SOUND in wc)
    groups = c.of("sound_groups")
    order = c.order
    check("sound_groups after the registries, before the first snapshot",
          groups and "chunk_snapshot" in order
          and order.index("block_registry") < order.index("sound_groups") < order.index("chunk_snapshot"), order)
    if groups:
        g = groups[0]
        by = {b: grp for grp in g["groups"] for b in grp["blocks"]}
        size = len(json.dumps(g))
        info["sound_groups"] = {"blocks": g["count"], "groups": len(g["groups"]), "bytes": size}
        check("sound_groups lists every block with its sounds",
              g["count"] > 1000 and by["minecraft:stone"]["break"] == "block.stone.break"
              and by["minecraft:oak_planks"]["step"] == "block.wood.step"
              and abs(by["minecraft:anvil"]["volume"] - 0.3) < 1e-6, f"{g['count']} blocks in {len(g['groups'])} groups, {size} bytes")

    x0, z0 = a.at
    await c.send("region_request", world=c.welcome["world"], origin=[x0, -64, z0], dims=[32, 64, 32])
    await c.until(lambda: c.mirror.origin == (x0, -64, z0), 15)
    await asyncio.sleep(0.5)
    ground = next(y for y in range(-64, 0) if c.mirror.at_world(x0 + 5, y, z0 + 5) == "minecraft:air")

    b = Client(a.url, a.token, ["rle_palette_v1", CAP_ACTIONS, CAP_ENTITIES])   # no sound_v1
    await b.open()
    await b.until(lambda: b.of("chunk_snapshot"), 15)

    # a note block played from the headset
    nb = [x0 + 5, ground, z0 + 5]
    await c.send("block_edit", **block_edit("place", nb, "minecraft:note_block"))
    await c.until(lambda: (c.mirror.at_world(*nb) or "").startswith("minecraft:note_block"), 5)
    n0, nb0 = len(c.actions), len(b.actions)
    await c.send("interact", **interact(nb, "up"))
    note = await c.until(lambda: sounds(c, "block.note_block.", n0), 5)
    check("a note block played from the headset is a sound action at the block centre, volume 3",
          note and note[0]["pos"] == [nb[0] + 0.5, nb[1] + 0.5, nb[2] + 0.5] and note[0]["volume"] == 3.0, note[0] if note else None)

    # a door
    door = [x0 + 8, ground, z0 + 5]
    door_state = "minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]"
    await c.send("block_edit", **block_edit("place", door, door_state))
    await c.until(lambda: (c.mirror.at_world(*door) or "").startswith("minecraft:oak_door"), 5)
    d0 = len(c.actions)
    await c.send("interact", **interact(door, "north"))
    ds = await c.until(lambda: sounds(c, "block.wooden_door.", d0), 5)
    check("a door opened from the headset plays its door sound", ds and ds[0]["sound"] == "block.wooden_door.open", ds[0] if ds else None)

    # TNT lit with flint and steel: the explosion sound about 4 s later
    tnt = [x0 + 20, ground, z0 + 20]
    await c.send("block_edit", **block_edit("place", tnt, "minecraft:tnt[unstable=false]"))
    await c.until(lambda: (c.mirror.at_world(*tnt) or "").startswith("minecraft:tnt"), 5)
    t0, lit = len(c.actions), time.monotonic()
    await c.send("interact", **interact(tnt, "up", item="minecraft:flint_and_steel"))
    boom = await c.until(lambda: sounds(c, "entity.generic.explode", t0), 8)
    check("TNT lit from the headset explodes with entity.generic.explode, volume 4",
          boom and boom[0]["volume"] == 4.0 and 0.5 <= boom[0]["pitch"] <= 1.0, f"after {time.monotonic() - lit:.1f} s" if boom else None)
    await asyncio.sleep(0.5)
    bs = [x for x in b.actions[nb0:] if x["kind"] in ("sound", "sound_stop")]
    check("a client without sound_v1 gets no sound actions and no sound_groups",
          not bs and not b.of("sound_groups") and len(b.actions) > nb0,
          f"{len(b.actions) - nb0} actions")
    info["sounds_heard"] = sorted({x["sound"] for x in c.actions if x["kind"] == "sound"})

    if a.assets_dir:
        d = pathlib.Path(a.assets_dir)
        local = {n: d / n for n in ASSET_NAMES if (d / n).is_file()}
        check("welcome grants assets_v1 (token set, LAN)", CAP_ASSETS in wc)
        offer = await c.until(lambda: c.of("assets"), 30)
        offer = offer[0] if offer else {"files": [], "chunk": 0}
        files = {f["name"]: f for f in offer["files"]}
        check("the offer lists the files in assets.dir with size and SHA-256",
              set(files) == set(local)
              and all(files[n]["size"] == p.stat().st_size and files[n]["sha256"] == hashlib.sha256(p.read_bytes()).hexdigest()
                                               for n, p in local.items()), sorted(files))
        chunk = offer["chunk"]
        speeds = {}
        for name, f in files.items():
            c.frames.clear()
            got = bytearray(f["size"])
            start = time.monotonic()
            nxt, have, inflight = 0, 0, 0
            while have < f["size"] and time.monotonic() - start < 120:
                while inflight < 4 and nxt < f["size"]:
                    ln = min(chunk, f["size"] - nxt)
                    await c.send("asset_get", name=name, offset=nxt, length=ln)
                    nxt += ln
                    inflight += 1
                while c.frames:
                    fn, off, data = c.frames.pop(0)
                    if fn == name:
                        got[off:off + len(data)] = data
                        have += len(data)
                        inflight -= 1
                await asyncio.sleep(0.001)
            secs = time.monotonic() - start
            speeds[name] = round(f["size"] / 1e6 / max(secs, 1e-3), 1)
            check(f"{name} downloads and matches its SHA-256", hashlib.sha256(bytes(got)).hexdigest() == f["sha256"],
                  f"{f['size'] / 1e6:.1f} MB in {secs:.1f} s, {speeds[name]} MB/s")
        info["asset_mb_per_s"] = speeds
        bad = c.env.wrap("asset_get", name="../server.properties", offset=0, length=1024)   # not validated: the server must refuse it
        await c.ws.send(json.dumps(bad))
        e1 = await c.until(lambda: c.errors(bad["seq"]), 3)
        check("a name that isn't offered is not_found (nothing outside the three names is read)",
              e1 and e1[0]["code"] == "not_found" and e1[0]["op"] == "asset_get")
        name0 = next(iter(files), "sounds.zip")
        s2 = await c.send("asset_get", name=name0, offset=0, length=1024)
        await c.until(lambda: c.errors(s2) or [fr for fr in c.frames if fr[0] == name0], 3)
        raw = c.env.wrap("asset_get", name=name0, offset=0, length=2 << 20)   # not validated: the server must refuse it
        await c.ws.send(json.dumps(raw))
        e2 = await c.until(lambda: c.errors(raw["seq"]), 3)
        check("a 2 MiB asset_get is bad_message", e2 and e2[0]["code"] == "bad_message")
        s3 = await b.send("asset_get", name=name0, offset=0, length=1024)
        e3 = await b.until(lambda: b.errors(s3), 3)
        check("a client without assets_v1 gets asset_denied", e3 and e3[0]["code"] == "asset_denied" and e3[0]["name"] == name0)

    for p in (nb, door, [door[0], door[1] + 1, door[2]]):
        await c.send("block_edit", **block_edit("break", p))
    await b.close()
    await c.close()
    return {"checks": checks, "info": info, "passed": sum(checks.values()), "total": len(checks)}


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--url", default="ws://127.0.0.1:8765/tabletop")
    p.add_argument("--token", default="")
    p.add_argument("--assets-dir", default=None)
    p.add_argument("--report", default=None)
    p.add_argument("--at", default="0,0", type=lambda s: tuple(int(v) for v in s.split(",")))
    a = p.parse_args()
    r = asyncio.run(run(a))
    print(f"{r['passed']}/{r['total']} checks passed; {json.dumps(r['info'])}")
    if a.report:
        pathlib.Path(a.report).write_text(json.dumps(r, indent=1))
    return 0 if r["passed"] == r["total"] else 1


if __name__ == "__main__":
    sys.exit(main())
