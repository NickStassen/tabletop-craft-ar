"""Live block entity check (PROTOCOL.md 4.26): a client with block_entities_v1 subscribes to a box in the air while the
console places a sign (glowing, coloured), a hanging sign, a banner with patterns, a player head with a profile, a
decorated pot with sherds, a spawner, a beacon on an iron base, a campfire and a shelf with items, and a chest (which
has no data to send). The mirrored entries must match what was placed; then the sign text is changed with /data (no
event fires: the round robin must pick it up), the banner is removed, and a block event in the chunk must make an edit
show quickly. Reports how long each step took and bytes/s.

Needs a running server with the plugin; the console is driven through the server's stdin FIFO.
  SERVER_FIFO=run/blocks/stdin.fifo TABLETOP_URL=ws://127.0.0.1:8812/tabletop python block_entities_check.py [ox oy oz]
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
import time

import websockets

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tabletop_protocol import CAP_BLOCK_ENTITIES, BlockEntityMirror, Envelope, validate  # noqa: E402

URL = os.environ.get("TABLETOP_URL", "ws://127.0.0.1:8765/tabletop")
FIFO = os.environ.get("SERVER_FIFO", "run/server/stdin.fifo")
DIMS = (32, 16, 32)


def cmd(c: str) -> None:
    with open(FIFO, "w") as f:
        f.write(c + "\n")


class Link:
    def __init__(self) -> None:
        self.mirror = BlockEntityMirror()
        self.bytes = 0
        self.msgs = 0
        self.errors: list[str] = []
        self.welcome_caps = None
        self.first_at: float | None = None

    async def run(self, origin, script) -> None:
        env = Envelope()
        async with websockets.connect(URL, max_size=1 << 23) as ws:
            await ws.send(json.dumps(env.wrap("hello", client="block-entities-check", protocols=[1], caps=["rle_palette_v1", CAP_BLOCK_ENTITIES])))
            done = asyncio.Event()

            async def reader():
                next_ping = time.monotonic() + 2
                while not done.is_set():
                    if time.monotonic() >= next_ping:   # the server closes idle links after 15 s
                        await ws.send(json.dumps(env.wrap("ping", id=0)))
                        next_ping = time.monotonic() + 2
                    try:
                        raw = await asyncio.wait_for(ws.recv(), 0.25)
                    except TimeoutError:
                        continue
                    msg = json.loads(raw)
                    try:
                        validate(msg)
                    except ValueError as e:
                        self.errors.append(str(e))
                    if msg["type"] == "welcome":
                        self.welcome_caps = msg.get("caps")
                        await ws.send(json.dumps(env.wrap("subscribe", world="world", origin=list(origin), dims=list(DIMS))))
                    elif msg["type"] == "block_entities":
                        self.mirror.apply(msg)
                        self.msgs += 1
                        self.bytes += len(raw)
                        if self.first_at is None:
                            self.first_at = time.monotonic()

            task = asyncio.create_task(reader())
            try:
                await script(self)
            finally:
                done.set()
                await task


async def wait_for(pred, secs: float) -> float | None:
    t0 = time.monotonic()
    while time.monotonic() - t0 < secs:
        if pred():
            return time.monotonic() - t0
        await asyncio.sleep(0.05)
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("origin", nargs="*", type=int, default=[200, 150, 200])
    a = ap.parse_args()
    ox, oy, oz = a.origin
    x, y, z = ox + 8, oy + 4, oz + 8
    fails: list[str] = []
    results: list[str] = []

    def check(ok: bool, what: str) -> None:
        print(("PASS " if ok else "FAIL ") + what)
        if not ok:
            fails.append(what)

    async def script(link: Link) -> None:
        await asyncio.sleep(1.5)
        cmd(f"forceload add {ox} {oz} {ox + DIMS[0] - 1} {oz + DIMS[2] - 1}")
        cmd(f"fill {ox} {oy} {oz} {ox + DIMS[0] - 1} {oy + DIMS[1] - 1} {oz + DIMS[2] - 1} air")
        await asyncio.sleep(1)
        cmd(f"setblock {x} {y} {z} oak_sign[rotation=4]{{front_text:{{messages:[\"Hello\",\"Tabletop\",\"\",\"\"],color:\"blue\",has_glowing_text:1b}},"
            f"back_text:{{messages:[\"\",\"Back\",\"\",\"\"],color:\"black\"}}}}")
        cmd(f"setblock {x + 2} {y} {z} oak_hanging_sign[rotation=0,attached=false]{{front_text:{{messages:[\"Inn\",\"\",\"\",\"\"]}}}}")
        cmd(f"setblock {x + 4} {y} {z} red_banner[rotation=8]{{patterns:[{{pattern:\"minecraft:stripe_bottom\",color:\"white\"}},{{pattern:\"minecraft:creeper\",color:\"black\"}}]}}")
        cmd(f"setblock {x + 6} {y} {z} player_head[rotation=0]{{profile:{{id:[I;1,2,3,4],name:\"Steve\"}}}}")
        cmd(f"setblock {x + 8} {y} {z} decorated_pot[facing=north]{{sherds:{{back:\"minecraft:brick\",left:\"minecraft:arms_up_pottery_sherd\","
            f"right:\"minecraft:brick\",front:\"minecraft:skull_pottery_sherd\"}}}}")
        cmd(f"setblock {x + 10} {y} {z} spawner{{SpawnData:{{entity:{{id:\"minecraft:zombie\"}}}}}}")
        cmd(f"fill {x - 1} {y - 1} {z + 4} {x + 1} {y - 1} {z + 6} iron_block")
        cmd(f"setblock {x} {y} {z + 5} beacon")
        cmd(f"setblock {x + 4} {y} {z + 5} campfire[facing=north,lit=true]{{CookingTotalTimes:[I;6000,6000,6000,6000],Items:[{{Slot:0b,id:\"minecraft:beef\",count:1}},{{Slot:2b,id:\"minecraft:cod\",count:1}}]}}")
        cmd(f"setblock {x + 6} {y} {z + 5} oak_shelf[facing=north]{{Items:[{{Slot:1b,id:\"minecraft:apple\",count:1}}]}}")
        cmd(f"setblock {x + 8} {y} {z + 5} chest[facing=north]")
        t0 = time.monotonic()
        want = {"sign": 2, "banner": 1, "skull": 1, "decorated_pot": 1, "spawner": 1, "campfire": 1, "shelf": 1}
        took = await wait_for(lambda: all(len(link.mirror.of_kind(k)) >= n for k, n in want.items()), 15)
        check(took is not None, f"placed block entities arrive ({'%.2f s' % took if took is not None else 'timeout'}; kinds {sorted({e['kind'] for e in link.mirror.entries.values()})})")
        results.append(f"first entries after placing: {took}")
        e = link.mirror.entries
        sign = e.get((x, y, z))
        check(sign is not None and sign["front"]["lines"][:2] == ["Hello", "Tabletop"] and sign["front"]["color"] == "blue" and sign["front"]["glowing"]
              and sign["back"]["lines"][1] == "Back", f"sign: both sides, colour, glowing ({sign})")
        hang = e.get((x + 2, y, z))
        check(hang is not None and hang["kind"] == "sign" and hang["front"]["lines"][0] == "Inn", "hanging sign text")
        banner = e.get((x + 4, y, z))
        check(banner is not None and [(p["pattern"], p["color"]) for p in banner["patterns"]] == [("minecraft:stripe_bottom", "white"), ("minecraft:creeper", "black")],
              f"banner patterns ({banner})")
        head = e.get((x + 6, y, z))
        check(head is not None and head.get("name") == "Steve" and head.get("uuid", "").startswith("00000001-0000-0002"), f"player head profile ({head})")
        pot = e.get((x + 8, y, z))
        check(pot is not None and pot["sherds"] == ["minecraft:brick", "minecraft:arms_up_pottery_sherd", "minecraft:brick", "minecraft:skull_pottery_sherd"], f"pot sherds ({pot})")
        sp = e.get((x + 10, y, z))
        check(sp is not None and sp["entity"] == "minecraft:zombie" and sp.get("size") and abs(sp["size"][1] - 1.95) < 0.01, f"spawner mob and size ({sp})")
        camp = e.get((x + 4, y, z + 5))
        check(camp is not None and camp["items"] == ["minecraft:beef", None, "minecraft:cod", None], f"campfire items ({camp})")
        shelf = e.get((x + 6, y, z + 5))
        check(shelf is not None and shelf["items"] == [None, "minecraft:apple", None], f"shelf items ({shelf})")
        check((x + 8, y, z + 5) not in e, "a chest sends nothing (its block state says it all)")
        took = await wait_for(lambda: (link.mirror.entries.get((x, y, z + 5)) or {}).get("tier", 0) >= 1, 12)
        check(took is not None, f"beacon on a 3x3 iron base reaches tier 1 ({took})")
        # /data changes no block and fires no event: the round robin finds it (chunks with only signs, banners, heads and
        # pots every 8 x rescan-ticks, 16 s by default; this one also holds a spawner, so every rescan-ticks).
        cmd(f"data merge block {x} {y} {z} {{front_text:{{messages:[\"Changed\",\"\",\"\",\"\"]}}}}")
        took = await wait_for(lambda: (link.mirror.entries.get((x, y, z)) or {}).get("front", {}).get("lines", [""])[0] == "Changed", 20)
        check(took is not None, f"sign text changed by /data arrives with the round robin ({took})")
        results.append(f"/data sign change after: {took}")
        cmd(f"setblock {x + 4} {y} {z} air")
        took = await wait_for(lambda: (x + 4, y, z) not in link.mirror.entries, 5)
        check(took is not None, f"removed banner is removed ({took})")
        results.append(f"banner removal after: {took}")
        await asyncio.sleep(1)

    link = Link()
    asyncio.run(link.run((a.origin[0], a.origin[1], a.origin[2]), script))
    check(link.welcome_caps is not None and CAP_BLOCK_ENTITIES in link.welcome_caps, f"welcome grants {CAP_BLOCK_ENTITIES}")
    check(not link.errors, f"every message validates ({link.errors[:3]})")
    print(f"REPORT block_entities messages={link.msgs} bytes={link.bytes} entries={len(link.mirror.entries)} " + "; ".join(results))
    print("ALL PASS" if not fails else f"{len(fails)} FAILED")
    return 0 if not fails else 1


if __name__ == "__main__":
    sys.exit(main())
