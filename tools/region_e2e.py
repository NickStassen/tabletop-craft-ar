"""Area-setup e2e: two headsets share one region; A resizes it small -> large -> small, B must follow.

Checks every snapshot against known blocks (a stone layer at y=-60 over x,z in [-64,63], and markers),
records snapshot bytes and server timings, and confirms over-cap requests are rejected.
Needs the e2e server running with the world prepared by e2e.sh (flat world).
"""
from __future__ import annotations

import asyncio
import json
import os
import sys
import time

import websockets

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tabletop_protocol import Envelope, WorldMirror, parse_region, validate  # noqa: E402

URL = os.environ.get("TABLETOP_URL", "ws://127.0.0.1:8765/tabletop")
MARKERS = {(3, -59, 5): "minecraft:gold_block", (-60, -59, 60): "minecraft:diamond_block", (60, -59, -60): "minecraft:emerald_block"}
SIZES = [16, 32, 64, 128, 64, 16]


class Client:
    def __init__(self, name: str) -> None:
        self.name, self.env, self.mirror, self.q = name, Envelope(), WorldMirror(), asyncio.Queue()

    async def open(self) -> None:
        self.ws = await websockets.connect(URL, max_size=8 * 1024 * 1024)
        await self.send("hello", client=self.name, protocols=[1])
        self.task = asyncio.create_task(self.reader())

    async def send(self, t: str, **kw) -> None:
        await self.ws.send(json.dumps(self.env.wrap(t, **kw)))

    async def reader(self) -> None:
        async for raw in self.ws:
            m = json.loads(raw)
            validate(m)
            if m["type"] == "chunk_snapshot":
                t0 = time.perf_counter()
                self.mirror.apply_snapshot(m)
                m["_bytes"], m["_decode_ms"] = len(raw), (time.perf_counter() - t0) * 1000
            await self.q.put(m)

    async def wait(self, *types: str, timeout: float = 30) -> dict:
        end = time.monotonic() + timeout
        while True:
            m = await asyncio.wait_for(self.q.get(), max(0.1, end - time.monotonic()))
            if m["type"] in types:
                return m


def check(mirror: WorldMirror, n: int) -> list[str]:
    errs = []
    if mirror.count("minecraft:stone") != n * n:
        errs.append(f"stone {mirror.count('minecraft:stone')} != {n * n}")
    if mirror.count("minecraft:bedrock") != n * n:
        errs.append(f"bedrock {mirror.count('minecraft:bedrock')} != {n * n}")
    ox, _, oz = mirror.origin
    for (x, y, z), want in MARKERS.items():
        inside = ox <= x < ox + n and oz <= z < oz + n
        got = mirror.at_world(x, y, z) if inside else None
        if inside and got != want:
            errs.append(f"marker {x},{y},{z} {got} != {want}")
        if not inside and mirror.count(want):
            errs.append(f"{want} present but outside region")
    return errs


async def main() -> int:
    a, b = Client("headset-A"), Client("headset-B")
    await a.open()
    await b.open()
    for c in (a, b):
        await c.wait("chunk_snapshot")
    rows, fails = [], 0
    for n in SIZES:
        t0 = time.perf_counter()
        await a.send("region_request", world="world", **parse_region(f"c:0,0,0,{n},64,{n},-64"))
        ca = await a.wait("region_changed")
        sa = await a.wait("chunk_snapshot")
        rtt = (time.perf_counter() - t0) * 1000
        cb = await b.wait("region_changed")
        sb = await b.wait("chunk_snapshot")
        errs = check(a.mirror, n) + [f"B: {e}" for e in check(b.mirror, n)]
        same = a.mirror.grid == b.mirror.grid if a.mirror.palette == b.mirror.palette else \
            [a.mirror.palette[g] for g in a.mirror.grid] == [b.mirror.palette[g] for g in b.mirror.grid]
        if not same:
            errs.append("A and B mirrors differ")
        if cb["by_session"] != ca["by_session"]:
            errs.append("by_session mismatch")
        fails += bool(errs)
        rows.append({"dims": f"{n}x64x{n}", "blocks": n * n * 64, "bytes": sa["_bytes"], "runs": len(sa["data"]) // 2,
                     "palette": len(sa["palette"]), "server_timing": sa.get("server_timing"), "request_to_snapshot_ms": round(rtt, 1),
                     "client_decode_ms": round(sa["_decode_ms"], 1), "B_bytes": sb["_bytes"], "ok": not errs, "errors": errs})
    # Over the caps: dims, then a private subscribe over the cap.
    rejects = []
    for spec in ("c:0,0,0,256,64,256,-64", "c:0,0,0,129,64,128,-64", "c:0,0,0,128,384,128,-64"):
        await a.send("region_request", world="world", **parse_region(spec))
        e = await a.wait("error", "chunk_snapshot", timeout=10)
        ok = e["type"] == "error" and e["code"] == "region_too_large" and "max_dims" in e
        fails += not ok
        rejects.append({"request": spec, "reply": e["type"], "code": e.get("code"), "message": e.get("message"),
                        "max_dims": e.get("max_dims"), "max_blocks": e.get("max_blocks"), "ok": ok})
    # region_query
    await a.send("region_query")
    info = await a.wait("region_info")
    print(json.dumps({"resizes": rows, "rejects": rejects, "region_info": info["region"], "limits": info["limits"],
                      "players": [p["name"] for p in info["players"]], "ok": fails == 0}, indent=1))
    for c in (a, b):
        await c.ws.close()
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
