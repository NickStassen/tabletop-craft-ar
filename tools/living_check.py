"""Time of day, weather and living entities against a running plugin (PROTOCOL.md "World state: time and weather",
"Living entities").

Needs a server with auth.token set (the checks set up their scene with command_v1). Builds a stone platform in the
air inside the region, summons the scene and checks what the headset gets:

  1. world_state: sent after welcome with world_v1, again within a second of /time set and /weather (rain, thunder),
     about once a second otherwise; the clock runs between messages;
  2. leads: a wolf leashed to a fence (holder = the knot's id, the knot listed), a boat leashed to a happy ghast;
  3. gear: horse armour and saddle, a llama's chest and carpet, a happy ghast harness, wolf and cat collars;
  4. looks: an experience orb's value, an enderman's block, a shulker's lid and face, a snow golem without its
     pumpkin, a villager's visible name;
  5. displays: a text display's text and background, an item display's transformation, brightness and context,
     a block display's block and scale;
  6. new types: ender dragon, wither, giant, evoker fangs, wither skull, llama spit, shulker bullet, dragon fireball,
     cushion, sulfur cube, ominous item spawner, and a lightning bolt;
  7. bytes per second with the scene standing still.

  python tools/living_check.py --url ws://127.0.0.1:8813/tabletop --token T [--origin 0,96,0] [--report out.json]
"""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time

import websockets

from tabletop_protocol import CAP_COMMAND, CAP_ENTITIES, CAP_WORLD, EntityMirror, Envelope, validate


class Headset:
    def __init__(self, url: str, token: str, caps: list[str]) -> None:
        self.url, self.token, self.caps = url, token, caps
        self.env, self.msgs = Envelope(), []
        self.mirror = EntityMirror()
        self.welcome: dict | None = None
        self.bytes = 0
        self.seen: set[str] = set()   # every entity type listed so far (short-lived ones like fangs and bolts too)

    async def open(self) -> None:
        self.ws = await websockets.connect(self.url, max_size=64 * 1024 * 1024)
        await self.send("hello", client="living-check", client_version="0.4.0", protocols=[1], token=self.token,
                        caps=["rle_palette_v1", "avatar", *self.caps])
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
            m["_at"] = time.monotonic()
            self.bytes += len(raw)
            if m["type"] == "welcome":
                self.welcome = m
            if m["type"] == "entities":
                self.mirror.apply(m)
                self.seen |= {e["type"] for e in m["entities"] if "type" in e}
            self.msgs.append(m)

    async def until(self, pred, timeout: float = 5.0):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            for m in list(self.msgs):
                if pred(m):
                    return m
            await asyncio.sleep(0.02)
        return None

    async def command(self, line: str) -> dict | None:
        await asyncio.sleep(0.55)   # commands.max-per-second 2
        seq = await self.send("command", line=line)
        return await self.until(lambda m: m.get("ref_seq") == seq and m["type"] == "command_result", 8.0)

    def of_type(self, t: str) -> list[dict]:
        return self.mirror.of_type("minecraft:" + t)

    def world_states(self, after: float = 0.0) -> list[dict]:
        return [m for m in self.msgs if m["type"] == "world_state" and m["_at"] >= after]


def sub(d: dict | None, *keys: str) -> dict | None:
    return None if d is None else {k: d.get(k) for k in keys}


