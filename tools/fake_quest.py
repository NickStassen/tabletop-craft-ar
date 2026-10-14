#!/usr/bin/env python3
"""Fake Quest client for the tabletop link protocol v1 (docs/PROTOCOL.md).

Connects, sends hello (+ optional avatar_config), mirrors chunk_snapshot/block_update, records
player_positions and avatar_state, sends a scripted ~30 Hz pose loop and pings every 2 s, then prints
a JSON report. --expect / --expect-count assert blocks in the final mirrored world.

  python tools/fake_quest.py --url ws://127.0.0.1:8765/tabletop --duration 10 \
      --expect 5,60,5=minecraft:gold_block --expect-count minecraft:stone=1024
"""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time

import websockets

from tabletop_protocol import CAP_BIOMES, CAP_LIGHT, Envelope, WorldMirror, parse_region, scripted_pose, validate


async def run(a: argparse.Namespace) -> dict:
    env = Envelope()
    mirror = WorldMirror()
    st: dict = {"counts": {}, "errors": [], "server_errors": [], "players": {}, "block_updates": [], "avatar_states": [],
                "poses_sent": 0, "rtt_ms": [], "snapshot": None}
    t0 = time.monotonic()
    async with websockets.connect(a.url, max_size=8 * 1024 * 1024) as ws:
        caps = ["rle_palette_v1", "avatar", *([CAP_LIGHT, CAP_BIOMES] if a.extras else [])]
        await ws.send(json.dumps(env.wrap("hello", client="fake-quest", client_version="0.1.0", protocols=[1],
                                          token=a.token, caps=caps)))
        welcomed = asyncio.Event()

        async def sender() -> None:
            await asyncio.wait_for(welcomed.wait(), 10)
            if a.subscribe:
                ox, oy, oz, dx, dy, dz = (int(v) for v in a.subscribe.split(","))
                await ws.send(json.dumps(env.wrap("subscribe", world="world", origin=[ox, oy, oz], dims=[dx, dy, dz])))
            if a.query is not None:
                await ws.send(json.dumps(env.wrap("region_query", **({"player": a.query} if a.query else {}))))
            if a.region:
                await ws.send(json.dumps(env.wrap("region_request", world="world", **parse_region(a.region))))
            if a.avatar:
                await ws.send(json.dumps(env.wrap("avatar_config", enabled=True, block_size_m=a.block_size, max_scale=16.0,
                                                  show_hands=True, skin_player=a.skin or None, name_tag="Tabletop Giant")))
            start = time.monotonic()
            next_ping = start
            ping_id = 0
            while time.monotonic() - t0 < a.duration:
                now = time.monotonic()
                if not a.no_pose:
                    await ws.send(json.dumps(env.wrap("pose", **scripted_pose(now - start))))
                    st["poses_sent"] += 1
                if now >= next_ping:
                    await ws.send(json.dumps(env.wrap("ping", id=ping_id)))
                    ping_id += 1
                    next_ping = now + 2.0
                await asyncio.sleep(1.0 / a.pose_hz)
            await ws.send(json.dumps(env.wrap("bye", reason="fake-quest done")))

        async def receiver() -> None:
            while time.monotonic() - t0 < a.duration:
                try:
                    raw = await asyncio.wait_for(ws.recv(), timeout=0.5)
                except TimeoutError:
                    continue
                except websockets.ConnectionClosed:
                    return
                m = json.loads(raw)
                t = m.get("type")
                st["counts"][t] = st["counts"].get(t, 0) + 1
                try:
                    validate(m)
                except ValueError as e:
                    st["errors"].append(str(e))
                    continue
                if t == "welcome":
                    st["welcome"] = {k: m[k] for k in ("server", "server_version", "mc_version", "world", "region", "limits")}
                    welcomed.set()
                elif t == "chunk_snapshot":
                    mirror.apply_snapshot(m)
                    st["snapshot"] = {"server_timing": m.get("server_timing"),
                                      "id": m["snapshot_id"], "origin": m["origin"], "dims": m["dims"], "bytes": len(raw),
                                      "palette": len(m["palette"]), "runs": len(m["data"]) // 2,
                                      "at_ms": round((time.monotonic() - t0) * 1000),
                                      "light_runs": len(m["light"]["data"]) // 2 if m.get("light") else None,
                                      "biomes": m["biomes"]["palette"] if m.get("biomes") else None}
                elif t == "region_changed":
                    st.setdefault("region_changes", []).append({k: m[k] for k in ("origin", "dims", "by_session")})
                elif t == "region_info":
                    st["region_info"] = {"region": m["region"], "players": m["players"], "limits": m["limits"]}
                elif t == "block_update":
                    if mirror.apply_block_update(m):
                        st["light_updates"] = st.get("light_updates", 0) + len(m.get("light") or [])
                        for x, y, z, p in m["changes"][:20]:
                            st["block_updates"].append([x, y, z, mirror.palette[p]])
                elif t == "player_positions":
                    for p in m["players"]:
                        r = st["players"].setdefault(p["name"], {"first": p["pos"], "updates": 0})
                        r["last"] = p["pos"]
                        r["updates"] += 1
                elif t == "avatar_state":
                    st["avatar_states"].append({k: m.get(k) for k in ("enabled", "scale", "world_height_blocks", "note", "debug")})
                elif t == "pong":
                    st["rtt_ms"].append(int(time.time() * 1000) - m["echo_t"])
                elif t == "error":
                    st["server_errors"].append(f"{m['code']}: {m['message']}")
                if a.verbose and t not in ("player_positions", "pong"):
                    print("<-", t, raw[:200], flush=True)

        await asyncio.gather(sender(), receiver())

    checks = []
    if mirror.loaded:
        for spec in a.expect:
            pos, want = spec.split("=", 1)
            x, y, z = (int(v) for v in pos.split(","))
            got = mirror.at_world(x, y, z)
            checks.append({"pos": [x, y, z], "want": want, "got": got, "ok": got.split("[")[0] == want})
        for spec in a.expect_light:
            pos, want = spec.split("=", 1)
            x, y, z = (int(v) for v in pos.split(","))
            got = list(mirror.light_at_world(x, y, z)) if mirror.light is not None else None
            checks.append({"light": [x, y, z], "want": want, "got": got, "ok": got == [int(v) for v in want.split(",")]})
        for spec in a.expect_count:
            name, n = spec.rsplit("=", 1)
            got = mirror.count(name)
            checks.append({"count": name, "want": int(n), "got": got, "ok": got == int(n)})
    st["checks"] = checks
    heads = [s["debug"]["head"] for s in st["avatar_states"] if s.get("debug") and s["debug"].get("head")]
    if heads:
        st["avatar_head_range"] = {"x": [min(h[0] for h in heads), max(h[0] for h in heads)],
                                   "y": [min(h[1] for h in heads), max(h[1] for h in heads)],
                                   "z": [min(h[2] for h in heads), max(h[2] for h in heads)], "samples": len(heads)}
    st["avatar_states"] = st["avatar_states"][:1] + st["avatar_states"][-2:]
    st["block_updates"] = st["block_updates"][:20]
    if st["rtt_ms"]:
        st["rtt_ms"] = {"n": len(st["rtt_ms"]), "min": min(st["rtt_ms"]), "max": max(st["rtt_ms"])}
    st["ok"] = mirror.loaded and not st["errors"] and all(c["ok"] for c in checks)
    return st


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default="ws://127.0.0.1:8765/tabletop")
    ap.add_argument("--token", default="")
    ap.add_argument("--duration", type=float, default=10.0)
    ap.add_argument("--pose-hz", type=float, default=30.0)
    ap.add_argument("--no-pose", action="store_true")
    ap.add_argument("--avatar", action="store_true", help="send avatar_config enabled=true first")
    ap.add_argument("--block-size", type=float, default=0.01)
    ap.add_argument("--skin", default="")
    ap.add_argument("--subscribe", help="ox,oy,oz,dx,dy,dz")
    ap.add_argument("--region", help="region_request: ox,oy,oz,dx,dy,dz or c:cx,cy,cz,dx,dy,dz[,y_origin]")
    ap.add_argument("--query", nargs="?", const="", help="send region_query (optionally for one player name)")
    ap.add_argument("--expect", action="append", default=[], help="x,y,z=minecraft:block (world coords)")
    ap.add_argument("--expect-count", action="append", default=[], help="minecraft:block=N in the region")
    ap.add_argument("--extras", action="store_true", help="ask for per-block light + biomes (hello caps light_v1, biomes_v1)")
    ap.add_argument("--expect-light", action="append", default=[], help="x,y,z=sky,block (world coords; needs --extras)")
    ap.add_argument("--report")
    ap.add_argument("-v", "--verbose", action="store_true")
    a = ap.parse_args()
    st = asyncio.run(run(a))
    text = json.dumps(st, indent=2, default=str)
    if a.report:
        with open(a.report, "w") as f:
            f.write(text)
    print(text)
    return 0 if st["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
