"""Bedrock Edition players through Geyser + Floodgate against a running plugin (QUEST_SETUP.md Part Q, TESTING.md).

Needs a TEST server (online-mode off) with Geyser-Spigot and Floodgate, Geyser's java.auth-type floodgate and
advanced.bedrock.validate-bedrock-login false (the bedrock-protocol bot logs in offline; never on a real server), its
console FIFO, and `npm install` in tools/bedrock. --java also needs tools/bot (npm install) and ViaVersion +
ViaBackwards on the server, as in tools/e2e.sh.

  1. join: the Bedrock bot (tools/bedrock/bedrock_player.js) joins, is put in creative in the middle of the region
     and is listed in player_positions under its Floodgate name ("." + name), with the first "skin" field;
  2. chat: the bot's chat reaches the headset (chat kind "chat" from the Floodgate name) and the headset's chat_send
     reaches the bot;
  3. floating giant (avatar.bedrock auto): the bot is sent a scaled-up player entity (the giant's Bedrock Mannequin)
     that follows the poses, and the chat bubble (a text display) as a name tag; with --java the Java bot is sent the
     head and hand displays and no Mannequin;
  4. ground giant: the Mannequin is sent to the bot as well;
  5. leave: after the bot quits it drops out of player_positions.

  python tools/bedrock_check.py --url ws://127.0.0.1:8827/tabletop --console tools/run/bedrock/server/stdin.fifo \\
      --bedrock-port 19142 [--java --mc-port 25627] [--report out.json]
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

from tabletop_protocol import CAP_CHAT, Envelope, scripted_pose, validate

HERE = pathlib.Path(__file__).parent


class Headset:
    def __init__(self, url: str, token: str) -> None:
        self.url, self.token, self.env, self.msgs = url, token, Envelope(), []
        self.welcome: dict | None = None

    async def open(self) -> None:
        self.ws = await websockets.connect(self.url, max_size=64 * 1024 * 1024)
        await self.send("hello", client="bedrock-check", client_version="0.7.0", protocols=[1], token=self.token,
                        operator_name="BedrockCheck", caps=["rle_palette_v1", "avatar", CAP_CHAT])
        self.task = asyncio.create_task(self.reader())

    async def send(self, t: str, **kw) -> None:
        m = self.env.wrap(t, **kw)
        validate(m)
        await self.ws.send(json.dumps(m))

    async def reader(self) -> None:
        try:
            async for raw in self.ws:
                m = json.loads(raw)
                validate(m)
                if m["type"] == "welcome":
                    self.welcome = m
                self.msgs.append(m)
        except websockets.ConnectionClosed:
            pass

    async def until(self, pred, timeout: float = 8.0, start: int = 0):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            for m in self.msgs[start:]:
                if pred(m):
                    return m
            await asyncio.sleep(0.02)
        return None

    async def poses(self, seconds: float) -> None:
        start = time.monotonic()
        while time.monotonic() - start < seconds:
            await self.send("pose", **scripted_pose(time.monotonic() - start))
            await asyncio.sleep(1 / 30)


class Bot:
    """A node bot as a subprocess: commands in on stdin, one event per line out on stdout."""

    def __init__(self, node: str, script: pathlib.Path, env: dict[str, str]) -> None:
        self.node, self.script, self.env, self.lines = node, script, env, []

    async def start(self) -> None:
        self.proc = await asyncio.create_subprocess_exec(self.node, self.script.name, cwd=self.script.parent,
                                                         env=dict(os.environ, **self.env), stdin=asyncio.subprocess.PIPE,
                                                         stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT)
        self.task = asyncio.create_task(self.read())

    async def read(self) -> None:
        async for raw in self.proc.stdout:
            self.lines.append(raw.decode(errors="replace").rstrip())

    async def say(self, text: str) -> None:
        self.proc.stdin.write(f"say {text}\n".encode())
        await self.proc.stdin.drain()

    async def saw(self, pred, timeout: float = 10.0, start: int = 0) -> str | None:
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            for line in self.lines[start:]:
                if pred(line):
                    return line
            await asyncio.sleep(0.05)
        return None

    async def quit(self) -> None:
        try:
            self.proc.stdin.write(b"quit\n")
            await self.proc.stdin.drain()
            await asyncio.wait_for(self.proc.wait(), 10)
        except Exception:
            if self.proc.returncode is None:
                self.proc.kill()


def players_named(m: dict, name: str) -> list[dict]:
    return [p for p in m.get("players", []) if p["name"] == name] if m["type"] == "player_positions" else []


async def run(args) -> dict:
    results: dict[str, object] = {}
    fails: list[str] = []

    def check(ok: bool, what: str, detail: object = "") -> None:
        print(("PASS " if ok else "FAIL ") + what + (f"  [{detail}]" if detail != "" else ""))
        results[what] = ok
        if not ok:
            fails.append(what)

    def console(line: str) -> None:
        with open(args.console, "w") as f:
            f.write(line + "\n")

    nonce = str(random.randint(1000, 9999))
    name = "Bedrock" + nonce
    fname = args.prefix + name   # what Java players, the console and the plugin call the Bedrock player

    h = Headset(args.url, args.token)
    await h.open()
    w = await h.until(lambda m: m["type"] == "welcome")
    check(w is not None, "headset welcomed")
    (ox, oy, oz), (dx, dy, dz) = w["region"]["origin"], w["region"]["dims"]
    cx, cy, cz = ox + dx // 2, oy + dy // 2, oz + dz // 2
    console("tabletop avatar remove")   # a giant left over from an earlier run would already be on the bots' screens
    console("tabletop avatar placement floating")

    be = Bot(args.node, HERE / "bedrock" / "bedrock_player.js",
             {"BE_HOST": args.host, "BE_PORT": str(args.bedrock_port), "BOT_NAME": name, "BOT_MS": "150000",
              "BE_TRACE": "1" if args.trace else ""})
    await be.start()
    spawn = await be.saw(lambda line: line.startswith(("SPAWN", "KICKED", "ERROR", "END")), 30)
    check(spawn is not None and spawn.startswith("SPAWN"), "Bedrock bot joined through Geyser", spawn)
    if not spawn or not spawn.startswith("SPAWN"):
        results["fails"] = fails
        return results

    # 1. On the table: creative (it hovers in the air without being kicked for flying), middle of the region.
    console(f"gamemode creative {fname}")
    console(f"tp {fname} {cx} {cy} {cz}")
    pp = await h.until(lambda m: bool(players_named(m, fname)), 10)
    p = players_named(pp, fname)[0] if pp else {}
    check(bool(p), f"Bedrock player in player_positions as {fname}", p.get("uuid", ""))
    check("skin" in p and (p["skin"] is None or p["skin"].startswith("http")),
          "first listing carries skin (null until Floodgate applies one)", p.get("skin", "missing"))

    # 2. Chat both ways.
    await be.say(f"hello headset {nonce}")
    c = await h.until(lambda m: m["type"] == "chat" and m["kind"] == "chat" and m["text"] == f"hello headset {nonce}")
    sender = (c or {}).get("from") or {}
    check(c is not None and sender.get("name") == fname, "Bedrock chat reaches the headset", sender)
    await h.send("chat_send", text=f"hello bedrock {nonce}")
    got = await be.saw(lambda line: line.startswith("MSG") and f"hello bedrock {nonce}" in line)
    check(got is not None, "headset chat reaches the Bedrock player", got)

    jv = None
    if args.java:
        jv = Bot(args.node, HERE / "bot" / "chat_player.js",
                 {"MC_PORT": str(args.mc_port), "BOT_NAME": "Java" + nonce, "BOT_MS": "150000", "BOT_ENTITIES": "1"})
        await jv.start()
        ok = await jv.saw(lambda line: line.startswith(("SPAWN", "KICKED", "ERROR", "END")), 30)
        check(ok is not None and ok.startswith("SPAWN"), "Java bot joined", ok)
        console(f"gamemode creative Java{nonce}")
        console(f"tp Java{nonce} {cx} {cy} {cz}")
        await asyncio.sleep(1)

    # 3. Floating giant: the Bedrock bot gets a Mannequin (add_player) scaled up and moving; Java players don't.
    mark = len(be.lines)
    await h.send("avatar_config", enabled=True, block_size_m=0.01, show_hands=True, name_tag=None)
    await h.poses(4)
    st = await h.until(lambda m: m["type"] == "avatar_state" and m.get("enabled"), 5)
    check(st is not None, "floating giant spawned", st and st.get("scale"))
    body = await be.saw(lambda line: line.startswith("PLAYER") and "scale=" in line, 2, mark)
    rid = body.split()[1] if body else None
    scaled = await be.saw(lambda line: rid is not None and line.startswith(f"DATA {rid} scale=") and float(line.split("=")[1]) > 1, 2, mark)
    moved = await be.saw(lambda line: line == f"MOVED {rid}", 2, mark)
    check(body is not None and scaled is not None and moved is not None, "Bedrock player sees the giant's Mannequin (scaled, moving)",
          f"{body} / {scaled} / {moved}")
    if jv:
        ents = [line.split()[1] for line in jv.lines if line.startswith("ENTITY")]
        check("item_display" in ents and "mannequin" not in ents, "Java player sees the head and hands, not the Bedrock Mannequin",
              sorted(set(ents)))
    # The giant's speech bubble is a text display, which Geyser shows as a name tag.
    await h.send("chat_send", text=f"bubble {nonce}")
    bubble = await be.saw(lambda line: line.startswith(("ENTITY", "DATA")) and f"bubble {nonce}" in line, 3, mark)
    console("tabletop avatar status")   # AVATAR_ENTITY lines in the server log: what the giant is made of
    check(bubble is not None, "Bedrock player sees the giant's chat bubble", bubble)
    await asyncio.sleep(0.5)   # let the last poses land first (spawn-on-first-pose would bring it back)
    await h.send("avatar_config", enabled=False)
    await h.until(lambda m: m["type"] == "avatar_state" and not m.get("enabled"), 5, len(h.msgs))

    # 4. Ground giant: the Mannequin is the giant itself, visible to everyone.
    mark = len(be.lines)
    console("tabletop avatar placement ground")
    await asyncio.sleep(0.5)
    await h.send("avatar_config", enabled=True)
    await h.poses(3)
    body = await be.saw(lambda line: line.startswith("PLAYER"), 2, mark)
    check(body is not None, "ground giant (Mannequin) is sent to the Bedrock player", body)
    await h.send("avatar_config", enabled=False)
    console("tabletop avatar placement floating")

    # 5. Leave (Geyser notices the bot's exit by timeout, about 10 s; a real client says goodbye).
    await be.quit()
    gone = await h.until(lambda m: m["type"] == "player_positions" and not players_named(m, fname), 25, len(h.msgs))
    check(gone is not None, "Bedrock player leaves player_positions after quitting")
    if jv:
        await jv.quit()
    await h.ws.close()
    results["bedrock_lines"] = be.lines if args.trace else len(be.lines)
    results["fails"] = fails
    return results


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default="ws://127.0.0.1:8765/tabletop")
    ap.add_argument("--token", default="")
    ap.add_argument("--console", required=True, help="the server's console FIFO (gamemode, tp, giant placement)")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--bedrock-port", type=int, default=19132, help="Geyser's UDP port")
    ap.add_argument("--prefix", default=".", help="Floodgate's username-prefix")
    ap.add_argument("--java", action="store_true",
                    help="also join a Java bot (tools/bot, needs ViaBackwards) to check what Java players see")
    ap.add_argument("--mc-port", type=int, default=25565)
    ap.add_argument("--node", default="node")
    ap.add_argument("--trace", action="store_true", help="report every Bedrock packet type the bot is sent (bedrock_lines)")
    ap.add_argument("--report")
    args = ap.parse_args()
    res = asyncio.run(run(args))
    if args.report:
        pathlib.Path(args.report).write_text(json.dumps(res, indent=1))
    print("ALL PASS" if not res["fails"] else f"{len(res['fails'])} FAILED: {res['fails']}")
    return 0 if not res["fails"] else 1


if __name__ == "__main__":
    sys.exit(main())
