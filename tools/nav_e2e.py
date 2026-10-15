"""Navigation e2e and measurements (PROTOCOL.md 4.17-4.18): a headset glides across the world section by section.

Headset A advertises "sections_v1", asks for a window plus a view margin, then shifts the region by 16 blocks STEPS
times (east, then south). Client B is a v1 client (no caps) on the same shared region: it gets every region as one
full rle_palette_v1 snapshot, so each delta-assembled grid of A is compared block by block with B's. Prints, per
step, request->complete ms, bytes, parts and sections sent. Optionally (--big) requests a large area to measure a
multi-part sectioned snapshot against the v1 size.

Usage: TABLETOP_URL=ws://127.0.0.1:8765/tabletop python nav_e2e.py [--window 128,96,128] [--margin 16] [--steps 6]
       [--center 0,64,0] [--y0 32] [--big 256,128,256] [--fresh] [--churn tools/run/server/stdin.fifo]

--fresh makes B read every region itself (a private subscribe), so the plugin's incremental reads (only the new
sections are read, the rest copied from the old region) are checked against the world; --churn changes blocks near
the centre while the region moves. request_to_last_part_ms is the time until the last part arrived (before the slow
Python decode).
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
from tabletop_protocol import CAP_BIOMES, CAP_LIGHT, SECTIONS, Envelope, WorldMirror, validate  # noqa: E402

URL = os.environ.get("TABLETOP_URL", "ws://127.0.0.1:8765/tabletop")


class Client:
    def __init__(self, name: str, caps: list[str]) -> None:
        self.name, self.caps, self.env, self.mirror, self.q = name, caps, Envelope(), WorldMirror(), asyncio.Queue()
        self.welcome: dict = {}

    async def open(self) -> None:
        self.ws = await websockets.connect(URL, max_size=16 * 1024 * 1024)
        await self.send("hello", client=self.name, protocols=[1], caps=self.caps)
        self.task = asyncio.create_task(self.reader())
        self.pinger = asyncio.create_task(self.keepalive())

    async def keepalive(self) -> None:
        """The server closes a link after 15 s of silence; chunk generation for a big region can take longer."""
        n = 0
        while True:
            await asyncio.sleep(2)
            n += 1
            await self.send("ping", id=n)

    async def send(self, t: str, **kw) -> None:
        await self.ws.send(json.dumps(self.env.wrap(t, **kw)))

    async def reader(self) -> None:
        parts_bytes, timing = 0, None
        async for raw in self.ws:
            m = json.loads(raw)
            validate(m)
            if m["type"] == "welcome":
                self.welcome = m
            if m["type"] == "block_update":
                self.mirror.apply_block_update(m)   # deltas are against the snapshot *plus* its block updates
            if m["type"] == "chunk_snapshot":
                parts_bytes += len(raw)
                timing = timing or m.get("server_timing")
                t0 = time.perf_counter()
                done = self.mirror.apply_snapshot(m)
                if not done:
                    continue
                m = {"type": "snapshot_done", "id": m["snapshot_id"], "bytes": parts_bytes, "parts": m.get("parts", 1),
                     "encoding": m["encoding"], "delta": "base" in m, "sections": self.mirror.sections_sent,
                     "decode_ms": (time.perf_counter() - t0) * 1000, "timing": timing, "view_margin": m.get("view_margin"),
                     "arrived": t0}   # [nav] when the last part arrived, before this (slow, Python) decode
                parts_bytes, timing = 0, None
            await self.q.put(m)

    async def wait(self, *types: str, timeout: float = 60) -> dict:
        end = time.monotonic() + timeout
        while True:
            m = await asyncio.wait_for(self.q.get(), max(0.1, end - time.monotonic()))
            if m["type"] in types:
                return m


def same(a: WorldMirror, b: WorldMirror, quiet: bool = False) -> bool:
    if a.origin != b.origin or a.dims != b.dims:
        print(f"MISMATCH region {a.origin} {a.dims} vs {b.origin} {b.dims}", file=sys.stderr)
        return False
    diff = [i for i, (x, y) in enumerate(zip(a.grid, b.grid, strict=True)) if a.palette[x] != b.palette[y]]
    if (a.light is None) != (b.light is None):
        print("MISMATCH one mirror has light, the other doesn't", file=sys.stderr)
        return False
    if a.light is not None:
        diff += [i for i, (x, y) in enumerate(zip(a.light, b.light, strict=True)) if x != y]
    if quiet:
        return not diff
    dx, _, dz = a.dims
    for i in diff[:10]:
        x, z, y = i % dx, (i // dx) % dz, i // (dx * dz)
        w = (a.origin[0] + x, a.origin[1] + y, a.origin[2] + z)
        print(f"MISMATCH at {w} section {(x // 16, y // 16, z // 16)}: headset {a.palette[a.grid[i]]} v1 {b.palette[b.grid[i]]}",
              file=sys.stderr)
    if diff:
        print(f"MISMATCH {len(diff)} cells differ", file=sys.stderr)
    return not diff


def save(w: WorldMirror, out: str) -> None:
    """The mirror as one rle_palette_v1 snapshot, whatever its size (the C# measurement reads this format)."""
    os.makedirs(out, exist_ok=True)
    data: list[int] = []
    g = w.grid
    i = 0
    while i < len(g):
        j = i + 1
        while j < len(g) and g[j] == g[i]:
            j += 1
        data += [g[i], j - i]
        i = j
    msg = {"v": 1, "type": "chunk_snapshot", "seq": 0, "t": 0, "snapshot_id": w.snapshot_id, "world": w.world, "origin": list(w.origin),
           "dims": list(w.dims), "view_margin": list(w.view_margin), "order": "yzx", "palette": w.palette, "encoding": "rle_palette_v1",
           "data": data, "biomes": None, "light": None}
    with open(os.path.join(out, "x".join(map(str, w.dims)) + ".json"), "w") as f:
        json.dump(msg, f)


def three(s: str) -> list[int]:
    v = [int(x) for x in s.split(",")]
    if len(v) != 3:
        raise argparse.ArgumentTypeError("need x,y,z")
    return v


async def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--window", type=three, default=[128, 96, 128])
    ap.add_argument("--margin", type=int, default=16)
    ap.add_argument("--margin-y", type=int, default=None, help="y margin (default: --margin)")
    ap.add_argument("--steps", type=int, default=6)
    ap.add_argument("--center", type=three, default=[0, 64, 0])
    ap.add_argument("--y0", type=int, default=32, help="bottom of the window")
    ap.add_argument("--big", type=three, default=None, help="also measure one large sectioned snapshot")
    ap.add_argument("--save", default=None, help="write the initial and big regions as rle_palette_v1 JSON (for Tests~ --measure)")
    ap.add_argument("--no-v1", action="store_true", help="headset only: realistic latency, no cross-check")
    ap.add_argument("--light", action="store_true", help="both clients ask for light_v1 + biomes_v1 (light is cross-checked too)")
    ap.add_argument("--fresh", action="store_true",
                    help="the v1 client reads every region itself (private subscribe) instead of sharing the headset's read")
    ap.add_argument("--churn", metavar="FIFO", default=None,
                    help="server stdin FIFO: setblock near the centre every 0.15 s while gliding (changes during moves)")
    a = ap.parse_args()
    extras = [CAP_LIGHT, CAP_BIOMES] if a.light else []
    hs = Client("nav-headset", ["rle_palette_v1", SECTIONS, *extras])
    v1 = None if a.no_v1 else Client("nav-v1", ["rle_palette_v1", *extras])
    clients = [c for c in (hs, v1) if c]
    for c in clients:
        await c.open()
        await c.wait("snapshot_done", "error", timeout=180)
    if v1 and a.fresh:   # leave the shared region: from now on the v1 client only gets what it subscribes to
        await v1.send("subscribe", world="world", origin=list(v1.mirror.origin), dims=list(v1.mirror.dims))
        await v1.wait("snapshot_done", "error", timeout=180)
    caps = hs.welcome.get("caps", [])
    m = a.margin
    my = m if a.margin_y is None else a.margin_y   # the headset keeps 16 on y while x/z grow with the width
    vm = [m, my, m]
    dims = [a.window[0] + 2 * m, a.window[1] + 2 * my, a.window[2] + 2 * m]
    origin = [a.center[0] - dims[0] // 2, a.y0 - my, a.center[2] - dims[2] // 2]
    rows, fails = [], 0
    churning = asyncio.Event()   # set while --churn may change blocks

    async def request(o: list[int], d: list[int], label: str) -> None:
        nonlocal fails
        t0 = time.perf_counter()
        await hs.send("region_request", world="world", origin=o, dims=d, view_margin=vm)
        ch = await hs.wait("region_changed", "error", timeout=180)
        if ch["type"] == "error":
            rows.append({"step": label, "error": ch.get("code"), "message": ch.get("message")})
            fails += 1
            return
        sa = await hs.wait("snapshot_done", timeout=180)
        ms = (time.perf_counter() - t0) * 1000
        if v1 and a.fresh:
            # [nav] An independent full read of the same region (a private subscribe), so a delta built from an
            # incremental server read is checked against the world itself, not against the same merged data.
            await v1.send("subscribe", world="world", origin=list(hs.mirror.origin), dims=list(hs.mirror.dims))
        sb = await v1.wait("snapshot_done", "error", timeout=120) if v1 else {"type": "skipped", "code": "skipped", "message": "--no-v1"}
        v1_ok = sb["type"] == "snapshot_done"   # a v1 client can't take a frame over 4 MiB: it gets region_too_large instead
        margins = list(ch.get("view_margin", [])) == vm and list(hs.mirror.view_margin) == vm
        if churning.is_set() and v1_ok:
            churning.clear()            # hold the churn and let both clients' block_updates settle before comparing
            await asyncio.sleep(1.5)
        match = not v1_ok or same(hs.mirror, v1.mirror, quiet=True)
        if a.churn:
            churning.set()
            await asyncio.sleep(0.5)    # a few changes right before the next move (its base is older than them)
        if not match:
            await asyncio.sleep(1.5)   # a block_update (e.g. kelp growing) may have reached one client and not yet the other
            match = same(hs.mirror, v1.mirror)
        ok = match and margins
        fails += not ok
        rows.append({"step": label, "origin": list(hs.mirror.origin), "dims": "x".join(map(str, hs.mirror.dims)),
                     "request_to_snapshot_ms": round(ms, 1), "request_to_last_part_ms": round((sa["arrived"] - t0) * 1000, 1),
                     "sections_v1": {"bytes": sa["bytes"], "parts": sa["parts"], "delta": sa["delta"], "sections": sa["sections"],
                                     "decode_ms_py": round(sa["decode_ms"], 1)},
                     "v1_bytes": sb["bytes"] if v1_ok else f"{sb.get('code')}: {sb.get('message')}", "server_timing": sa["timing"],
                     "grids_match": ok if v1_ok else None})
        if a.save and label in ("initial", "big"):
            save(hs.mirror, a.save)

    churn = None
    if a.churn:
        # [nav] Blocks change while the region moves (console setblock through the server's stdin FIFO): the copied
        # sections of an incremental read are older than the world, and the re-diff after each move must catch up.
        churning.set()

        async def churner() -> None:
            n = 0
            while True:
                await churning.wait()
                # A gold block set and cleared again (commands fire no block events: the safety diff finds these), and
                # sand dropped from the sky (it lands with an EntityChangeBlockEvent, so the region is marked dirty).
                k = n // 2
                x, z = a.center[0] + (k % 24) - 12, a.center[2] + (k * 7 % 24) - 12
                top = a.y0 + a.window[1] - 8
                lines = [f"setblock {x} {top} {z} {'minecraft:gold_block' if n % 2 == 0 else 'minecraft:air'}"]
                if n % 4 == 0:
                    lines.append(f"setblock {x + 1} {top} {z} minecraft:sand")
                with open(a.churn, "w") as f:
                    f.write("".join(line + "\n" for line in lines))
                n += 1
                await asyncio.sleep(0.15)
        churn = asyncio.create_task(churner())

    await request(origin, dims, "initial")
    for i in range(a.steps):
        axis = 0 if i < (a.steps + 1) // 2 else 2
        origin = list(origin)
        origin[axis] += 16
        await request(origin, dims, f"shift {'xz'[axis // 2]}+16")
    big = None
    if a.big:
        bo = [a.center[0] - a.big[0] // 2, a.y0 - my, a.center[2] - a.big[2] // 2]
        await request(bo, list(a.big), "big")
        big = rows[-1]
    if churn:
        churn.cancel()
    print(json.dumps({"server_caps": caps, "window": a.window, "margin": m, "steps": rows, "ok": fails == 0}, indent=1))
    if big:
        print("BIG", json.dumps(big))
    for c in clients:
        c.pinger.cancel()
        await c.ws.close()
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
