"""Area size presets e2e and measurements (PROTOCOL.md 4.25): a headset resizes the visible window around its view.

Headset A ("sections_v1", optionally light + biomes) asks for a window plus a 16-block view margin, then resizes it
through WIDTHS, each time centred where it was and with the origin moved by whole sections, as the Quest app's
AreaSizing.Plan does. The server answers with a resize delta (only the sections that aren't fully inside the old
region). After every step a fresh client C connects: it gets the shared region as a full snapshot, and A's
delta-assembled grid (blocks and light) is compared with it block by block. Prints, per step, request->complete ms,
delta and full sizes, sections sent and the server timings. With --save DIR, C's full snapshot (all parts, light and
biomes included) is written as DIR/<width>.json for `quest-app/Tests~ dotnet run -- --measure-batches DIR`.

Usage: TABLETOP_URL=ws://127.0.0.1:8765/tabletop python area_e2e.py [--widths 128,64,256,96] [--height 96]
       [--center 0,0] [--y0 32] [--light] [--save DIR] [--server-clamp]
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
from nav_e2e import same  # noqa: E402
from tabletop_protocol import CAP_BIOMES, CAP_LIGHT, SECTION, SECTIONS, Envelope, WorldMirror, validate  # noqa: E402

URL = os.environ.get("TABLETOP_URL", "ws://127.0.0.1:8765/tabletop")
MARGIN = 16


class Client:
    def __init__(self, name: str, caps: list[str]) -> None:
        self.name, self.caps, self.env, self.mirror, self.q = name, caps, Envelope(), WorldMirror(), asyncio.Queue()
        self.welcome: dict = {}
        self.parts: list[dict] = []   # raw parts of the last complete snapshot

    async def open(self) -> None:
        self.ws = await websockets.connect(URL, max_size=16 * 1024 * 1024)
        await self.send("hello", client=self.name, protocols=[1], caps=self.caps)
        self.task = asyncio.create_task(self.reader())
        self.pinger = asyncio.create_task(self.keepalive())

    async def keepalive(self) -> None:
        n = 0
        while True:
            await asyncio.sleep(2)
            n += 1
            await self.send("ping", id=n)

    async def close(self) -> None:
        self.pinger.cancel()
        await self.ws.close()

    async def send(self, t: str, **kw) -> None:
        await self.ws.send(json.dumps(self.env.wrap(t, **kw)))

    async def reader(self) -> None:
        parts_bytes, timing, parts = 0, None, []
        async for raw in self.ws:
            m = json.loads(raw)
            validate(m)
            if m["type"] == "welcome":
                self.welcome = m
            if m["type"] == "block_update":
                self.mirror.apply_block_update(m)
            if m["type"] == "chunk_snapshot":
                parts_bytes += len(raw)
                timing = timing or m.get("server_timing")
                parts.append(m)
                t0 = time.perf_counter()
                if not self.mirror.apply_snapshot(m):
                    continue
                self.parts, parts = parts, []
                m = {"type": "snapshot_done", "id": m["snapshot_id"], "bytes": parts_bytes, "parts": m.get("parts", 1),
                     "delta": "base" in m, "sections": self.mirror.sections_sent, "decode_ms": (time.perf_counter() - t0) * 1000,
                     "timing": timing}
                parts_bytes, timing = 0, None
            await self.q.put(m)

    async def wait(self, *types: str, timeout: float = 120) -> dict:
        end = time.monotonic() + timeout
        while True:
            m = await asyncio.wait_for(self.q.get(), max(0.1, end - time.monotonic()))
            if m["type"] in types:
                return m


def plan(mirror: WorldMirror, view: list[float], width: int, max_view: int) -> tuple[list[int], list[int]]:
    """AreaSizing.Plan: window `width` on x/z around `view`, same height and y margin, origin moved by whole sections."""
    w = max(16, min(round(width / 16) * 16, max_view))
    d = [w + 2 * MARGIN, mirror.dims[1], w + 2 * MARGIN]
    o = list(mirror.origin)
    for a in (0, 2):
        ideal = view[a] - d[a] / 2
        o[a] = mirror.origin[a] + int(round((ideal - mirror.origin[a]) / SECTION)) * SECTION
    return o, d


async def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--widths", default="128,64,256,96", help="first = starting window, then the resizes")
    ap.add_argument("--height", type=int, default=96, help="visible height (the region adds the margin on top and below)")
    ap.add_argument("--center", default="0,0")
    ap.add_argument("--y0", type=int, default=32, help="bottom of the window")
    ap.add_argument("--light", action="store_true", help="ask for light_v1 + biomes_v1 (light is compared too)")
    ap.add_argument("--save", default=None, help="write each full snapshot (parts) as DIR/<width>.json")
    ap.add_argument("--server-clamp", action="store_true", help="send widths over limits.max_view_width as asked (the server must clamp)")
    a = ap.parse_args()
    widths = [int(x) for x in a.widths.split(",")]
    cx, cz = (int(x) for x in a.center.split(","))
    caps = ["rle_palette_v1", SECTIONS, *([CAP_LIGHT, CAP_BIOMES] if a.light else [])]
    hs = Client("area-headset", caps)
    await hs.open()
    await hs.wait("snapshot_done", "error", timeout=180)
    limits = hs.welcome.get("limits", {})
    max_view = limits.get("max_view_width", 1 << 30)
    rows, fails = [], 0
    if a.save:
        os.makedirs(a.save, exist_ok=True)

    async def step(o: list[int], d: list[int], asked: int, label: str) -> None:
        nonlocal fails
        t0 = time.perf_counter()
        await hs.send("region_request", world="world", origin=o, dims=d, view_margin=[MARGIN, MARGIN, MARGIN])
        ch = await hs.wait("region_changed", "error", timeout=180)
        if ch["type"] == "error":
            rows.append({"step": label, "error": ch.get("code"), "message": ch.get("message")})
            fails += 1
            return
        sa = await hs.wait("snapshot_done", timeout=180)
        ms = (time.perf_counter() - t0) * 1000
        window = hs.mirror.dims[0] - 2 * hs.mirror.view_margin[0]
        full = Client("area-full", caps)
        t1 = time.perf_counter()
        await full.open()
        sc = await full.wait("snapshot_done", "error", timeout=180)
        full_ms = (time.perf_counter() - t1) * 1000
        match = sc["type"] == "snapshot_done" and same(hs.mirror, full.mirror, quiet=True)
        if not match and sc["type"] == "snapshot_done":
            await asyncio.sleep(1.5)   # a block_update (kelp growing, water settling) may have reached one client first
            match = same(hs.mirror, full.mirror)
        if a.save and sc["type"] == "snapshot_done":
            with open(os.path.join(a.save, f"{window}.json"), "w") as f:
                json.dump(full.parts, f)
        await full.close()
        expect = min(asked, max_view)
        ok = match and window == expect
        fails += not ok
        rows.append({"step": label, "window": window, "asked": asked, "origin": list(hs.mirror.origin),
                     "dims": "x".join(map(str, hs.mirror.dims)),
                     "request_to_snapshot_ms": round(ms, 1),
                     "delta": {"bytes": sa["bytes"], "parts": sa["parts"], "is_delta": sa["delta"], "sections_sent": sa["sections"]},
                     "full": {"bytes": sc.get("bytes"), "parts": sc.get("parts"), "connect_to_snapshot_ms": round(full_ms, 1)},
                     "server_timing": sa["timing"], "grids_match": match})

    h = a.height
    w0 = widths[0]
    d0 = [w0 + 2 * MARGIN, h + 2 * MARGIN, w0 + 2 * MARGIN]
    o0 = [cx - d0[0] // 2, a.y0 - MARGIN, cz - d0[2] // 2]
    await step(o0, d0, w0, f"start {w0}")
    view = [o0[0] + d0[0] / 2, 0, o0[2] + d0[2] / 2]
    for w in widths[1:]:
        o, d = plan(hs.mirror, view, w, 1 << 30 if a.server_clamp else max_view)
        await step(o, d, w, f"resize -> {w}")
    print(json.dumps({"limits": limits, "steps": rows, "ok": fails == 0}, indent=1))
    await hs.close()
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
