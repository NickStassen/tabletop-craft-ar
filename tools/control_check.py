"""Chat, commands and server status against a running plugin (PROTOCOL.md 4.26-4.28).

Needs a server with auth.token set (commands.require-token is on by default) and, for the chat round trip, online-mode
off with ViaVersion + ViaBackwards (the mineflayer bot in tools/bot/chat_player.js joins as 26.1, see tools/e2e.sh).

  1. caps: a wrong token is refused; a headset asking for chat_v1, command_v1 and status_v1 gets them with
     welcome.chat (sanitised operator name) and welcome.commands; one without command_v1 gets command_denied;
  2. commands: a result with feedback, a syntax error (ok false), an unknown command, denied roots (stop,
     minecraft:stop, execute ... run stop, op), malformed lines, say as chat, the "at" pointer (setblock at a block,
     checked with execute if block), tab completion (start and items), the rate limit;
  3. status: server_status every 2 s, world_info with gamerules from the server (one toggled and back), players_info;
  4. chat with a Java player (the bot): join, the bot's chat to the headset, the headset's chat to the bot in the
     server format, /tabletop tell, an advancement and a death message, the giant's speech bubble (a tagged
     text display that the entity stream never lists), quit; with --console also console say and the notify-ops echo.

  python tools/control_check.py --url ws://127.0.0.1:8811/tabletop --token T --mc-port 25611 \\
      [--console tools/run/<dir>/stdin.fifo] [--report out.json]
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import pathlib
import random
import sys
import time

import websockets

from tabletop_protocol import CAP_CHAT, CAP_COMMAND, CAP_STATUS, Envelope, suggestion_apply, validate

HERE = pathlib.Path(__file__).parent
CONTROL = [CAP_CHAT, CAP_COMMAND, CAP_STATUS]


class Headset:
    def __init__(self, url: str, token: str, caps: list[str], name: str = "Talk Check!") -> None:
        self.url, self.token, self.caps, self.name = url, token, caps, name
        self.env, self.msgs = Envelope(), []
        self.welcome: dict | None = None
        self.closed: int | None = None
        self.last_command = 0.0

    async def open(self) -> None:
        self.ws = await websockets.connect(self.url, max_size=64 * 1024 * 1024)
        await self.send("hello", client="control-check", client_version="0.4.0", protocols=[1], token=self.token,
                        operator_name=self.name, caps=["rle_palette_v1", "avatar", *self.caps])
        self.task = asyncio.create_task(self.reader())

    async def send(self, t: str, validate_it: bool = True, **kw) -> int:
        m = self.env.wrap(t, **kw)
        if validate_it:
            validate(m)
        await self.ws.send(json.dumps(m))
        return m["seq"]

    async def reader(self) -> None:
        try:
            async for raw in self.ws:
                m = json.loads(raw)
                validate(m)
                m["_at"] = time.monotonic()
                m["_bytes"] = len(raw)
                if m["type"] == "welcome":
                    self.welcome = m
                self.msgs.append(m)
        except websockets.ConnectionClosed as e:
            self.closed = e.rcvd.code if e.rcvd else -1

    async def until(self, pred, timeout: float = 5.0):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            for m in list(self.msgs):
                if pred(m):
                    return m
            await asyncio.sleep(0.01)
        return None

    async def reply(self, seq: int, timeout: float = 5.0):
        return await self.until(lambda m: m.get("ref_seq") == seq and m["type"] != "region_changed", timeout)

    async def command(self, line: str, at: dict | None = None, timeout: float = 5.0):
        # Stay under commands.max-per-second (2/s): the burst test at the end checks the limit itself.
        wait = self.last_command + 0.55 - time.monotonic()
        if wait > 0:
            await asyncio.sleep(wait)
        self.last_command = time.monotonic()
        kw = {"line": line}
        if at is not None:
            kw["at"] = at
        seq = await self.send("command", **kw)
        return await self.reply(seq, timeout)

    def chats(self, kind: str | None = None) -> list[dict]:
        return [m for m in self.msgs if m["type"] == "chat" and (kind is None or m["kind"] == kind)]

    async def close(self) -> None:
        await self.ws.close()


class Bot:
    """tools/bot/chat_player.js as a subprocess: lines in on stdin, events out on stdout."""

    def __init__(self, node: str, port: int, name: str) -> None:
        self.node, self.port, self.name, self.lines = node, port, name, []

    async def start(self) -> None:
        env = dict(os.environ, MC_PORT=str(self.port), BOT_NAME=self.name, BOT_MS="150000")
        self.proc = await asyncio.create_subprocess_exec(self.node, "chat_player.js", cwd=HERE / "bot", env=env,
                                                         stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
                                                         stderr=asyncio.subprocess.STDOUT)
        self.task = asyncio.create_task(self.read())

    async def read(self) -> None:
        async for raw in self.proc.stdout:
            self.lines.append(raw.decode(errors="replace").rstrip())

    async def say(self, text: str) -> None:
        self.proc.stdin.write(f"say {text}\n".encode())
        await self.proc.stdin.drain()

    async def saw(self, needle: str, timeout: float = 8.0) -> str | None:
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            for line in list(self.lines):
                if needle in line:
                    return line
            await asyncio.sleep(0.05)
        return None

    async def quit(self) -> None:
        try:
            self.proc.stdin.write(b"quit\n")
            await self.proc.stdin.drain()
            await asyncio.wait_for(self.proc.wait(), 10)
        except Exception:
            self.proc.kill()


async def run(args) -> dict:
    results: dict[str, object] = {}
    fails: list[str] = []

    def check(ok: bool, what: str, detail: object = "") -> None:
        print(("PASS " if ok else "FAIL ") + what + (f"  [{detail}]" if detail != "" else ""))
        results[what] = ok
        if not ok:
            fails.append(what)

    def console(line: str) -> None:
        if args.console:
            with open(args.console, "w") as f:
                f.write(line + "\n")

    nonce = str(random.randint(1000, 9999))

    # 1. caps
    bad = Headset(args.url, "wrong-" + args.token, CONTROL)
    await bad.open()
    err = await bad.until(lambda m: m["type"] == "error")
    await asyncio.sleep(0.3)
    check(err is not None and err["code"] == "auth_failed", "wrong token: auth_failed")

    h = Headset(args.url, args.token, [*CONTROL, "entities_v1"])
    await h.open()
    await h.until(lambda m: m["type"] == "welcome")
    w = h.welcome or {}
    check(set(CONTROL) <= set(w.get("caps", [])), "welcome grants chat_v1, command_v1, status_v1", w.get("caps"))
    check(w.get("chat", {}).get("name") == "TalkCheck" and w["chat"].get("send") is True and w["chat"].get("max_len") == 256,
          "welcome.chat: sanitised operator name, send, max_len", w.get("chat"))
    check(isinstance(w.get("commands", {}).get("quick"), list) and w["commands"].get("max_len") == 256,
          "welcome.commands: quick buttons", w.get("commands"))
    region = w.get("region", {})
    world = w.get("world", "world")

    ro = Headset(args.url, args.token, [CAP_CHAT], name="Reader")
    await ro.open()
    await ro.until(lambda m: m["type"] == "welcome")
    check(CAP_COMMAND not in (ro.welcome or {}).get("caps", []) and "commands" not in (ro.welcome or {}),
          "no command_v1 asked: none granted")
    r = await ro.command("list")
    check(r is not None and r["type"] == "error" and r["code"] == "command_denied",
          "command without command_v1: command_denied", r and r.get("message"))

    # 2. commands
    t0 = time.monotonic()
    r = await h.command("time set day")
    check(r is not None and r["type"] == "command_result" and r["ok"] and r["lines"],
          "time set day: ok with feedback", r and (r.get("lines"), r.get("ms")))
    results["time_set_day_rtt_ms"] = round((time.monotonic() - t0) * 1000, 1)
    r = await h.command("/minecraft:time set noon")
    check(r is not None and r["type"] == "command_result" and r["ok"],
          "/minecraft:time set noon: slash and namespace", r and r.get("lines"))
    r = await h.command("time sett")
    check(r is not None and r["type"] == "command_result" and not r["ok"] and
          any("<--[HERE]" in x or "ncorrect" in x or "nknown" in x for x in r["lines"]),
          "syntax error: ok false with the game's message", r and r.get("lines"))
    r = await h.command("nosuchcommand" + nonce)
    check(r is not None and r["type"] == "command_result" and not r["ok"], "unknown command: ok false", r and r.get("lines"))
    for line in ("stop", "minecraft:stop", "execute as @a run stop", "execute as run run stop", "op TalkWalker", "whitelist off"):
        r = await h.command(line)
        check(r is not None and r["type"] == "error" and r["code"] == "command_denied", f"denied: /{line}", r and r.get("message"))
    seq = await h.send("command", validate_it=False, line="time set day\nstop")
    r = await h.reply(seq)
    check(r is not None and r["code"] == "bad_message", "a line with a newline: bad_message", r and r.get("message"))
    seq = await h.send("command", validate_it=False, line="say " + "x" * 300)
    r = await h.reply(seq)
    check(r is not None and r["code"] == "bad_message", "a line over 256 characters: bad_message")

    # the "at" pointer: a block in the sky above the region centre
    cx = region.get("origin", [0, 0, 0])[0] + region.get("dims", [32, 64, 32])[0] // 2
    cz = region.get("origin", [0, 0, 0])[2] + region.get("dims", [32, 64, 32])[2] // 2
    cy = 200
    at = {"world": world, "pos": [cx, cy, cz]}
    r = await h.command("setblock ~ ~ ~ minecraft:gold_block", at=at)
    check(r is not None and r["type"] == "command_result" and r["ok"], "at: setblock ~ ~ ~ at the pointer", r and r.get("lines"))
    r = await h.command(f"execute if block {cx} {cy} {cz} minecraft:gold_block")
    check(r is not None and r.get("ok") and any("passed" in x.lower() for x in r["lines"]),
          "at: the block landed at the pointed cell", r and r.get("lines"))
    r = await h.command("execute if block ~ ~ ~ minecraft:gold_block", at=at)
    check(r is not None and r.get("ok"), "at: ~ ~ ~ is the pointed block", r and r.get("lines"))
    r = await h.command("summon minecraft:armor_stand ~ ~1 ~ {Tags:[\"tt_check\"],NoGravity:1b}", at=at)
    r2 = await h.command(f"execute if entity @e[tag=tt_check,x={cx},y={cy + 1},z={cz},distance=..1]")
    check(r is not None and r.get("ok") and r2 is not None and r2.get("ok"),
          "at: summon ~ ~1 ~ lands on top of the pointed block", r2 and r2.get("lines"))
    await h.command("kill @e[tag=tt_check]")
    r = await h.command("setblock ~ ~ ~ minecraft:air", at=at)
    check(r is not None and r.get("ok"), "at: cleaned up")
    r = await h.command("list", at={"world": "no_such_world", "pos": [0, 0, 0]})
    check(r is not None and r["type"] == "error" and r["code"] == "world_not_found", "at: unknown world -> world_not_found")

    # completion
    async def complete(line: str):
        seq = await h.send("command_complete", line=line)
        return await h.reply(seq)
    s = await complete("gamerule adv")
    check(s is not None and s["type"] == "command_suggestions" and s["start"] == 9 and
          {"advance_time", "advance_weather"} <= set(s["items"]),
          "complete 'gamerule adv' -> advance_time, advance_weather from 9", s and (s.get("start"), s.get("items")))
    if s and s.get("items"):
        check(suggestion_apply("gamerule adv", s["start"], s["items"][0]).startswith("gamerule advance_"),
              "accepting a suggestion builds the line")
    s = await complete("ti")
    check(s is not None and s["start"] == 0 and "time" in s["items"], "complete the command name", s and s.get("items"))
    s = await complete("/ti")
    check(s is not None and s["start"] == 1 and "time" in s["items"], "complete the command name after a slash")
    s = await complete("time set ")
    check(s is not None and s["start"] == 9 and any("day" in x for x in s["items"]),
          "complete an argument after a space", s and s.get("items"))

    # say -> chat
    r = await h.command("say through the console tab " + nonce)
    echo = await h.until(lambda m: m["type"] == "chat" and m["kind"] == "operator" and nonce in m["text"])
    check(r is not None and r.get("as_chat") is True and echo is not None and echo["line"].startswith("[Giant] TalkCheck:"),
          "say from the console tab goes out as the giant's chat", echo and echo.get("line"))

    # 3. status
    st = await h.until(lambda m: m["type"] == "server_status", timeout=4)
    check(st is not None and len(st["tps"]) == 3 and any(x["name"] == world for x in st["worlds"]), "server_status arrives (every 2 s)",
          st and {"tps": st["tps"], "mspt": st["mspt"], "bytes": st["_bytes"]})
    if st:
        sts = [m for m in h.msgs if m["type"] == "server_status"]
        later = await h.until(lambda m: m["type"] == "server_status" and m["_at"] > sts[-1]["_at"], timeout=4)
        gap = later["_at"] - sts[-1]["_at"] if later else None
        check(gap is not None and 1.5 < gap < 2.6, "server_status every 2 s", gap and round(gap, 2))
        results["server_status_bytes"] = st["_bytes"]
    seq = await h.send("world_query")
    wi = await h.reply(seq)
    check(wi is not None and wi["type"] == "world_info" and wi["world"] == world and isinstance(wi["gamerules"].get("advance_time"), bool),
          "world_info with gamerules by the server's names", wi and f"{len(wi['gamerules'])} rules, difficulty {wi['difficulty']}")
    if wi:
        before = wi["gamerules"].get("advance_weather")
        await h.command(f"gamerule advance_weather {str(not before).lower()}")
        seq = await h.send("world_query")
        wi2 = await h.reply(seq)
        await h.command(f"gamerule advance_weather {str(before).lower()}")
        check(wi2 is not None and wi2["gamerules"].get("advance_weather") == (not before),
              "gamerule toggled through a command shows in world_info")

    # 4. chat with a Java player
    bot = Bot(args.node, args.mc_port, "TalkWalker")
    await bot.start()
    spawned = await bot.saw("SPAWN", timeout=30)
    check(spawned is not None, "bot joined the server", (bot.lines or ["no output"])[-1])
    if spawned:
        join = await h.until(lambda m: m["type"] == "chat" and m["kind"] == "join" and m.get("from", {}).get("name") == "TalkWalker")
        check(join is not None and "TalkWalker" in join["text"], "join -> chat kind join", join and join.get("text"))
        seq = await h.send("players_query")
        pi = await h.reply(seq)
        bot_row = next((p for p in (pi or {}).get("players", []) if p["name"] == "TalkWalker"), None)
        check(bot_row is not None and bot_row["gamemode"] and bot_row["max_health"] > 0, "players_info lists the bot", bot_row)
        await asyncio.sleep(1.0)
        await bot.say(f"hello headset {nonce}")
        c = await h.until(lambda m: m["type"] == "chat" and m["kind"] == "chat" and m["text"] == f"hello headset {nonce}")
        check(c is not None and c["from"]["name"] == "TalkWalker" and bot_row is not None and c["from"]["uuid"] == bot_row["uuid"],
              "Java chat reaches the headset with the player's uuid", c and c.get("from"))
        await h.send("chat_send", text=f"hello bot <red>{nonce}</red>")
        got = await bot.saw(f"hello bot <red>{nonce}</red>")
        check(got is not None and f"[Giant] TalkCheck: hello bot <red>{nonce}</red>" in got,
              "headset chat reaches the Java player in the server format", got)
        await bot.say(f"/tabletop tell psst {nonce}")
        wh = await h.until(lambda m: m["type"] == "chat" and m["kind"] == "whisper" and nonce in m["text"])
        check(wh is not None and wh["from"]["name"] == "TalkWalker", "/tabletop tell -> chat kind whisper", wh and wh.get("text"))
        await h.command("advancement revoke TalkWalker only minecraft:story/mine_stone")   # the bot keeps it between runs
        r = await h.command("advancement grant TalkWalker only minecraft:story/mine_stone")
        adv = await h.until(lambda m: m["type"] == "chat" and m["kind"] == "advancement")
        check(adv is not None and "TalkWalker" in adv["text"], "advancement -> chat kind advancement", adv and adv.get("text"))
        await h.command("gamemode survival TalkWalker")
        await h.command("kill TalkWalker")
        death = await h.until(lambda m: m["type"] == "chat" and m["kind"] == "death")
        check(death is not None and "TalkWalker" in death["text"],
              "death -> chat kind death (flattened en_us)", death and death.get("text"))
        # the giant's speech bubble for Java players
        await h.send("avatar_config", enabled=True, block_size_m=0.01, show_hands=True, name_tag=None)
        await asyncio.sleep(1.0)
        await h.send("chat_send", text=f"bubble {nonce}")
        await asyncio.sleep(0.5)
        r = await h.command("execute if entity @e[type=minecraft:text_display,tag=tabletop_avatar]")
        check(r is not None and r.get("ok"), "the giant's speech bubble is a tagged text display", r and r.get("lines"))
        listed = [e for m in h.msgs if m["type"] == "entities" for e in m["entities"] if e.get("type") == "minecraft:text_display"]
        check(not listed, "the entity stream never lists the bubble")
        await asyncio.sleep(6.0)
        r = await h.command("execute if entity @e[type=minecraft:text_display,tag=tabletop_avatar]")
        check(r is not None and not r.get("ok"), "the bubble goes away after chat.bubble-seconds", r and r.get("lines"))
        await h.send("avatar_config", enabled=False)
        if args.console:
            console(f"say console {nonce}")
            say = await h.until(lambda m: m["type"] == "chat" and m["kind"] == "say" and nonce in m["text"])
            check(say is not None and say["from"]["name"] == "Server", "console say -> chat kind say from Server", say and say.get("text"))
            console("op TalkWalker")
            await asyncio.sleep(1.0)
            await h.command("time set day")
            note = await bot.saw("[Tabletop: /time set day]")
            check(note is not None, "notify-ops: an op sees [Tabletop: /time set day]", note)
            console("deop TalkWalker")
        await bot.quit()
        quit_ = await h.until(lambda m: m["type"] == "chat" and m["kind"] == "quit")
        check(quit_ is not None and "TalkWalker" in quit_["text"], "quit -> chat kind quit", quit_ and quit_.get("text"))
        check(any(m["type"] == "chat" and m["kind"] == "operator" for m in ro.msgs),
              "a read-only chat headset sees the operator's lines too")
    else:
        await bot.quit()
        print("\n".join(bot.lines[-20:]))

    # rate limits (last; the bucket of 5 refills at 2/s first)
    await asyncio.sleep(3.0)
    seqs = [await h.send("command", line="list") for _ in range(10)]
    replies = [await h.reply(sq) for sq in seqs]
    limited = sum(1 for x in replies if x and x["type"] == "error" and x["code"] == "rate_limited")
    ran = sum(1 for x in replies if x and x["type"] == "command_result")
    check(5 <= ran <= 7 and limited == 10 - ran, "commands: burst 5 then 2/s, the rest rate_limited", f"{ran} ran, {limited} limited")
    results["commands_ms"] = sorted(m["ms"] for m in h.msgs if m["type"] == "command_result")
    await h.close()
    await ro.close()
    results["fails"] = fails
    return results


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default="ws://127.0.0.1:8765/tabletop")
    ap.add_argument("--token", required=True, help="the server's auth.token (commands need one)")
    ap.add_argument("--mc-port", type=int, default=25565, help="the server's game port, for the mineflayer bot")
    ap.add_argument("--console", help="the server's console FIFO (console say, op for the notify-ops check)")
    ap.add_argument("--node", default="node")
    ap.add_argument("--report")
    args = ap.parse_args()
    res = asyncio.run(run(args))
    if args.report:
        pathlib.Path(args.report).write_text(json.dumps(res, indent=1))
    print("ALL PASS" if not res["fails"] else f"{len(res['fails'])} FAILED: {res['fails']}")
    return 0 if not res["fails"] else 1


if __name__ == "__main__":
    sys.exit(main())