async def run(args) -> dict:
    results: dict[str, object] = {}
    fails: list[str] = []

    def check(ok: bool, what: str, detail: object = "") -> None:
        print(("PASS " if ok else "FAIL ") + what + (f"  [{detail}]" if detail != "" else ""))
        results[what] = bool(ok)
        if not ok:
            fails.append(what)

    ox, oy, oz = (int(v) for v in args.origin.split(","))
    h = Headset(args.url, args.token, [CAP_WORLD, CAP_ENTITIES, CAP_COMMAND])
    await h.open()
    await h.until(lambda m: m["type"] == "welcome")
    caps = (h.welcome or {}).get("caps", [])
    check(CAP_WORLD in caps and CAP_COMMAND in caps, "welcome grants world_v1 and command_v1", caps)
    await h.send("region_request", world="world", origin=[ox, oy, oz], dims=[32, 32, 32])
    await h.until(lambda m: m["type"] == "chunk_snapshot" and m["origin"] == [ox, oy, oz], 20)
    first = await h.until(lambda m: m["type"] == "world_state", 5)
    check(first is not None and first["dimension"] == "overworld" and first["skylight"], "world_state after welcome",
          sub(first, "day_time", "rain", "thunder"))

    # the scene, in a clear day (endermen and snow golems suffer in rain): a platform in the air, a fence for the knot,
    # a wall the shulker sticks to
    for line in ("gamerule doDaylightCycle true", "weather clear", "time set 6000"):
        await h.command(line)
    x, y, z = ox + 2, oy + 4, oz + 2
    p = lambda dx, dz, dy=1: f"{x + dx} {y + dy} {z + dz}"   # noqa: E731
    await h.command(f"kill @e[type=!player,x={x},y={y},z={z},dx=28,dy=24,dz=28]")   # a previous run's scene
    setup = [
        f"fill {x} {y} {z} {x + 27} {y} {z + 27} minecraft:stone",
        f"fill {x} {y + 1} {z} {x + 27} {y + 20} {z + 27} minecraft:air",
        f"setblock {p(1, 1)} minecraft:oak_fence",
        f"setblock {p(11, 7)} minecraft:stone",
        f"summon wolf {p(3, 1)} {{NoAI:1b,Owner:[I;1,2,3,4],CollarColor:14b,leash:[I;{x + 1},{y + 1},{z + 1}]}}",
        f"summon horse {p(6, 3)} {{NoAI:1b,Tame:1b,equipment:{{saddle:{{id:\"minecraft:saddle\"}},"
        "body:{id:\"minecraft:iron_horse_armor\"}}}",
        f"summon llama {p(9, 3)} {{NoAI:1b,Tame:1b,ChestedHorse:1b,equipment:{{body:{{id:\"minecraft:red_carpet\"}}}}}}",
        f"summon cat {p(3, 6)} {{NoAI:1b,Owner:[I;1,2,3,4],CollarColor:11b}}",
        f"summon experience_orb {p(5, 8)} {{Value:17}}",
        f"summon enderman {p(8, 8)} {{NoAI:1b,carriedBlockState:\"minecraft:grass_block\"}}",
        f"summon shulker {p(11, 8)} {{NoAI:1b,Peek:50b,AttachFace:2b}}",
        f"summon snow_golem {p(14, 8)} {{NoAI:1b,Pumpkin:0b}}",
        f"summon villager {p(17, 8)} {{NoAI:1b,CustomName:\"Bob\",CustomNameVisible:1b}}",
        f"summon text_display {p(5, 12, 3)} {{text:\"Hello table\",billboard:\"center\",background:1073741824}}",
        f"summon item_display {p(8, 12, 3)} {{item:{{id:\"minecraft:diamond_sword\"}},item_display:\"fixed\","
        "brightness:{block:15,sky:15},"
        "transformation:{translation:[0f,0.5f,0f],left_rotation:[0f,0f,0.3827f,0.9239f],scale:[2f,2f,2f],right_rotation:[0f,0f,0f,1f]}}",
        f"summon block_display {p(11, 12, 3)} {{block_state:{{id:\"minecraft:oak_stairs\",properties:{{facing:\"east\"}}}},"
        "transformation:{translation:[0f,0f,0f],left_rotation:[0f,0f,0f,1f],scale:[0.5f,0.5f,0.5f],right_rotation:[0f,0f,0f,1f]}}",
        f"summon happy_ghast {p(20, 20, 6)} {{NoAI:1b,UUID:[I;1,2,3,5],equipment:{{body:{{id:\"minecraft:orange_harness\"}}}}}}",
        f"summon oak_boat {p(20, 20)} {{leash:{{UUID:[I;1,2,3,5]}}}}",
        f"summon ender_dragon {p(14, 22, 8)} {{NoAI:1b}}",
        f"summon wither {p(5, 22, 2)} {{NoAI:1b}}",
        f"summon giant {p(24, 4)} {{NoAI:1b}}",
        f"summon evoker_fangs {p(2, 14)}",
        f"summon wither_skull {p(4, 14, 4)} {{Motion:[0d,0d,0d]}}",
        f"summon llama_spit {p(6, 14, 4)} {{Motion:[0d,0d,0d],NoGravity:1b}}",
        f"summon shulker_bullet {p(8, 14, 4)} {{NoGravity:1b}}",
        f"summon dragon_fireball {p(10, 14, 4)} {{Motion:[0d,0d,0d]}}",
        f"summon cushion {p(12, 14)} {{Color:3b}}",
        f"summon sulfur_cube {p(14, 14)} {{NoAI:1b}}",
        f"summon ominous_item_spawner {p(16, 14, 2)} {{item:{{id:\"minecraft:diamond\"}},spawn_item_after_ticks:100000L}}",
        f"summon lightning_bolt {p(24, 14)}",
    ]
    refused = []
    for line in setup:
        r = await h.command(line)
        if r is None or not (r.get("ok") or "No blocks were filled" in str(r.get("lines"))):   # clearing air is fine
            refused.append((line.split(" ")[1], r and r.get("lines")))
    check(not refused, "scene set up with commands", refused[:4])
    bolt = await h.until(lambda m: m["type"] == "entities" and any(e.get("type") == "minecraft:lightning_bolt" for e in m["entities"]), 3)
    check(bolt is not None, "the lightning bolt is listed while it strikes")
    await asyncio.sleep(2.0)

    def one(t: str) -> dict:
        return (h.of_type(t) or [{}])[0]

    # 2. leads
    wolf, knots, ghast, boat = one("wolf"), h.of_type("leash_knot"), one("happy_ghast"), one("oak_boat")
    check(knots and wolf.get("leash") == knots[0]["id"] and wolf.get("eye", 0) > 0.5, "wolf leashed to the fence knot",
          {"leash": wolf.get("leash"), "knots": [k["id"] for k in knots]})
    check(ghast and boat.get("leash") == ghast.get("id"), "boat leashed to the happy ghast",
          {"boat": boat.get("leash"), "ghast": ghast.get("id")})
    # 3. gear
    horse, llama, cat = one("horse"), one("llama"), one("cat")
    check(horse.get("equipment") == {"body": "minecraft:iron_horse_armor", "saddle": "minecraft:saddle"}, "horse armour and saddle",
          horse.get("equipment"))
    check(llama.get("chested") is True and (llama.get("equipment") or {}).get("body") == "minecraft:red_carpet", "llama chest and carpet",
          sub(llama, "chested", "equipment"))
    check((ghast.get("equipment") or {}).get("body") == "minecraft:orange_harness", "happy ghast harness", ghast.get("equipment"))
    check(wolf.get("tamed") and wolf.get("color") == "red" and cat.get("tamed") and cat.get("color") == "blue", "wolf and cat collars",
          {"wolf": wolf.get("color"), "cat": cat.get("color")})
    # 4. looks
    orb, ender, shulker, golem, villager = one("experience_orb"), one("enderman"), one("shulker"), one("snow_golem"), one("villager")
    check(orb.get("xp") == 17, "experience orb value", orb.get("xp"))
    check(str(ender.get("carried", "")).startswith("minecraft:grass_block"), "enderman carries grass", ender.get("carried"))
    check(0.2 <= shulker.get("peek", 0) <= 0.7 and shulker.get("attach") == "north", "shulker lid open, stuck to the wall north of it",
          sub(shulker, "peek", "attach"))
    check(golem.get("sheared") is True, "snow golem without a pumpkin", golem.get("sheared"))
    check(villager.get("name") == "Bob" and villager.get("name_visible") is True, "villager's name always shown",
          sub(villager, "name", "name_visible"))
    # 5. displays
    text = one("text_display").get("display") or {}
    item, block = one("item_display"), one("block_display")
    idisp, bdisp = item.get("display") or {}, block.get("display") or {}
    check(text.get("text") == "Hello table" and text.get("billboard") == "center" and text.get("background") == 1073741824,
          "text display: text, billboard, background", text)
    check(idisp.get("item_transform") == "fixed" and idisp.get("brightness") == [15, 15] and idisp.get("scale") == [2.0, 2.0, 2.0]
          and abs(idisp.get("left_rotation", [0, 0, 0, 0])[2] - 0.3827) < 1e-3 and item.get("item") == "minecraft:diamond_sword",
          "item display: transformation, brightness, context", idisp)
    check(str(block.get("block", "")).startswith("minecraft:oak_stairs[facing=east") and bdisp.get("scale") == [0.5, 0.5, 0.5],
          "block display: block and scale", {"block": block.get("block"), "scale": bdisp.get("scale")})
    # 6. new types
    want = ["ender_dragon", "wither", "giant", "evoker_fangs", "wither_skull", "llama_spit", "shulker_bullet", "dragon_fireball", "cushion",
            "sulfur_cube", "ominous_item_spawner"]
    seen = {t: "minecraft:" + t in h.seen for t in want}
    check(all(seen.values()), "dragon, wither, giant, fangs, skull, spit, bullet, fireball, cushion, sulfur cube, item spawner listed",
          [t for t, s in seen.items() if not s])
    spawner, cushion = one("ominous_item_spawner"), one("cushion")
    check(spawner.get("item") == "minecraft:diamond", "ominous item spawner shows its item", spawner.get("item"))
    results["cushion color"] = cushion.get("color")
    # 7. bytes per second, the scene standing still (the projectiles may still move)
    b0, t1 = h.bytes, time.monotonic()
    await asyncio.sleep(5)
    rate = (h.bytes - b0) / (time.monotonic() - t1)
    results["bytes_per_s"] = round(rate)
    check(rate < 4000, f"a still scene costs little ({rate:.0f} B/s with world_state)")

    # 1. time and weather
    t0 = time.monotonic()
    await h.command("time set 18000")
    await h.command("weather thunder 6000")
    def midnight(m: dict) -> bool:
        return m["type"] == "world_state" and m["_at"] >= t0 and 17990 <= m["day_time"] < 18200

    storm = await h.until(lambda m: midnight(m) and m["rain"] == 1 and m["thunder"] == 1, 4)
    night = next((m for m in h.msgs if midnight(m)), None)
    check(night is not None and night["_at"] - t0 < 1.5, "time set arrives within a second", night and round(night["_at"] - t0, 2))
    check(storm is not None, "midnight thunderstorm: rain 1, thunder 1")
    await asyncio.sleep(3.2)
    states = h.world_states(time.monotonic() - 3.0)
    times = [s["time"] for s in states]
    rising = all(b > a for a, b in zip(times, times[1:], strict=False))
    check(2 <= len(states) <= 4 and rising, "about one world_state a second while the clock runs", times)
    # back to a clear day for the next run
    t1 = time.monotonic()
    await h.command("weather clear")
    clear = await h.until(lambda m: m["type"] == "world_state" and m["rain"] == 0 and m["_at"] >= t1, 3)
    check(clear is not None, "weather clear arrives")
    await h.command("time set 6000")
    await h.ws.close()
    results["fails"] = fails
    return results


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default="ws://127.0.0.1:8765/tabletop")
    ap.add_argument("--token", required=True, help="the server's auth.token (the scene is set up with commands)")
    ap.add_argument("--origin", default="0,96,0", help="region origin x,y,z (32 x 32 x 32); a platform is built 4 blocks up")
    ap.add_argument("--report", help="write the results as JSON")
    args = ap.parse_args()
    res = asyncio.run(run(args))
    if args.report:
        with open(args.report, "w") as f:
            json.dump(res, f, indent=1)
    print("ALL PASS" if not res["fails"] else f"{len(res['fails'])} FAILED")
    return 0 if not res["fails"] else 1


if __name__ == "__main__":
    sys.exit(main())
