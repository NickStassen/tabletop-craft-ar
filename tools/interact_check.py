"""Using blocks, items and mobs from the headset against a running plugin (PROTOCOL.md "Using blocks and items",
"Acting on mobs"; cap interact_v1).

Connects like the Quest (hello caps edit_v1, entities_v1, actions_v1, interact_v1), moves the shared region onto the
surface of a flat world (region_request origin [x, -64, z]), builds a small test bench with block_edit and then:
  1. the item_registry arrives after welcome and block_registry, before the first snapshot, with the usable items,
     spawn eggs and the lightning wand; welcome.interact carries the switches;
  2. an oak door opens and closes (both halves); a lever on a stone block powers redstone dust on the far side of
     that block (vanilla updates the support block's neighbours too) and turns it off again; a stone button goes down
     and comes back up by itself (Block#tick after 20 ticks); a note block is tuned;
  3. bone meal grows a sapling (repeated until it does), an axe strips a log, a water bucket pours a source on the
     grass and the empty bucket picks it up again;
  4. a pig spawn egg makes a pig; it is named "Rex", hit (hurt action, health down), held (gravity and AI off) and
     dropped somewhere else, frozen and unfrozen, made to glow, moved, and killed; entity_info reports each step;
  5. flint and steel lights TNT (the block goes, a primed TNT entity comes) and entity_op remove takes it away before
     it explodes;
  6. refusals carry op and pos (or uuid): a chest (the container view's hook), an axe on stone, a position outside
     the region, an item the server doesn't offer, an unknown uuid, and a second client without interact_v1.
The bench is broken again at the end (water that flowed dries by itself).

  python tools/interact_check.py --url ws://127.0.0.1:8765/tabletop [--token T] [--report out.json] [--at X,Z] [--log server.log]
"""
from __future__ import annotations

import argparse
import asyncio
import json
import re
import sys
import time
import uuid as uuidlib

import websockets

from tabletop_protocol import (
    CAP_ACTIONS,
    CAP_EDIT,
    CAP_ENTITIES,
    CAP_INTERACT,
    LIGHTNING_ITEM,
    EntityMirror,
    Envelope,
    WorldMirror,
    block_edit,
    entity_op,
    interact,
    validate,
)

CAPS = ["rle_palette_v1", CAP_EDIT, CAP_ENTITIES, CAP_ACTIONS, CAP_INTERACT]


def prop(state: str | None, key: str) -> str | None:
    if not state or "[" not in state:
        return None
    for kv in state[state.index("[") + 1:-1].split(","):
        k, _, v = kv.partition("=")
        if k == key:
            return v
    return None


def name(state: str | None) -> str:
    return (state or "").split("[", 1)[0].split(":", 1)[-1]


class Client:
    def __init__(self, url: str, token: str, caps: list[str]) -> None:
        self.url, self.token, self.caps = url, token, caps
        self.env, self.mirror, self.ents, self.msgs = Envelope(), WorldMirror(), EntityMirror(), []
        self.welcome: dict | None = None
        self.items: dict | None = None
        self.order: list[str] = []
        self.hurt: list[int] = []

    async def open(self) -> None:
        self.ws = await websockets.connect(self.url, max_size=16 * 1024 * 1024)
        await self.send("hello", client="interact-check", client_version="0.5.0", protocols=[1], token=self.token, caps=self.caps)
        self.task = asyncio.create_task(self.reader())

    async def send(self, t: str, **kw) -> int:
        m = self.env.wrap(t, **kw)
        validate(m)
        await self.ws.send(json.dumps(m))
        return m["seq"]

    async def reader(self) -> None:
        async for raw in self.ws:
            m = json.loads(raw)
            validate(m)
            if len(self.order) < 4:
                self.order.append(m["type"])
            t = m["type"]
            if t == "welcome":
                self.welcome = m
            elif t == "item_registry":
                self.items = m
            elif t == "chunk_snapshot":
                self.mirror.apply_snapshot(m)
            elif t == "block_update":
                self.mirror.apply_block_update(m)
            elif t == "entities":
                self.ents.apply(m)
            elif t == "actions":
                self.hurt += [a["id"] for a in m["actions"] if a["kind"] == "hurt"]
            self.msgs.append(m)

    async def until(self, pred, timeout: float = 5.0):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            r = pred()
            if r:
                return r
            await asyncio.sleep(0.01)
        return None

    def errors(self, code: str | None = None, ref_seq: int | None = None) -> list[dict]:
        return [m for m in self.msgs if m["type"] == "error" and (code is None or m["code"] == code) and (ref_seq is None or m.get("ref_seq") == ref_seq)]

    def reply(self, type_: str, ref_seq: int) -> dict | None:
        return next((m for m in self.msgs if m["type"] == type_ and m.get("ref_seq") == ref_seq), None)

    async def close(self) -> None:
        await self.ws.close()
        self.task.cancel()


