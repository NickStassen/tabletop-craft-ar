"""Live light check (PROTOCOL.md 4.4.1): a client with light_v1 + biomes_v1 mirrors a region while the console builds a
roofed room with a torch and then opens the roof; the mirrored blocks AND light must equal a fresh snapshot afterwards.

Needs a running server with the plugin; the console is driven through the server's stdin FIFO.
  SERVER_FIFO=run/light/stdin.fifo TABLETOP_URL=ws://127.0.0.1:8772/tabletop python light_check.py [ox oy oz]
"""
from __future__ import annotations

import asyncio
import json
import os
import sys
import time

import websockets

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tabletop_protocol import CAP_BIOMES, CAP_LIGHT, Envelope, WorldMirror  # noqa: E402

URL = os.environ.get("TABLETOP_URL", "ws://127.0.0.1:8765/tabletop")
FIFO = os.environ.get("SERVER_FIFO", "run/server/stdin.fifo")
DIMS = (16, 16, 16)


def cmd(c: str) -> None:
    with open(FIFO, "w") as f:
        f.write(c + "\n")


async def session(origin: tuple[int, int, int], secs: float, stats: dict | None = None) -> WorldMirror:
    env, m = Envelope(), WorldMirror()
    async with websockets.connect(URL, max_size=1 << 23) as ws:
        await ws.send(json.dumps(env.wrap("hello", client="light-check", protocols=[1], caps=["rle_palette_v1", CAP_LIGHT, CAP_BIOMES])))
        welcomed = False
        end = time.monotonic() + secs
        while time.monotonic() < end:
            try:
                raw = await asyncio.wait_for(ws.recv(), 0.5)
            except TimeoutError:
                continue
            msg = json.loads(raw)
            if msg["type"] == "welcome" and not welcomed:
                welcomed = True
                if stats is not None:
                    stats["welcome_caps"] = msg.get("caps")
                await ws.send(json.dumps(env.wrap("subscribe", world="world", origin=list(origin), dims=list(DIMS))))
            elif msg["type"] == "chunk_snapshot" and msg["origin"] == list(origin):
                m.apply_snapshot(msg)
                if stats is not None:
                    stats["snapshots"] = stats.get("snapshots", 0) + 1
            elif msg["type"] == "block_update" and m.apply_block_update(msg) and stats is not None:
                stats["updates"] = stats.get("updates", 0) + 1
                stats["block_changes"] = stats.get("block_changes", 0) + len(msg["changes"])
                stats["light_changes"] = stats.get("light_changes", 0) + len(msg.get("light") or [])
                stats["max_update_bytes"] = max(stats.get("max_update_bytes", 0), len(raw))
    return m


async def main(ox: int, oy: int, oz: int) -> int:
    o = (ox, oy, oz)
    x0, x1, z0, z1, floor, roof = ox + 2, ox + 10, oz + 2, oz + 10, oy + 4, oy + 8
    cx, cz = (x0 + x1) // 2, (z0 + z1) // 2
    cmd(f"fill {ox} {oy} {oz} {ox + 15} {oy + 15} {oz + 15} minecraft:air")
    await asyncio.sleep(1.0)
    stats: dict = {}
    live = asyncio.create_task(session(o, 14, stats))
    await asyncio.sleep(2.5)
    for c in (f"fill {x0} {floor} {z0} {x1} {floor} {z1} minecraft:stone", f"fill {x0} {roof} {z0} {x1} {roof} {z1} minecraft:stone",
              f"fill {x0} {floor + 1} {z0} {x0} {roof - 1} {z1} minecraft:stone", f"fill {x1} {floor + 1} {z0} {x1} {roof - 1} {z1} minecraft:stone",
              f"fill {x0} {floor + 1} {z0} {x1} {roof - 1} {z0} minecraft:stone", f"setblock {cx} {floor + 1} {cz} minecraft:torch"):
        cmd(c)
        await asyncio.sleep(0.4)
    await asyncio.sleep(3.0)
    mid = dict(stats)
    cmd(f"setblock {cx} {roof} {cz} minecraft:air")   # open the roof above the torch: sky light pours in
    m = await live
    fresh = await session(o, 2.5)
    blocks_diff = sum(1 for a, b in zip(m.grid, fresh.grid, strict=True) if m.palette[a] != fresh.palette[b])
    light_diff = sum(1 for a, b in zip(m.light, fresh.light, strict=True) if a != b)
    report = {
        "stats_after_room": mid, "stats_final": stats,
        "torch": m.light_at_world(cx, floor + 1, cz), "under_roof_corner": m.light_at_world(x0 + 1, floor + 1, z1 - 1),
        "below_hole": m.light_at_world(cx, roof - 1, cz), "outside": m.light_at_world(ox, oy + 15, oz),
        "biomes": m.biomes.palette if m.biomes else None,
        "live_vs_fresh": {"blocks_differing": blocks_diff, "light_differing": light_diff, "cells": len(m.grid)},
    }
    print(json.dumps(report, indent=1))
    ok = blocks_diff == 0 and light_diff == 0 and report["torch"][1] == 14 and stats.get("light_changes", 0) > 0
    print("OK" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    args = [int(v) for v in sys.argv[1:4]] if len(sys.argv) >= 4 else [200, 100, 200]
    sys.exit(asyncio.run(main(*args)))
