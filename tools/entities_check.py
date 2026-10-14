"""Live entities and actions check (PROTOCOL.md 4.21, 4.22): a client with entities_v1 + actions_v1 mirrors a region
while the console builds a pond, puts a boat with a pig passenger next to it, summons a few still mobs (NoAI), a zombie
with a sword, a dropped item and an item frame, moves a cow in steps, kills the item, damages the sheep and (with --tick)
blows up some floor with TNT. The mirrored entities must match what was summoned (types, positions, the boat/pig vehicle
link, equipment, the removal), the hurt and break actions must arrive, and the report gives message counts and bytes/s.
With --load N it then summons N more mobs to measure bytes/s and the plugin's ENTITIES log line (main-thread ms); add
--tick to forceload the region first, so its entities tick (wander, fall, float, explode) without a player online.

Needs a running server with the plugin; the console is driven through the server's stdin FIFO.
  SERVER_FIFO=run/entities/stdin.fifo TABLETOP_URL=ws://127.0.0.1:8777/tabletop python entities_check.py [ox oy oz] [--load 200]
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
from tabletop_protocol import CAP_ACTIONS, CAP_ENTITIES, EntityMirror, Envelope, validate  # noqa: E402

URL = os.environ.get("TABLETOP_URL", "ws://127.0.0.1:8765/tabletop")
FIFO = os.environ.get("SERVER_FIFO", "run/server/stdin.fifo")
DIMS = (24, 16, 24)


def cmd(c: str) -> None:
    with open(FIFO, "w") as f:
        f.write(c + "\n")


class Link:
    def __init__(self) -> None:
        self.mirror = EntityMirror()
        self.bytes = 0
        self.msgs = 0
        self.max_bytes = 0
        self.errors: list[str] = []
        self.welcome_caps = None
        self.window: list[tuple[float, int]] = []   # (time, bytes) of entities messages
        self.actions: list[dict] = []
        self.action_bytes = 0

    async def run(self, origin: tuple[int, int, int], secs: float) -> None:
        env = Envelope()
        async with websockets.connect(URL, max_size=1 << 23) as ws:
            caps = ["rle_palette_v1", CAP_ENTITIES, CAP_ACTIONS]
            await ws.send(json.dumps(env.wrap("hello", client="entities-check", protocols=[1], caps=caps)))
            end = time.monotonic() + secs
            next_ping = time.monotonic() + 2
            while time.monotonic() < end:
                if time.monotonic() >= next_ping:   # the server closes idle links after 15 s
                    await ws.send(json.dumps(env.wrap("ping", id=0)))
                    next_ping = time.monotonic() + 2
                try:
                    raw = await asyncio.wait_for(ws.recv(), 0.5)
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
                elif msg["type"] == "entities":
                    self.mirror.apply(msg)
                    self.msgs += 1
                    self.bytes += len(raw)
                    self.max_bytes = max(self.max_bytes, len(raw))
                    self.window.append((time.monotonic(), len(raw)))
                elif msg["type"] == "actions":
                    self.actions.extend(msg["actions"])
                    self.action_bytes += len(raw)

    def rate(self, secs: float) -> float:
        now = time.monotonic()
        return sum(b for t, b in self.window if now - t <= secs) / secs


def near(a: list[float], b: tuple[float, float, float], eps: float) -> bool:
    return all(abs(x - y) <= eps for x, y in zip(a, b, strict=True))


async def main(ox: int, oy: int, oz: int, load: int, tick: bool) -> int:
    link = Link()
    secs = 26 + (40 if load else 0)
    task = asyncio.create_task(link.run((ox, oy, oz), secs))
    if tick:
        cmd(f"forceload add {ox} {oz} {ox + DIMS[0] - 1} {oz + DIMS[2] - 1}")
    await asyncio.sleep(2.5)
    fy = oy + 4   # floor
    for c in (f"kill @e[type=!player,x={ox},y={oy},z={oz},dx={DIMS[0]},dy={DIMS[1]},dz={DIMS[2]}]",
              f"fill {ox} {oy} {oz} {ox + 23} {oy + 15} {oz + 15} minecraft:air",
              f"fill {ox} {oy} {oz + 16} {ox + 23} {oy + 15} {oz + 23} minecraft:air",
              f"fill {ox + 1} {fy} {oz + 1} {ox + 22} {fy} {oz + 22} minecraft:stone",
              f"fill {ox + 1} {fy + 1} {oz + 1} {ox + 9} {fy + 1} {oz + 9} minecraft:stone",
              f"fill {ox + 2} {fy + 1} {oz + 2} {ox + 8} {fy + 1} {oz + 8} minecraft:water",
              f"fill {ox + 12} {fy + 1} {oz + 20} {ox + 20} {fy + 4} {oz + 20} minecraft:stone"):
        cmd(c)
        await asyncio.sleep(0.4)
    await asyncio.sleep(1.0)
    boat = (ox + 5.5, fy + 1.0, oz + 15.5)   # on land: with no player online the region's chunks don't tick (no floating)
    cow = (ox + 14.5, fy + 1.0, oz + 6.5)
    sheep = (ox + 16.5, fy + 1.0, oz + 10.5)
    zombie = (ox + 12.5, fy + 1.0, oz + 14.5)
    item = (ox + 18.5, fy + 1.2, oz + 4.5)
    frame = (ox + 16, fy + 2, oz + 19)
    pig = '{id:"minecraft:pig",Age:-24000,NoAI:1b}'
    cmd(f"summon minecraft:oak_boat {boat[0]} {boat[1]} {boat[2]} {{Rotation:[90f,0f],Passengers:[{pig}]}}")
    cmd(f"summon minecraft:cow {cow[0]} {cow[1]} {cow[2]} {{NoAI:1b,Rotation:[45f,0f]}}")
    cmd(f"summon minecraft:sheep {sheep[0]} {sheep[1]} {sheep[2]} {{NoAI:1b,Color:3b}}")
    cmd(f"summon minecraft:zombie {zombie[0]} {zombie[1]} {zombie[2]} {{NoAI:1b,PersistenceRequired:1b}}")
    await asyncio.sleep(0.5)
    near_zombie = f"@e[type=minecraft:zombie,x={zombie[0]},y={zombie[1]},z={zombie[2]},distance=..1,limit=1]"
    cmd(f"item replace entity {near_zombie} weapon.mainhand with minecraft:iron_sword")
    cmd(f'summon minecraft:item {item[0]} {item[1]} {item[2]} {{Item:{{id:"minecraft:diamond",count:3}},PickupDelay:32767}}')
    compass = '{id:"minecraft:compass",count:1}'
    cmd(f"summon minecraft:item_frame {frame[0]} {frame[1]} {frame[2]} {{Facing:2b,Item:{compass},ItemRotation:2b}}")
    await asyncio.sleep(3.0)
    m = link.mirror
    snap = {k: json.loads(json.dumps(v)) for k, v in m.entities.items()}
    # Move the cow in 0.5 block steps: each step must reach the client as a delta.
    for i in range(1, 7):
        cmd(f"tp @e[type=minecraft:cow,limit=1,sort=nearest,x={cow[0]},y={cow[1]},z={cow[2]}] {cow[0] + 0.5 * i} {cow[1]} {cow[2]}")
        await asyncio.sleep(0.5)
    await asyncio.sleep(1.0)
    cow_after = [e["pos"] for e in m.of_type("minecraft:cow")]
    cmd(f"kill @e[type=minecraft:item,x={item[0]},y={item[1]},z={item[2]},distance=..3]")
    cmd(f"damage @e[type=minecraft:sheep,limit=1,sort=nearest,x={sheep[0]},y={sheep[1]},z={sheep[2]}] 1")
    if tick:   # TNT only explodes in ticking chunks: its broken floor blocks come as "break" actions
        cmd(f"summon minecraft:tnt {ox + 20.5} {fy + 1} {oz + 3.5} {{fuse:0}}")
    await asyncio.sleep(1.5)
    idle_rate = link.rate(3.0)
    items_after_kill = sum(1 for e in m.of_type("minecraft:item") if e.get("item") == "minecraft:diamond")   # TNT drops others
    load_report = None
    if load:
        before = (link.msgs, link.bytes)
        for i in range(load):
            x, z = ox + 2 + (i * 7) % 20, oz + 2 + (i * 11) % 20
            ai = "" if tick else "{NoAI:1b}"   # ticking: they wander, so positions change all the time
            cmd(f"summon minecraft:{('chicken', 'cow', 'pig', 'sheep')[i % 4]} {x + 0.5} {fy + (1 if tick else 6 + i % 5)} {z + 0.5} {ai}")
            if i % 20 == 19:
                await asyncio.sleep(0.2)
        await asyncio.sleep(5)
        cmd("tabletop entities")
        await asyncio.sleep(30)
        load_report = {"entities_held": len(m.entities), "rate_bytes_per_s_last5s": round(link.rate(5.0)),
                       "msgs": link.msgs - before[0], "bytes": link.bytes - before[1]}
        cmd(f"kill @e[type=!player,x={ox},y={oy},z={oz},dx={DIMS[0]},dy={DIMS[1]},dz={DIMS[2]}]")
    await task
    if tick:
        cmd(f"forceload remove {ox} {oz} {ox + DIMS[0] - 1} {oz + DIMS[2] - 1}")

    def find(t: str) -> dict | None:
        found = [e for e in snap.values() if e["type"] == t]
        return found[0] if found else None

    b, p, c, s, z, it, fr = (find(t) for t in ("minecraft:oak_boat", "minecraft:pig", "minecraft:cow", "minecraft:sheep",
                                                "minecraft:zombie", "minecraft:item", "minecraft:item_frame"))
    checks = {
        "welcome grants entities_v1": CAP_ENTITIES in (link.welcome_caps or []),
        "boat at summon x/z": b is not None and near(b["pos"][::2], boat[::2], 0.3),
        "boat carries the pig": b is not None and p is not None and b.get("passengers") == [p["id"]] and p.get("vehicle") == b["id"],
        "pig is a baby": p is not None and p.get("baby") is True,
        "cow at summon pos": c is not None and near(c["pos"], cow, 0.05),
        "cow yaw 45": c is not None and abs(c["yaw"] - 45) < 1.5,
        "sheep light_blue": s is not None and s.get("color") == "light_blue",
        "zombie holds iron sword": z is not None and (z.get("equipment") or {}).get("main_hand") == "minecraft:iron_sword",
        "item is 3 diamonds": it is not None and it.get("item") == "minecraft:diamond" and it.get("count") == 3,
        "frame faces north with compass": fr is not None and fr.get("facing") == "north" and fr.get("item") == "minecraft:compass",
        "cow move reached client": bool(cow_after) and near(cow_after[0], (cow[0] + 3.0, cow[1], cow[2]), 0.05),
        "killed item removed": items_after_kill == 0,
        "welcome grants actions_v1": CAP_ACTIONS in (link.welcome_caps or []),
        "sheep hurt action": s is not None and any(a["kind"] == "hurt" and a["id"] == s["id"] for a in link.actions),
        "explosion break actions": not tick or any(a["kind"] == "break" and a["block"] == "minecraft:stone" for a in link.actions),
        "no protocol errors": not link.errors,
    }
    report = {"checks": checks, "messages": link.msgs, "fulls": m.fulls, "bytes": link.bytes, "max_msg_bytes": link.max_bytes,
              "bytes_per_s_idle_scene": round(idle_rate), "entities_seen": {e["type"]: e["pos"] for e in snap.values()},
              "actions": {k: sum(1 for a in link.actions if a["kind"] == k) for k in ("swing", "dig", "break", "place", "hurt")},
              "action_bytes": link.action_bytes, "load": load_report, "errors": link.errors[:5]}
    print(json.dumps(report, indent=1))
    ok = all(checks.values())
    print("OK" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("origin", nargs="*", type=int, default=[200, 100, 200])
    ap.add_argument("--load", type=int, default=0, help="also summon this many mobs and measure bytes/s")
    ap.add_argument("--tick", action="store_true", help="forceload the region so its entities tick without a player")
    a = ap.parse_args()
    sys.exit(asyncio.run(main(*a.origin[:3], a.load, a.tick)))