async def run(a: argparse.Namespace) -> dict:
    rep: dict = {"checks": [], "ms": {}}

    def check(ok: bool, what: str, **info) -> bool:
        rep["checks"].append({"ok": bool(ok), "what": what, **info})
        print(("PASS " if ok else "FAIL ") + what + (f" {info}" if info else ""), flush=True)
        return bool(ok)

    c = Client(a.url, a.token, CAPS)
    await c.open()
    await c.until(lambda: c.mirror.loaded and c.welcome and c.items)
    caps = (c.welcome or {}).get("caps") or []
    check(CAP_INTERACT in caps and isinstance((c.welcome or {}).get("interact"), dict), "welcome grants interact_v1 with the interact switches",
          interact=(c.welcome or {}).get("interact"))
    check(c.order[:3] == ["welcome", "block_registry", "item_registry"], "item_registry follows welcome and block_registry", order=c.order)
    use = c.items["categories"][0]["items"] if c.items else []
    eggs = c.items["categories"][1]["items"] if c.items else []
    check("minecraft:bone_meal" in use and "minecraft:bucket" in use and LIGHTNING_ITEM in use and "minecraft:pig_spawn_egg" in eggs
          and "minecraft:wither_spawn_egg" not in eggs, "item_registry lists usable items and spawn eggs minus deny-spawn", use=len(use), eggs=len(eggs))

    # The region onto the flat world's surface (grass at y = -61).
    X0, Z0 = a.at
    G = -61
    await c.send("region_request", world=c.welcome["world"], origin=[X0, -64, Z0], dims=[32, 64, 32])
    ok = await c.until(lambda: c.mirror.loaded and c.mirror.origin == (X0, -64, Z0) and c.mirror.at_world(X0 + 1, G, Z0 + 1), 15)
    if not check(ok, "region moved to the surface", origin=c.mirror.origin):
        return rep
    check(name(c.mirror.at_world(X0 + 4, G, Z0 + 4)) == "grass_block", "flat world grass at y=-61", got=c.mirror.at_world(X0 + 4, G, Z0 + 4))
    Y = G + 1

    def P(x: int, z: int, y: int = Y) -> tuple[int, int, int]:
        return (X0 + x, y, Z0 + z)

    bench = {
        P(4, 4): "minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]",
        P(8, 4): "minecraft:stone",
        P(9, 4): "minecraft:lever[face=wall,facing=east,powered=false]",
        P(7, 4): "minecraft:redstone_wire[east=none,north=none,power=0,south=none,west=none]",
        P(6, 10): "minecraft:stone_button[face=floor,facing=north,powered=false]",
        P(24, 4): "minecraft:note_block[instrument=harp,note=0,powered=false]",
        P(12, 4): "minecraft:oak_sapling[stage=0]",
        P(16, 4): "minecraft:oak_log[axis=x]",
        P(28, 28): "minecraft:tnt[unstable=false]",
        P(20, 12): "minecraft:chest[facing=north,type=single,waterlogged=false]",
    }
    for pos, st in bench.items():
        await c.send("block_edit", **block_edit("place", pos, st, "up"))
        await asyncio.sleep(0.06)
    built = await c.until(lambda: all(name(c.mirror.at_world(*p)) == name(s) for p, s in bench.items()), 8)
    if not check(built, "test bench placed with block_edit", missing=[f"{p}={s}" for p, s in bench.items() if name(c.mirror.at_world(*p)) != name(s)]):
        return rep

    async def use(pos, face: str, item: str | None = None, dir=None) -> int:
        return await c.send("interact", **interact(pos, face, item, dir=dir))

    # 2. doors, levers, buttons, note blocks
    door, top = P(4, 4), P(4, 4, Y + 1)
    t0 = time.perf_counter()
    await use(door, "north", dir=(0, -0.3, 1))
    ok = await c.until(lambda: prop(c.mirror.at_world(*door), "open") == "true" and prop(c.mirror.at_world(*top), "open") == "true")
    rep["ms"]["door"] = round((time.perf_counter() - t0) * 1000)
    check(ok, "empty hand opens the oak door (both halves)", ms=rep["ms"]["door"])
    await use(top, "north")
    check(await c.until(lambda: prop(c.mirror.at_world(*door), "open") == "false" and prop(c.mirror.at_world(*top), "open") == "false"),
          "clicking the upper half closes it again")

    lever, dust = P(9, 4), P(7, 4)
    await use(lever, "east")
    ok = await c.until(lambda: prop(c.mirror.at_world(*lever), "powered") == "true" and prop(c.mirror.at_world(*dust), "power") == "15")
    check(ok, "the lever powers the dust behind its stone block (support neighbours updated)",
          lever=c.mirror.at_world(*lever), dust=c.mirror.at_world(*dust))
    await use(lever, "east")
    check(await c.until(lambda: prop(c.mirror.at_world(*lever), "powered") == "false" and prop(c.mirror.at_world(*dust), "power") == "0"),
          "flipped back, the dust goes dark", dust=c.mirror.at_world(*dust))

    button = P(6, 10)
    t0 = time.perf_counter()
    await use(button, "up")
    down = await c.until(lambda: prop(c.mirror.at_world(*button), "powered") == "true")
    up = await c.until(lambda: prop(c.mirror.at_world(*button), "powered") == "false", 4) if down else None
    check(down and up, "the stone button goes down and comes back up by itself", ms=round((time.perf_counter() - t0) * 1000))

    note = P(24, 4)
    await use(note, "up")
    check(await c.until(lambda: prop(c.mirror.at_world(*note), "note") == "1"), "the note block is tuned up one step")

    # 3. bone meal, axe, buckets
    sapling = P(12, 4)
    grown = None
    for i in range(16):
        await use(sapling, "up", "minecraft:bone_meal")
        grown = await c.until(lambda: c.mirror.at_world(*sapling) != "minecraft:oak_sapling[stage=0]", 0.6)
        if grown:
            break
    check(grown, "bone meal grows the sapling", now=c.mirror.at_world(*sapling), tries=i + 1)
    log = P(16, 4)
    await use(log, "north", "minecraft:iron_axe")
    check(await c.until(lambda: c.mirror.at_world(*log) == "minecraft:stripped_oak_log[axis=x]"), "the axe strips the log", now=c.mirror.at_world(*log))
    ground, cell = P(20, 20, G), P(20, 20)
    await use(ground, "up", "minecraft:water_bucket")
    check(await c.until(lambda: c.mirror.at_world(*cell) == "minecraft:water[level=0]"), "the water bucket pours a source on the grass",
          now=c.mirror.at_world(*cell))
    await asyncio.sleep(0.4)
    await use(cell, "up", "minecraft:bucket")
    check(await c.until(lambda: c.mirror.at_world(*cell) != "minecraft:water[level=0]"), "the empty bucket picks the source up",
          now=c.mirror.at_world(*cell))

    # 4. a pig: spawn, name, hit, hold and drop, freeze, glow, move, kill
    known = set(c.ents.entities)
    await use(P(24, 16, G), "up", "minecraft:pig_spawn_egg")
    pig = await c.until(lambda: next((e for e in c.ents.of_type("minecraft:pig") if e["id"] not in known), None), 5)
    if check(pig, "the pig spawn egg makes a pig on the grass", pos=pig and pig.get("pos")):
        pid, pu = pig["id"], pig["uuid"]

        async def op(o: str, **kw) -> int:
            return await c.send("entity_op", **entity_op(pu, o, **kw))

        async def info() -> dict | None:
            s = await op("info")
            return await c.until(lambda: c.reply("entity_info", s), 3)

        await op("name", text="Rex")
        check(await c.until(lambda: c.ents.entities.get(pid, {}).get("name") == "Rex"), "the pig is named Rex")
        i0 = await info()
        check(i0 and i0["entity_type"] == "minecraft:pig" and i0.get("name") == "Rex" and i0["health"][0] == i0["health"][1], "entity_info: a healthy pig named Rex",
              info=i0)
        await asyncio.sleep(0.6)   # past the spawn's invulnerable ticks
        await op("attack", dir=[1.0, -0.2, 0.0])
        check(await c.until(lambda: pid in c.hurt, 3), "a hit flashes it red (actions hurt)")
        i1 = await info()
        check(i1 and i1["health"][0] < i0["health"][0], "the hit took health", health=i1 and i1["health"])
        lift = [X0 + 26.5, Y + 2.0, Z0 + 18.5]
        for k in range(5):
            await op("hold", to=lift)
            await asyncio.sleep(0.1)
        i2 = await info()
        check(i2 and i2.get("held") and i2.get("ai") is False and abs(i2["pos"][1] - lift[1]) < 0.3, "held: it hangs where the hand says, AI off", info=i2)
        await op("drop", to=[X0 + 26.5, float(Y), Z0 + 18.5])
        await asyncio.sleep(0.2)
        i3 = await info()
        check(i3 and not i3.get("held") and i3.get("ai") is True and abs(i3["pos"][0] - (X0 + 26.5)) < 0.3, "dropped: AI back, on the ground where it was put",
              info=i3)
        await op("hold", to=lift)
        await asyncio.sleep(3.6)   # no drop: the server lets go after 3 s
        it = await info()
        check(it and not it.get("held") and it.get("ai") is True, "a hold without updates times out after 3 s (AI back)", info=it)
        await op("drop", to=[X0 + 26.5, float(Y), Z0 + 18.5])
        await op("freeze")
        i4 = await info()
        await op("unfreeze")
        i5 = await info()
        check(i4 and i4.get("frozen") is True and i5 and i5.get("frozen") is False, "frozen and unfrozen")
        await op("glow")
        i6 = await info()
        await op("glow")
        check(i6 and i6["glowing"] is True, "it glows")
        to = [X0 + 22.5, float(Y), Z0 + 14.5]
        await op("move", to=to, yaw=90.0)
        moved = await c.until(lambda: abs(c.ents.entities.get(pid, {}).get("pos", [0, 0, 0])[0] - to[0]) < 0.3
                              and abs(c.ents.entities.get(pid, {}).get("pos", [0, 0, 0])[2] - to[2]) < 0.3)
        check(moved, "moved to another spot", pos=c.ents.entities.get(pid, {}).get("pos"))
        s = await op("move", to=[X0 - 40.5, float(Y), Z0])
        check(await c.until(lambda: c.errors("out_of_region", s)), "a move outside the region is refused")
        await op("kill")
        check(await c.until(lambda: pid not in c.ents.entities, 4), "killed: it leaves the entity list")
    s = await c.send("entity_op", **entity_op(str(uuidlib.uuid4()), "info"))
    e = await c.until(lambda: c.errors("not_found", s))
    check(e and e[0].get("op") == "entity_op" and "uuid" in e[0], "an unknown uuid is not_found with op and uuid", err=e and e[0])

    # 5. TNT lit, then removed before it goes off
    tnt = P(28, 28)
    known = set(c.ents.entities)
    await use(tnt, "up", "minecraft:flint_and_steel")
    primed = await c.until(lambda: name(c.mirror.at_world(*tnt)) == "air" and next((e for e in c.ents.of_type("minecraft:tnt") if e["id"] not in known), None))
    if check(primed, "flint and steel lights the TNT (block gone, primed TNT entity)"):
        await c.send("entity_op", **entity_op(primed["uuid"], "remove"))
        check(await c.until(lambda: primed["id"] not in c.ents.entities), "entity_op remove takes the primed TNT away")

    # The lightning wand on the grass: no refusal (with --log, the server says it struck).
    s = await use(P(30, 2, G), "up", LIGHTNING_ITEM)
    await asyncio.sleep(1.0)
    check(not c.errors(None, s), "the lightning wand strikes without a refusal", errors=c.errors(None, s))

    # 6. refusals
    s = await use(P(20, 12), "up")
    e = await c.until(lambda: c.errors("interact_denied", s))
    check(e and e[0].get("op") == "interact" and e[0].get("pos") == list(P(20, 12)) and "container" in e[0]["message"],
          "a chest is the container view's hook (refused for now, with op and pos)", msg=e and e[0]["message"])
    s = await use(P(8, 4), "up", "minecraft:iron_axe")
    e = await c.until(lambda: c.errors("interact_denied", s))
    check(e and "stone" in e[0]["message"], "an axe on stone is refused readably", msg=e and e[0]["message"])
    s = await use((X0 - 20, Y, Z0), "up")
    check(await c.until(lambda: c.errors("out_of_region", s)), "outside the region: out_of_region")
    s = await use(P(8, 4), "up", "minecraft:diamond")
    e = await c.until(lambda: c.errors("interact_denied", s))
    check(e and "item_registry" in e[0]["message"], "an item the server doesn't offer is refused", msg=e and e[0]["message"])
    b = Client(a.url, a.token, ["rle_palette_v1", CAP_EDIT])
    await b.open()
    await b.until(lambda: b.mirror.loaded and b.welcome)
    check(b.items is None and CAP_INTERACT not in (b.welcome.get("caps") or []), "no interact_v1, no item_registry")
    s = await b.send("interact", **interact(door, "north"))
    check(await b.until(lambda: b.errors("interact_denied", s)), "interact without the cap is refused")
    await b.close()

    # Clean up the bench (a grown tree's leaves decay by themselves once its log is gone).
    for pos in list(bench) + [top, sapling, P(20, 20)]:
        if name(c.mirror.at_world(*pos)) not in ("air", "water", "cave_air"):
            await c.send("block_edit", **block_edit("break", pos, face="up"))
            await asyncio.sleep(0.06)
    for y in range(Y + 1, Y + 9):
        p = P(12, 4, y)
        if name(c.mirror.at_world(*p)) in ("oak_log",):
            await c.send("block_edit", **block_edit("break", p, face="up"))
            await asyncio.sleep(0.06)
    await asyncio.sleep(0.5)
    await c.close()
    if a.log:
        with open(a.log, encoding="utf-8", errors="replace") as f:
            text = f.read()
        check("INTERACT_NEIGHBOURS on" in text, "server log: the neighbour bridge is on (levers and buttons)")
        check(re.search(r"INTERACT session=\S+ tabletop:lightning .* lightning -> ", text), "server log: the wand struck lightning")
    return rep


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default="ws://127.0.0.1:8765/tabletop")
    ap.add_argument("--token", default="")
    ap.add_argument("--report")
    ap.add_argument("--at", default="0,0", help="X,Z of the region's north-west corner on a flat world (default 0,0)")
    ap.add_argument("--log", help="the server's log file: also checks the neighbour bridge and the lightning line")
    a = ap.parse_args()
    a.at = tuple(int(v) for v in a.at.split(","))
    rep = asyncio.run(run(a))
    fails = [c for c in rep["checks"] if not c["ok"]]
    print(f"{len(rep['checks']) - len(fails)}/{len(rep['checks'])} checks passed", flush=True)
    if a.report:
        with open(a.report, "w") as f:
            json.dump(rep, f, indent=1)
    return 1 if fails or not rep["checks"] else 0


if __name__ == "__main__":
    sys.exit(main())
