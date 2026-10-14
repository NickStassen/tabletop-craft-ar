"""Measure snapshot size and timings on real terrain: requests each region in turn and saves the raw snapshots.

Usage: python measure_regions.py [--extras] OUT_DIR "c:cx,cy,cz,dx,dy,dz,y0" [...]
--extras asks for per-block light and biomes (hello caps light_v1 + biomes_v1, PROTOCOL.md 4.4.1).
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
from tabletop_protocol import CAP_BIOMES, CAP_LIGHT, parse_region  # noqa: E402


async def main(out: str, specs: list[str], extras: bool = False) -> None:
    os.makedirs(out, exist_ok=True)
    c = Client("measure", [CAP_LIGHT, CAP_BIOMES] if extras else None)
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
                     "client_decode_ms": round(m["_decode_ms"], 1),
                     **({"light_runs": len(m["light"]["data"]) // 2, "light_bytes": len(json.dumps(m["light"], separators=(",", ":")))}
                        if m.get("light") else {}),
                     **({"biomes": len(m["biomes"]["palette"]), "biome_runs": len(m["biomes"]["data"]) // 2,
                         "biome_bytes": len(json.dumps(m["biomes"], separators=(",", ":")))} if m.get("biomes") else {})})
    print(json.dumps(rows, indent=1))
    await c.ws.close()


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if a != "--extras"]
    asyncio.run(main(args[0], args[1:], "--extras" in sys.argv[1:]))
