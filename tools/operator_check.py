"""The operator's navigation and block-entity tools against a running plugin (PROTOCOL.md "Following a player",
"Marks: bookmarks, spawn and worlds", "Containers", "Signs"; caps marks_v1, interact_v1, command_v1).

Connects like the Quest (hello caps marks_v1, interact_v1, command_v1, block_entities_v1; the token is needed for the
setup commands), starts a mineflayer Java player (tools/bot/chat_player.js) and then:
  1. welcome lists every world (env, build height, spawn), the bookmark limit and the container/sign switches; the
     bookmarks arrive before the first snapshot; region_info carries the spawn and the worlds;
  2. a spawn jump (region_request around welcome.worlds' spawn, keeping width and margin) lands where it was asked;
  3. bookmarks: an add reaches a second headset, bookmarks.yml holds it, a remove reaches both, an unknown name and a
     headset without marks_v1 get mark_denied;
  4. following the Java player as the headset does: region_query finds them, a region_request around them shows them
     in player_positions; after a far teleport and a move into the nether the same steps follow them (the nether
     region comes with its build height in region_changed.limits);
  5. a double chest as one view (54 slots, the other half named), putting, taking, a stale "was" refused, a Java-side
     change arriving live, sorting, a shulker box refusing a shulker box, a furnace refusing non-fuel, a barrel's lid
     open while viewed and shut after container_close, the view ending when the chest is broken, refusals outside the
     region and without interact_v1;
  6. a sign edited on both sides (lines, colour, glow) shows up in block_entities; a waxed sign is refused.
The test area is cleared again at the end.

  python tools/operator_check.py --url ws://127.0.0.1:8765/tabletop --token T --mc-port 25565 [--report out.json]
      [--data-dir plugins/TabletopCraftAR] [--bot-name OpWalker]
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import pathlib
import sys
import time

import websockets

from tabletop_protocol import (
    CAP_BLOCK_ENTITIES,
    CAP_COMMAND,
    CAP_EDIT,
    CAP_INTERACT,
    CAP_MARKS,
    BlockEntityMirror,
    Envelope,
    WorldMirror,
    bookmark,
    container_edit,
    container_slots,
    validate,
)

CAPS = ["rle_palette_v1", CAP_EDIT, CAP_INTERACT, CAP_MARKS, CAP_COMMAND, CAP_BLOCK_ENTITIES]
HERE = pathlib.Path(__file__).resolve().parent


def prop(state: str | None, key: str) -> str | None:
    if not state or "[" not in state:
        return None
    for kv in state[state.index("[") + 1:-1].split(","):
        k, _, v = kv.partition("=")
        if k == key:
            return v
    return None


class Client:
    def __init__(self, url: str, token: str, caps: list[str], name: str) -> None:
        self.url, self.token, self.caps, self.name = url, token, caps, name
        self.env, self.mirror, self.be, self.msgs = Envelope(), WorldMirror(), BlockEntityMirror(), []
        self.welcome: dict | None = None
        self.bookmarks: dict | None = None
        self.order: list[str] = []
        self.players: dict[str, list[float]] = {}
        self.containers: list[dict] = []

    async def open(self) -> None:
        self.ws = await websockets.connect(self.url, max_size=64 * 1024 * 1024)
        await self.send("hello", client=self.name, client_version="0.5.0", protocols=[1], token=self.token, caps=self.caps)
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
            t = m["type"]
            if len(self.order) < 6:
                self.order.append(t)
            if t == "welcome":
                self.welcome = m
            elif t == "bookmarks":
                self.bookmarks = m
            elif t == "chunk_snapshot":
                self.mirror.apply_snapshot(m)
            elif t == "block_update":
                self.mirror.apply_block_update(m)
            elif t == "block_entities":
                self.be.apply(m)
            elif t == "player_positions":
                self.players = {p["name"]: p["pos"] for p in m["players"]}
            elif t == "container":
                self.containers.append(m)
            self.msgs.append(m)

    async def until(self, pred, timeout: float = 5.0):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            r = pred()
            if r:
                return r
            await asyncio.sleep(0.01)
        return None

    def reply(self, type_: str, ref_seq: int) -> dict | None:
        return next((m for m in self.msgs if m["type"] == type_ and m.get("ref_seq") == ref_seq), None)

    def errors(self, ref_seq: int) -> list[dict]:
        return [m for m in self.msgs if m["type"] == "error" and m.get("ref_seq") == ref_seq]

    async def close(self) -> None:
        await self.ws.close()
        self.task.cancel()


async def run(a: argparse.Namespace) -> dict:
    rep: dict = {"checks": [], "ms": {}}

    def check(ok, what: str, **info) -> bool:
        rep["checks"].append({"ok": bool(ok), "what": what, **info})
        print(("PASS " if ok else "FAIL ") + what + (f" {info}" if info else ""), flush=True)
        return bool(ok)

    c = Client(a.url, a.token, CAPS, "operator-check")
    await c.open()
    await c.until(lambda: c.mirror.loaded and c.welcome, 15)
    w = c.welcome or {}
    caps = w.get("caps") or []
    worlds = {x["name"]: x for x in w.get("worlds") or []}
    envs = {x["env"]: x for x in w.get("worlds") or []}
    granted = CAP_MARKS in caps and CAP_INTERACT in caps and CAP_COMMAND in caps
    if not check(granted, "welcome grants marks_v1, interact_v1 and command_v1", caps=caps):
        return rep
    check({"normal", "nether", "the_end"} <= set(envs) and envs["nether"]["min_y"] == 0 and envs["nether"]["max_y"] == 256
          and envs["normal"]["max_y"] == 320, "welcome.worlds: overworld, nether, end with their build heights",
          worlds={k: (v["env"], v["min_y"], v["max_y"], v["spawn"]) for k, v in worlds.items()})
    inter = w.get("interact", {})
    check(w.get("marks", {}).get("edit") is True and inter.get("containers") == "edit" and inter.get("signs") is True,
          "welcome.marks and welcome.interact containers/signs", marks=w.get("marks"), interact=w.get("interact"))
    early = "bookmarks" in c.order and c.order.index("bookmarks") < c.order.index("chunk_snapshot")
    check(early, "bookmarks arrive before the first snapshot", order=c.order)

    async def cmd(line: str) -> bool:
        for _ in range(8):   # commands are limited to 2 a second (burst 5): wait and retry when rate_limited
            seq = await c.send("command", line=line)
            r = await c.until(lambda s=seq: c.reply("command_result", s) or c.errors(s), 5)
            if not (isinstance(r, list) and r and r[0]["code"] == "rate_limited"):
                break
            await asyncio.sleep(0.6)
        if not (isinstance(r, dict) and r.get("ok")):
            print("  command", line, "->", r if not isinstance(r, dict) else r.get("lines"), flush=True)
        return bool(isinstance(r, dict) and r.get("ok"))

    world = w["world"]
    spawn = worlds[world]["spawn"]
    q = await c.send("region_query")
    info = await c.until(lambda: c.reply("region_info", q))
    check(info and info.get("spawn") == spawn and len(info.get("worlds", [])) == len(worlds),
          "region_info carries the spawn and the worlds",
          spawn=info and info.get("spawn"))

    # 2. Spawn jump: 64 wide (96 with a 16 margin), 64 high, centred on the spawn.
    async def jump(world_name: str, centre, dy: int = 64, margin=(16, 16, 16), timeout: float = 30) -> tuple[bool, dict | None]:
        wd = worlds[world_name]
        oy = max(wd["min_y"], min(wd["max_y"] - dy, int(centre[1]) - dy // 2))
        origin = [int(centre[0]) - 48, oy, int(centre[2]) - 48]
        seq = await c.send("region_request", world=world_name, origin=origin, dims=[96, dy, 96], view_margin=list(margin))
        changed = await c.until(lambda: c.reply("region_changed", seq), timeout)
        ok = await c.until(lambda: changed and c.mirror.loaded and c.mirror.world == world_name
                           and list(c.mirror.origin) == origin, timeout)
        return bool(ok), changed

    t0 = time.perf_counter()
    ok, changed = await jump(world, spawn)
    rep["ms"]["spawn_jump"] = round((time.perf_counter() - t0) * 1000)
    check(ok and changed["view_margin"] == [16, 16, 16], "spawn jump: the region lands where asked, margin kept",
          ms=rep["ms"]["spawn_jump"], origin=c.mirror.origin)

    # 3. Bookmarks between two headsets.
    b = Client(a.url, a.token, ["rle_palette_v1", CAP_MARKS], "operator-check-b")
    await b.open()
    await b.until(lambda: b.welcome and b.bookmarks is not None)
    name = "Check spot"
    before = len(c.bookmarks["marks"]) if c.bookmarks else 0
    await c.send("bookmark", **bookmark("add", name, world, spawn))
    ok = await b.until(lambda: any(k["name"] == name for k in (b.bookmarks or {}).get("marks", [])))
    check(ok and any(k["name"] == name and k["pos"] == spawn for k in c.bookmarks["marks"]), "bookmark add reaches both headsets",
          count=len(b.bookmarks["marks"]) if b.bookmarks else None)
    if a.data_dir:
        f = pathlib.Path(a.data_dir) / "bookmarks.yml"
        text = await asyncio.get_running_loop().run_in_executor(None, lambda: (time.sleep(0.5), f.read_text() if f.exists() else "")[1])
        check(name in text and "world: " + world in text, "bookmarks.yml holds the bookmark", file=str(f))
    await b.send("bookmark", **bookmark("remove", "check SPOT"))
    ok = await c.until(lambda: c.bookmarks is not None and not any(k["name"] == name for k in c.bookmarks["marks"]))
    check(ok and len(c.bookmarks["marks"]) == before, "bookmark remove (any case) reaches both headsets")
    seq = await b.send("bookmark", **bookmark("remove", "No such mark"))
    e = await b.until(lambda: b.errors(seq))
    check(e and e[0]["code"] == "mark_denied" and e[0]["op"] == "bookmark" and e[0]["name"] == "No such mark",
          "removing an unknown bookmark: mark_denied", msg=e and e[0]["message"])
    await b.close()
    nm = Client(a.url, a.token, ["rle_palette_v1"], "operator-check-nomarks")
    await nm.open()
    await nm.until(lambda: nm.welcome)
    seq = await nm.send("bookmark", **bookmark("add", "x", world, spawn))
    e = await nm.until(lambda: nm.errors(seq))
    check(e and e[0]["code"] == "mark_denied" and nm.bookmarks is None, "a headset without marks_v1: no bookmarks, mark_denied",
          msg=e and e[0]["message"])
    await nm.close()

    # 4. Following the Java player.
    bot = None
    if a.mc_port:
        env = dict(os.environ, MC_HOST="127.0.0.1", MC_PORT=str(a.mc_port), BOT_NAME=a.bot_name, BOT_MS="240000")
        pipe = asyncio.subprocess.PIPE
        bot = await asyncio.create_subprocess_exec("node", str(HERE / "bot" / "chat_player.js"), cwd=str(HERE / "bot"), env=env,
                                                   stdin=pipe, stdout=pipe, stderr=asyncio.subprocess.STDOUT)
        spawned = False
        end = time.monotonic() + 30
        while time.monotonic() < end and not spawned:
            try:
                line = await asyncio.wait_for(bot.stdout.readline(), 1)
            except TimeoutError:
                continue
            spawned = line.startswith(b"SPAWN")
        if check(spawned, "the Java player joined", bot=a.bot_name):
            async def where() -> dict | None:
                s = await c.send("region_query", player=a.bot_name)
                r = await c.until(lambda: c.reply("region_info", s))
                return r["players"][0] if r and r["players"] else None

            async def follow(timeout: float = 30) -> tuple[bool, dict | None]:
                p = await where()
                if not p:
                    return False, None
                ok, ch = await jump(p["world"], p["pos"], timeout=timeout)
                seen = await c.until(lambda: a.bot_name in c.players, 10)
                return bool(ok and seen), p

            await cmd(f"gamemode creative {a.bot_name}")
            await cmd(f"tp {a.bot_name} {spawn[0] + 5.5} {spawn[1] + 1} {spawn[2] + 5.5}")
            ok, p = await follow()
            check(ok and p["world"] == world, "follow: region_query finds the player, the region around them lists them",
                  pos=p and p["pos"])
            await cmd(f"tp {a.bot_name} {spawn[0] + 400.5} 120 {spawn[2] - 300.5}")
            await asyncio.sleep(0.5)
            t0 = time.perf_counter()
            ok, p = await follow()
            rep["ms"]["far_follow"] = round((time.perf_counter() - t0) * 1000)
            check(ok and abs(p["pos"][0] - (spawn[0] + 400.5)) < 2, "follow: after a far teleport the jump finds them again",
                  ms=rep["ms"]["far_follow"], pos=p and p["pos"])
            nether = envs["nether"]["name"]
            await cmd(f"execute in minecraft:the_nether run tp {a.bot_name} 8.5 70 -4.5")
            await asyncio.sleep(1.0)
            t0 = time.perf_counter()
            ok, p = await follow(40)
            ch = next((m for m in reversed(c.msgs) if m["type"] == "region_changed"), None)
            rep["ms"]["nether_follow"] = round((time.perf_counter() - t0) * 1000)
            check(ok and p["world"] == nether and ch and ch["world"] == nether and ch["limits"]["build_height"] == [0, 256],
                  "follow: into the nether, with its build height in region_changed", ms=rep["ms"]["nether_follow"],
                  limits=ch and ch.get("limits", {}).get("build_height"))
            await cmd(f"execute in minecraft:overworld run tp {a.bot_name} {spawn[0] + 0.5} {spawn[1] + 1} {spawn[2] + 0.5}")
    # 5. Containers, in the air above the spawn (y 200), so nothing in the world gets in the way.
    X, Y, Z = spawn[0] + 4, 200, spawn[2] + 4
    ok, _ = await jump(world, (X + 4, Y, Z + 2), dy=48)
    check(ok, "region around the test spot", origin=c.mirror.origin)
    await cmd(f"fill {X - 1} {Y} {Z - 1} {X + 9} {Y} {Z + 4} minecraft:air")   # what a previous run left ("No blocks were filled" is fine)
    setup = [f"fill {X - 1} {Y - 1} {Z - 1} {X + 9} {Y - 1} {Z + 4} minecraft:stone",
             f"setblock {X + 1} {Y} {Z} minecraft:chest[facing=south,type=left]",
             f"setblock {X} {Y} {Z} minecraft:chest[facing=south,type=right]",
             f"item replace block {X} {Y} {Z} container.0 with minecraft:diamond 3",
             f"setblock {X + 3} {Y} {Z} minecraft:shulker_box", f"setblock {X + 5} {Y} {Z} minecraft:furnace[facing=south]",
             f"setblock {X + 7} {Y} {Z} minecraft:barrel[facing=up]",
             f"setblock {X} {Y} {Z + 2} minecraft:oak_sign[rotation=0]",
             f"setblock {X + 2} {Y} {Z + 2} minecraft:oak_sign[rotation=0]{{is_waxed:1b}}"]
    for line in setup:
        if not await cmd(line):
            check(False, "setup command", line=line)
    chest = [X, Y, Z]
    await c.until(lambda: prop(c.mirror.at_world(*chest), "type") == "right", 5)
    seq = await c.send("container_query", pos=chest)
    v = await c.until(lambda: c.reply("container", seq))
    slots = container_slots(v) if v else {}
    check(v and v["size"] == 54 and v["other"] == [X + 1, Y, Z] and v["title"] == "Large Chest" and v["edit"] == "edit",
          "double chest: one view of 54 slots, the other half named", size=v and v["size"], other=v and v.get("other"),
          title=v and v.get("title"))
    dslot = next((s for s, (it, n) in slots.items() if it == "minecraft:diamond" and n == 3), None)
    check(dslot is not None, "the diamonds put in by command are in the view", slots=slots)

    def latest() -> dict:
        return container_slots(next((m for m in reversed(c.containers) if m["pos"] == chest and not m.get("closed")), {"items": []}))

    t0 = time.perf_counter()
    await c.send("container_edit", **container_edit(chest, 5, "minecraft:torch", 16, was=(None, 0)))
    ok = await c.until(lambda: latest().get(5) == ("minecraft:torch", 16))
    rep["ms"]["container_put"] = round((time.perf_counter() - t0) * 1000)
    check(ok, "put 16 torches: the view updates by itself", ms=rep["ms"]["container_put"])
    await c.send("container_edit", **container_edit(chest, 5, "minecraft:ender_pearl", 64, was=("minecraft:torch", 16)))
    check(await c.until(lambda: latest().get(5) == ("minecraft:ender_pearl", 16)), "64 ender pearls are clamped to their stack size (16)",
          slot=latest().get(5))
    seq = await c.send("container_edit", **container_edit(chest, 5, None, 0, was=("minecraft:torch", 16)))
    e = await c.until(lambda: c.errors(seq))
    check(e and e[0]["code"] == "interact_denied" and "changed" in e[0]["message"] and e[0]["op"] == "container_edit"
          and e[0]["pos"] == chest,
          "a stale \"was\" is refused", msg=e and e[0]["message"])
    await cmd(f"item replace block {X + 1} {Y} {Z} container.1 with minecraft:emerald 7")
    ok = await c.until(lambda: ("minecraft:emerald", 7) in latest().values())
    check(ok, "a change by command (the other half) arrives live", slots=latest())
    await c.send("container_edit", **container_edit(chest, 40, "minecraft:diamond", 5, was=(None, 0)))
    await c.until(lambda: latest().get(40) == ("minecraft:diamond", 5))
    await c.send("container_edit", **container_edit(chest, op="sort"))
    ok = await c.until(lambda: latest().get(0) == ("minecraft:diamond", 8))
    check(ok and len(latest()) == 3, "sort merges the diamonds into the first slot", slots=latest())
    await c.send("container_edit", **container_edit(chest, 0, "minecraft:diamond", 2, was=("minecraft:diamond", 8)))
    check(await c.until(lambda: latest().get(0) == ("minecraft:diamond", 2)), "take 6 diamonds (to nowhere)")
    shulker = [X + 3, Y, Z]
    await c.send("container_query", pos=shulker)
    seq = await c.send("container_edit", **container_edit(shulker, 0, "minecraft:red_shulker_box", 1, was=(None, 0)))
    e = await c.until(lambda: c.errors(seq))
    check(e and "shulker box" in e[0]["message"], "a shulker box can't go into a shulker box", msg=e and e[0]["message"])
    furnace = [X + 5, Y, Z]
    seq = await c.send("container_edit", **container_edit(furnace, 1, "minecraft:stone", 4))
    e = await c.until(lambda: c.errors(seq))
    check(e and "fuel" in e[0]["message"], "a furnace's fuel slot refuses stone", msg=e and e[0]["message"])
    seq = await c.send("container_query", pos=furnace)
    await c.send("container_edit", **container_edit(furnace, 1, "minecraft:coal", 4))
    ok = await c.until(lambda: any(m["pos"] == furnace and container_slots(m).get(1) == ("minecraft:coal", 4)
                                   for m in c.containers if not m.get("closed")))
    check(ok, "coal goes into the furnace's fuel slot")
    barrel = [X + 7, Y, Z]
    await c.send("container_query", pos=barrel)
    check(await c.until(lambda: prop(c.mirror.at_world(*barrel), "open") == "true"), "the barrel opens while it is viewed (Lidded#open)")
    await c.send("container_close", pos=barrel)
    check(await c.until(lambda: prop(c.mirror.at_world(*barrel), "open") == "false"), "and shuts after container_close")
    await c.send("container_query", pos=chest)
    await c.until(lambda: c.containers and c.containers[-1]["pos"] == chest)
    await cmd(f"setblock {X} {Y} {Z} minecraft:air")
    closed = await c.until(lambda: next((m for m in c.containers if m.get("closed") and m["pos"] == chest), None))
    check(closed and "gone" in closed["reason"], "breaking the chest ends the view", reason=closed and closed["reason"])
    seq = await c.send("container_query", pos=[X, Y + 30, Z])
    e = await c.until(lambda: c.errors(seq))
    check(e and e[0]["code"] == "out_of_region", "a container outside the region: out_of_region")
    ni = Client(a.url, a.token, ["rle_palette_v1"], "operator-check-nointeract")
    await ni.open()
    await ni.until(lambda: ni.welcome)
    seq = await ni.send("container_query", pos=furnace)
    e = await ni.until(lambda: ni.errors(seq))
    check(e and e[0]["code"] == "interact_denied", "a headset without interact_v1 can't open containers", msg=e and e[0]["message"])
    await ni.close()

    # 6. Signs.
    sign = [X, Y, Z + 2]
    await c.send("sign_edit", pos=sign, side="front", lines=["", "Operator", "check", ""], color="light_blue", glowing=True)
    await c.send("sign_edit", pos=sign, side="back", lines=["Back", "", "", ""], color="red", glowing=False)
    def sign_shown() -> bool:
        e = c.be.entries.get(tuple(sign))
        front, back = (e or {}).get("front") or {}, (e or {}).get("back") or {}
        return bool(front.get("lines") == ["", "Operator", "check", ""] and front.get("color") == "light_blue" and front.get("glowing")
                    and (back.get("lines") or [""])[0] == "Back" and back.get("color") == "red")

    ok = await c.until(sign_shown, 8)
    check(ok, "sign edited on both sides shows in block_entities", entry=c.be.entries.get(tuple(sign)))
    seq = await c.send("sign_edit", pos=[X + 2, Y, Z + 2], side="front", lines=["no", "", "", ""])
    e = await c.until(lambda: c.errors(seq))
    check(e and "waxed" in e[0]["message"] and e[0]["op"] == "sign_edit", "a waxed sign is refused", msg=e and e[0]["message"])
    seq = await c.send("sign_edit", pos=[X + 1, Y, Z + 2], side="front", lines=["", "", "", ""])
    e = await c.until(lambda: c.errors(seq))
    check(e and "no sign" in e[0]["message"], "no sign there: refused", msg=e and e[0]["message"])

    await cmd(f"fill {X - 1} {Y - 1} {Z - 1} {X + 9} {Y} {Z + 4} minecraft:air")
    if bot:
        bot.stdin.write(b"quit\n")
        await bot.stdin.drain()
        try:
            await asyncio.wait_for(bot.wait(), 5)
        except TimeoutError:
            bot.kill()
    await c.close()
    return rep


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default="ws://127.0.0.1:8765/tabletop")
    ap.add_argument("--token", default="", help="the server's auth.token (the setup runs commands)")
    ap.add_argument("--mc-port", type=int, default=0, help="the server's game port: a mineflayer player joins to be followed (0 = skip)")
    ap.add_argument("--bot-name", default="OpWalker")
    ap.add_argument("--data-dir", help="the plugin's data folder (plugins/TabletopCraftAR): checks bookmarks.yml")
    ap.add_argument("--report")
    a = ap.parse_args()
    rep = asyncio.run(run(a))
    fails = [c for c in rep["checks"] if not c["ok"]]
    print(f"{len(rep['checks']) - len(fails)}/{len(rep['checks'])} checks passed", flush=True)
    if a.report:
        with open(a.report, "w") as f:
            json.dump(rep, f, indent=1)
    return 1 if fails or not rep["checks"] else 0


if __name__ == "__main__":
    sys.exit(main())
