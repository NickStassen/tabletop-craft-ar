"""Live mirror (snapshot + block_update) vs a fresh snapshot after console edits. Needs the e2e server running."""
import asyncio
import json
import os
import sys

import websockets

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tabletop_protocol import Envelope, WorldMirror  # noqa: E402

URL = os.environ.get("TABLETOP_URL", "ws://127.0.0.1:8765/tabletop")
FIFO = os.environ.get("SERVER_FIFO", "run/server/stdin.fifo")
# TABLETOP_CAPS=sections_v1 runs the live mirror as a sectioned client (snapshots and same-region deltas, PROTOCOL.md 4.18).
CAPS = [c for c in os.environ.get("TABLETOP_CAPS", "").split(",") if c]


def cmd(c: str) -> None:
    with open(FIFO, "w") as f:
        f.write(c + "\n")


async def mirror(secs: float, caps: list[str] | None = None):
    env, m, changes, msgs = Envelope(), WorldMirror(), 0, 0
    async with websockets.connect(URL, max_size=1 << 23) as ws:
        await ws.send(json.dumps(env.wrap("hello", client="consistency", protocols=[1], caps=caps or [])))
        loop = asyncio.get_event_loop()
        end = loop.time() + secs
        while loop.time() < end:
            try:
                msg = json.loads(await asyncio.wait_for(ws.recv(), 0.5))
            except TimeoutError:
                continue
            if msg["type"] == "chunk_snapshot":
                m.apply_snapshot(msg)
            elif msg["type"] == "block_update":
                m.apply_block_update(msg)
                changes += len(msg["changes"])
                msgs += 1
    return m, changes, msgs


async def main() -> int:
    live = asyncio.create_task(mirror(11, CAPS))
    await asyncio.sleep(2)
    for c in ("fill 0 -59 0 3 -57 3 minecraft:glass", "setblock 20 -59 21 minecraft:torch",
              "setblock 0 -58 0 minecraft:air", "fill 10 -59 10 12 -59 12 minecraft:oak_planks",
              # More than 4096 changes in one diff: the server sends a snapshot instead (a delta for sectioned clients).
              "fill 0 -50 0 31 -46 31 minecraft:stone", "fill 0 -50 0 31 -46 31 minecraft:air"):
        cmd(c)
        await asyncio.sleep(1.2)
    m, changes, msgs = await live
    f, _, _ = await mirror(1.5)
    a = [m.palette[g] for g in m.grid]
    b = [f.palette[g] for g in f.grid]
    diff = sum(1 for x, y in zip(a, b, strict=True) if x != y)
    print(f"block_update msgs={msgs} changes={changes}; live-vs-fresh differing cells: {diff}/{len(a)}; "
          f"glass={m.count('minecraft:glass')} planks={m.count('minecraft:oak_planks')} at(0,-58,0)={m.at_world(0, -58, 0)}")
    return 0 if diff == 0 else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
