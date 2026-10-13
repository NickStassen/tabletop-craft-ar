"""Measure snapshot size and timings on real terrain: requests each region in turn and saves the raw snapshots.

Usage: python measure_regions.py OUT_DIR "c:cx,cy,cz,dx,dy,dz,y0" [...]
Each snapshot is saved as OUT_DIR/<dx>x<dy>x<dz>.json for the C# greedy-mesh measurement
(quest-app/Tests~: dotnet run -- --measure OUT_DIR).
"""
from __future__ import annotations

import asyncio
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from region_e2e import Client  # noqa: E402
from tabletop_protocol import parse_region  # noqa: E402


async def main(out: str, specs: list[str]) -> None:
    os.makedirs(out, exist_ok=True)
    c = Client("measure")
    await c.open()
    await c.wait("welcome")
    await c.wait("chunk_snapshot")
    rows = []
    for spec in specs:
        t0 = time.perf_counter()
        await c.send("region_request", world="world", **parse_region(spec))
        m = await c.wait("chunk_snapshot", "error", timeout=120)
        if m["type"] == "error":
            rows.append({"spec": spec, "error": m.get("code")})
            continue
        dims = "x".join(map(str, m["dims"]))
        with open(os.path.join(out, dims + ".json"), "w") as f:
            json.dump(m, f)
        rows.append({"dims": dims, "bytes": m["_bytes"], "palette": len(m["palette"]), "runs": len(m["data"]) // 2,
                     "server_timing": m.get("server_timing"), "request_to_snapshot_ms": round((time.perf_counter() - t0) * 1000, 1),
                     "client_decode_ms": round(m["_decode_ms"], 1)})
    print(json.dumps(rows, indent=1))
    await c.ws.close()


if __name__ == "__main__":
    asyncio.run(main(sys.argv[1], sys.argv[2:]))
