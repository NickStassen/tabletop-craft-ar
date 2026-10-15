"""Undo/redo, box edits and brushes from the headset against a running plugin (PROTOCOL.md "Undo and redo", "Box edits",
"Brushes"; cap power_v1).

Connects like the Quest (hello caps edit_v1, interact_v1, power_v1; command_v1 with --token for the chest setup), moves
the shared region onto the surface of a flat world (region_request origin [x, -64, z], 64 x 48 x 64) and checks:
  1. welcome grants power_v1 with limits undo_depth, box_max_blocks, brush_max_radius, brush_blocks_per_second;
  2. one group (three places in one "trigger hold") is undone and redone as a whole; a break group comes back;
     history counts follow;
  3. an interact group (a door opened) is undone (closed again, both halves), then the door's placement;
  4. a door broken from its top half comes back with both halves; a chest broken with diamonds in it comes back with
     its diamonds (block entity snapshot), checked with /data get block;
  5. a cell changed by someone else since (a /setblock) is skipped and reported (skipped, skipped_pos); a new edit
     clears redo;
  6. box edits: fill, replace, walls, hollow, clone turned a quarter (stairs facing east come out facing south),
     clear, and undo of a whole box; box_progress reports each; the block cap (edit.box-max-blocks) and a box
     outside the region are refused with readable messages;
  7. the per-tick budget: a 32 x 32 x 32 fill (32768 blocks, the cap) runs over many ticks with box_progress while
     it goes, the server log's "max ... ms in a tick" stays near edit.box-budget-ms, and an undo during it is
     refused ("wait"); the whole fill is undone afterwards (it is bigger than undo_depth but the newest group);
  8. brushes: a sphere painted (81 blocks for radius 2), erased, a bump smoothed away; strokes over
     brush-blocks-per-second are refused (rate_limited);
  9. a second client without power_v1 is refused undo; a fresh one with it has nothing to undo.
Everything it builds is cleared at the end.

  python tools/power_check.py --url ws://127.0.0.1:8822/tabletop --token T [--report out.json] [--at X,Z] [--log server.log]
"""
from __future__ import annotations

import argparse
import asyncio
import json
import re
import sys
import time

import websockets

from tabletop_protocol import (
    CAP_COMMAND,
    CAP_EDIT,
    CAP_INTERACT,
    CAP_POWER,
    POWER_LIMITS,
    Envelope,
    WorldMirror,
    block_edit,
    box_edit_msg,
    brush_msg,
    interact,
    validate,
)

CAPS = ["rle_palette_v1", CAP_EDIT, CAP_INTERACT, CAP_POWER, CAP_COMMAND]
STONE, AIR = "minecraft:stone", "minecraft:air"
DOOR = "minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]"
CHEST = "minecraft:chest[facing=north,type=single,waterlogged=false]"


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
        self.env, self.mirror, self.msgs = Envelope(), WorldMirror(), []
        self.welcome: dict | None = None
        self.history: dict | None = None
        self.last_command = self.last_power = 0.0
        self.invalid: list[str] = []

    async def open(self) -> None:
        self.ws = await websockets.connect(self.url, max_size=32 * 1024 * 1024)
        await self.send("hello", client="power-check", client_version="0.5.0", protocols=[1], token=self.token, caps=self.caps)
        self.task = asyncio.create_task(self.reader())
        self.pings = asyncio.create_task(self.pinger())

    async def pinger(self) -> None:
        """The server closes a link after 15 s without messages (4004): ping every 2 s like the app."""
        n = 0
        while True:
            await asyncio.sleep(2)
            n += 1
            await self.send("ping", id=n)

    async def send(self, t: str, **kw) -> int:
        if t in ("undo", "redo", "box_edit"):   # edit.power-actions-per-second is 5: keep under it
            wait = self.last_power + 0.22 - time.monotonic()
            if wait > 0:
                await asyncio.sleep(wait)
            self.last_power = time.monotonic()
        m = self.env.wrap(t, **kw)
        validate(m)
        await self.ws.send(json.dumps(m))
        return m["seq"]

    async def reader(self) -> None:
        async for raw in self.ws:
            m = json.loads(raw)
            try:
                validate(m)
            except ValueError as e:   # keep reading: a failed check, not a dead client
                self.invalid.append(f"{m.get('type')}: {e}")
                continue
            t = m["type"]
            if t == "welcome":
                self.welcome = m
            elif t == "chunk_snapshot":
                self.mirror.apply_snapshot(m)
            elif t == "block_update":
                self.mirror.apply_block_update(m)
            elif t == "history":
                self.history = m
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
        return [m for m in self.msgs if m["type"] == "error" and (code is None or m["code"] == code)
                and (ref_seq is None or m.get("ref_seq") == ref_seq)]

    def done(self, ref_seq: int) -> dict | None:
        return next((m for m in self.msgs if m["type"] == "box_progress" and m.get("ref_seq") == ref_seq and m["finished"]), None)

    def progress(self, ref_seq: int) -> list[dict]:
        return [m for m in self.msgs if m["type"] == "box_progress" and m.get("ref_seq") == ref_seq]

    async def command(self, line: str, timeout: float = 5.0) -> dict | None:
        wait = self.last_command + 0.55 - time.monotonic()   # commands.max-per-second is 2
        if wait > 0:
            await asyncio.sleep(wait)
        self.last_command = time.monotonic()
        seq = await self.send("command", line=line)
        return await self.until(lambda: next((m for m in self.msgs if m.get("ref_seq") == seq
                                              and m["type"] in ("command_result", "error")), None), timeout)

    async def close(self) -> None:
        self.pings.cancel()
        await self.ws.close()
        self.task.cancel()


async def run(a: argparse.Namespace) -> dict:
    rep: dict = {"checks": [], "ms": {}}

    def check(ok, what: str, **info) -> bool:
        rep["checks"].append({"ok": bool(ok), "what": what, **info})
        print(("PASS " if ok else "FAIL ") + what + (f" {info}" if info else ""), flush=True)
        return bool(ok)

    c = Client(a.url, a.token, CAPS)
    await c.open()
    await c.until(lambda: c.mirror.loaded and c.welcome)
    caps = (c.welcome or {}).get("caps") or []
    lim = (c.welcome or {}).get("limits") or {}
    check(CAP_POWER in caps and all(isinstance(lim.get(k), int) for k in POWER_LIMITS), "welcome grants power_v1 with its limits",
          limits={k: lim.get(k) for k in POWER_LIMITS})
    box_max, depth = lim.get("box_max_blocks", 32768), lim.get("undo_depth", 10000)
    commands = CAP_COMMAND in caps

    X0, Z0 = a.at
    G = -61          # the flat world's grass
    Y = G + 1
    await c.send("region_request", world=c.welcome["world"], origin=[X0, -64, Z0], dims=[64, 48, 64])
    ok = await c.until(lambda: c.mirror.loaded and c.mirror.origin == (X0, -64, Z0) and c.mirror.dims == (64, 48, 64), 20)
    if not check(ok, "region moved to the surface (64 x 48 x 64)", origin=c.mirror.origin, dims=c.mirror.dims):
        return rep

    def P(x: int, z: int, y: int = Y) -> tuple[int, int, int]:
        return (X0 + x, y, Z0 + z)

    def at(p) -> str:
        return c.mirror.at_world(*p)

    group = [100]

    def g() -> int:
        group[0] += 1
        return group[0]

    async def place(p, state: str, grp: int | None, face: str = "up") -> int:
        d = block_edit("place", p, state, face)
        if grp is not None:
            d["group"] = grp
        return await c.send("block_edit", **d)

    async def brk(p, grp: int | None) -> int:
        d = block_edit("break", p, face="up")
        if grp is not None:
            d["group"] = grp
        return await c.send("block_edit", **d)

    async def undo(redo: bool = False, timeout: float = 10) -> dict | None:
        s = await c.send("redo" if redo else "undo")
        return await c.until(lambda: c.done(s) or next(iter(c.errors(ref_seq=s)), None), timeout)

    async def hist(pred, timeout: float = 3) -> dict | None:
        return await c.until(lambda: c.history if c.history and pred(c.history) else None, timeout)

    # A clean bench: the area above the grass cleared in four boxes of 32 x 32 x 32 (what an earlier run left).
    for qx in (0, 32):
        for qz in (0, 32):
            sq = await c.send("box_edit", **box_edit_msg("clear", P(qx, qz), P(qx + 31, qz + 31, Y + 31), group=g()))
            await c.until(lambda sq=sq: c.done(sq) or c.errors(ref_seq=sq), 60)
    await c.until(lambda: c.mirror.count(STONE) == 0, 5)

    # 2. a group of three places, undone and redone as one
    g1 = g()
    row = [P(2, 2), P(3, 2), P(4, 2)]
    for p in row:
        await place(p, STONE, g1)
        await asyncio.sleep(0.06)
    ok = await c.until(lambda: all(at(p) == STONE for p in row))
    h = await hist(lambda h: h["undo"] >= 1 and h["undo_blocks"] >= 3)
    check(ok and h, "three places in one group: history counts 3 blocks", history=c.history)
    t0 = time.perf_counter()
    r = await undo()
    ok = await c.until(lambda: all(at(p) == AIR for p in row))
    rep["ms"]["undo_3"] = round((time.perf_counter() - t0) * 1000)
    check(r and r["type"] == "box_progress" and r["op"] == "undo" and r["changed"] == 3 and ok, "undo takes the whole group back",
          result=r, ms=rep["ms"]["undo_3"])
    await hist(lambda h: h["redo"] >= 1)
    check(c.history and c.history["redo"] == 1 and c.history["redo_blocks"] == 3, "history: one group to redo", history=c.history)
    r = await undo(redo=True)
    check(r and r.get("op") == "redo" and r["changed"] == 3 and await c.until(lambda: all(at(p) == STONE for p in row)),
          "redo puts it back", result=r)
    g2 = g()
    await brk(row[0], g2)
    await asyncio.sleep(0.06)
    await brk(row[1], g2)
    await c.until(lambda: at(row[0]) == AIR and at(row[1]) == AIR)
    r = await undo()
    check(r and r.get("changed") == 2 and await c.until(lambda: at(row[0]) == STONE and at(row[1]) == STONE),
          "a break group comes back", result=r)

    # 3. an interact group: a door opened, undone; then the door's placement undone (both halves)
    door, top = P(8, 2), P(8, 2, Y + 1)
    await place(door, DOOR, g())
    await c.until(lambda: name(at(door)) == "oak_door" and name(at(top)) == "oak_door")
    d = interact(door, "north", dir=(0, -0.3, 1))
    d["group"] = g()
    await c.send("interact", **d)
    opened = await c.until(lambda: prop(at(door), "open") == "true" and prop(at(top), "open") == "true")
    r = await undo()
    check(opened and r and await c.until(lambda: prop(at(door), "open") == "false" and prop(at(top), "open") == "false"),
          "undo of an interact closes the door again (both halves)", result=r)
    r = await undo()
    check(r and await c.until(lambda: at(door) == AIR and at(top) == AIR), "undo of the door's placement takes both halves", result=r)

    # 4. two-block structures and block entities come back
    await place(door, DOOR, g())
    await c.until(lambda: name(at(top)) == "oak_door")
    await brk(top, g())
    gone = await c.until(lambda: at(door) == AIR and at(top) == AIR)
    r = await undo()
    check(gone and r and await c.until(lambda: name(at(door)) == "oak_door" and prop(at(top), "half") == "upper"),
          "a door broken from its top half comes back with both halves", door=at(door), top=at(top))
    chest = P(12, 2)
    await place(chest, CHEST, g())
    await c.until(lambda: name(at(chest)) == "chest")
    if commands:
        x, y, z = chest
        r1 = await c.command(f"item replace block {x} {y} {z} container.0 with minecraft:diamond 7")
        await brk(chest, g())
        await c.until(lambda: at(chest) == AIR)
        r = await undo()
        await c.until(lambda: name(at(chest)) == "chest")
        r2 = await c.command(f"data get block {x} {y} {z} Items")
        text = " ".join(r2.get("lines") or []) if r2 else ""
        check(r1 and r1.get("ok") and r and "diamond" in text and "7" in text, "a broken chest comes back with its diamonds",
              data=text[:160])
        await c.command(f"kill @e[type=item,x={x},y={y},z={z},distance=..4]")
        # a box clear over the full chest: nothing spills (the journal keeps the items), and its undo brings them back
        s = await c.send("box_edit", **box_edit_msg("clear", chest, chest, group=g()))
        await c.until(lambda: c.done(s) or c.errors(ref_seq=s), 10)
        await c.until(lambda: at(chest) == AIR)
        r3 = await c.command(f"execute if entity @e[type=item,x={x},y={y},z={z},distance=..4]")
        spilled = " ".join(r3.get("lines") or []) if r3 else "?"
        r = await undo()
        await c.until(lambda: name(at(chest)) == "chest")
        r4 = await c.command(f"data get block {x} {y} {z} Items")
        text = " ".join(r4.get("lines") or []) if r4 else ""
        check(r3 and not r3.get("ok") and "diamond" in text, "a box cleared over a chest spills nothing; its undo brings the diamonds back",
              spilled=spilled[:80], data=text[:120])
        await c.command(f"item replace block {x} {y} {z} container.0 with minecraft:air")
    else:
        check(True, "chest contents: skipped (no command_v1: pass --token)")

    # 5. someone else changed a cell: skipped and reported; a new edit clears redo
    cell = P(16, 2)
    await place(cell, STONE, g())
    await c.until(lambda: at(cell) == STONE)
    if commands:
        await c.command("setblock {} {} {} minecraft:dirt".format(*cell))
    else:
        c2 = Client(a.url, a.token, ["rle_palette_v1", CAP_EDIT])
        await c2.open()
        await c2.until(lambda: c2.welcome)
        await c2.send("block_edit", **block_edit("break", cell, face="up"))
        await asyncio.sleep(0.2)
        await c2.send("block_edit", **block_edit("place", cell, "minecraft:dirt", "up"))
        await c2.close()
    await c.until(lambda: at(cell) == "minecraft:dirt")
    r = await undo()
    check(r and r.get("skipped") == 1 and r.get("skipped_pos") == [list(cell)] and at(cell) == "minecraft:dirt",
          "a cell changed since is skipped and reported", result=r)
    await brk(cell, g())
    await c.until(lambda: at(cell) == AIR)
    r = await undo()
    await c.until(lambda: at(cell) == "minecraft:dirt")
    await hist(lambda h: h["redo"] >= 1)
    await place(P(17, 2), STONE, g())
    check(await hist(lambda h: h["redo"] == 0), "a new edit clears redo", history=c.history)

    # 6. box edits
    async def box(op: str, lo, hi, timeout: float = 20, **kw) -> dict | None:
        s = await c.send("box_edit", **box_edit_msg(op, lo, hi, group=g(), **kw))
        return await c.until(lambda: c.done(s) or next(iter(c.errors(ref_seq=s)), None), timeout)

    lo, hi = P(2, 20), P(11, 29, Y + 4)   # 10 x 5 x 10
    r = await box("fill", lo, hi, state="minecraft:stone_bricks")
    ok = await c.until(lambda: c.mirror.count("minecraft:stone_bricks") == 500, 5)
    check(r and r.get("changed") == 500 and ok, "fill: 500 stone bricks", result=r)
    r = await box("replace", hi, lo, state="minecraft:glass", filter="minecraft:stone_bricks")
    ok = await c.until(lambda: c.mirror.count("minecraft:glass") == 500 and c.mirror.count("minecraft:stone_bricks") == 0, 5)
    check(r and r.get("changed") == 500 and ok, "replace: stone bricks become glass (corners in any order)", result=r)
    r = await box("clear", lo, hi)
    ok = await c.until(lambda: c.mirror.count("minecraft:glass") == 0, 5)
    check(r and r.get("changed") == 500 and ok, "clear: the box is air", result=r)
    r = await undo()
    ok = await c.until(lambda: c.mirror.count("minecraft:glass") == 500, 5)
    check(r and r.get("changed") == 500 and ok, "undo of a box: the glass is back", result=r)
    r = await box("clear", lo, hi)
    await c.until(lambda: c.mirror.count("minecraft:glass") == 0, 5)

    wl, wh = P(20, 20), P(25, 25, Y + 2)   # 6 x 3 x 6
    r = await box("walls", wl, wh, state="minecraft:oak_planks")
    ok = await c.until(lambda: c.mirror.count("minecraft:oak_planks") == 60, 5)
    check(r and r.get("changed") == 60 and ok and at(P(22, 22, Y + 1)) == AIR, "walls: the four sides, nothing inside", result=r)
    r = await box("clear", wl, wh)
    hl, hh = P(30, 20), P(34, 24, Y + 4)   # 5 x 5 x 5
    await place(P(32, 22, Y + 2), "minecraft:gold_block", g())
    await c.until(lambda: at(P(32, 22, Y + 2)) == "minecraft:gold_block")
    r = await box("hollow", hl, hh, state=STONE)
    ok = await c.until(lambda: at(P(32, 22, Y + 2)) == AIR and at(P(30, 20)) == STONE, 5)
    check(r and ok and c.mirror.count(STONE) >= 98, "hollow: a stone shell, the inside cleared", result=r)
    await box("clear", hl, hh)

    # clone, turned a quarter: (x, z) -> (sz-1-z, x); stairs facing east come out facing south
    src = {P(40, 20): STONE, P(41, 20): "minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]",
           P(42, 20): "minecraft:gold_block", P(40, 21): "minecraft:dirt"}
    for p, s in src.items():
        await place(p, s, g())
        await asyncio.sleep(0.06)
    await c.until(lambda: all(name(at(p)) == name(s) for p, s in src.items()))
    dest = P(40, 30)
    r = await box("clone", P(40, 20), P(42, 21), dest=dest, rotate=1)
    want = {P(41, 30): STONE, P(41, 31): "oak_stairs", P(41, 32): "minecraft:gold_block", P(40, 30): "minecraft:dirt"}
    ok = await c.until(lambda: all(name(at(p)) == name(s) for p, s in want.items()), 5)
    check(r and r.get("changed") == 4 and ok and prop(at(P(41, 31)), "facing") == "south",
          "clone turned a quarter: east-facing stairs face south",
          got={str(p): at(p) for p in want})
    r = await box("clear", P(40, 20), P(42, 21))
    r = await box("clear", P(40, 30), P(41, 32))

    # refusals
    big = await box("fill", P(0, 0), P(39, 39, Y + 20), state=STONE)
    check(big and big["type"] == "error" and big["code"] == "edit_denied" and "box-max-blocks" in big["message"]
          and big.get("op") == "box_edit",
          "a box over the cap is refused with a readable message", error=big)
    out = await box("fill", P(60, 60), P(70, 62), state=STONE)
    check(out and out["type"] == "error" and out["code"] == "out_of_region", "a box outside the region is refused", error=out)
    bad = await box("fill", lo, hi, state="minecraft:command_block")
    check(bad and bad["type"] == "error" and bad["code"] == "edit_denied" and "edit.deny" in bad["message"],
          "edit.deny blocks can't fill a box", error=bad)

    # 7. the per-tick budget on a box at the cap (32 x 32 x 32)
    side = round(box_max ** (1 / 3))
    fl, fh = P(2, 30), P(2 + side - 1, 30 + side - 1, Y + side - 1)
    s = await c.send("box_edit", **box_edit_msg("fill", fl, fh, state=STONE, group=g()))
    running = await c.until(lambda: any(not m["finished"] for m in c.progress(s)), 10)
    us = await c.send("undo")
    busy = await c.until(lambda: c.errors("edit_denied", us), 3)
    t0 = time.perf_counter()
    r = await c.until(lambda: c.done(s), 60)
    rep["ms"]["fill_32768"] = round((time.perf_counter() - t0) * 1000)
    check(running, "box_progress while a big fill runs", progress=[(m["processed"], m["phase"]) for m in c.progress(s)][:4])
    check(busy and "wait" in busy[0]["message"], "undo during the fill is refused (wait)", error=busy and busy[0])
    check(r and r["changed"] == side ** 3 and r["ticks"] > 5, f"the {side ** 3}-block fill finished over many ticks",
          ticks=r and r["ticks"], ms=r and r["ms"])
    await c.until(lambda: c.mirror.count(STONE) >= side ** 3, 10)
    if a.log:
        await asyncio.sleep(0.3)
        with open(a.log, encoding="utf-8", errors="replace") as f:
            lines = [ln for ln in f if "POWER session=" in ln and " fill " in ln and "max " in ln]
        m = re.search(r"main thread ([0-9.]+) ms .* max ([0-9.]+) ms in a tick, (\d+) ticks over", lines[-1]) if lines else None
        busy, worst, slow = (float(m.group(1)), float(m.group(2)), int(m.group(3))) if m else (None, None, None)
        rep["ms"]["fill_main_thread"], rep["ms"]["fill_max_tick"] = busy, worst
        check(m and slow <= 1, "the fill keeps to edit.box-budget-ms per tick (at most one tick over 1.5x: a GC pause can land in one)",
              main_thread_ms=busy, worst_ms=worst, slow_ticks=slow, budget=a.budget)
    r = await undo(timeout=60)
    ok = await c.until(lambda: c.mirror.count(STONE) < 200, 10)
    check(r and r.get("changed") == side ** 3 and ok, f"the whole fill is undone ({side ** 3} blocks, more than undo_depth {depth})",
          result=r and {k: r.get(k) for k in ("changed", "skipped", "ticks", "ms", "message")})

    # 8. brushes
    centre = P(30, 50, Y + 4)

    async def stroke(op: str, p, radius: int, timeout: float = 3, **kw) -> int:
        return await c.send("brush", **brush_msg(op, p, radius, group=g(), **kw))

    await stroke("paint", centre, 2, state="minecraft:moss_block")
    ok = await c.until(lambda: c.mirror.count("minecraft:moss_block") == 81, 4)
    check(ok, "a radius 2 sphere paints 81 blocks", got=c.mirror.count("minecraft:moss_block"))
    await stroke("erase", centre, 2)
    check(await c.until(lambda: c.mirror.count("minecraft:moss_block") == 0, 4), "erase takes them away")
    bump = P(30, 56)
    await place(bump, "minecraft:dirt", g())
    await c.until(lambda: at(bump) == "minecraft:dirt")
    await stroke("smooth", bump, 2)
    check(await c.until(lambda: at(bump) == AIR, 4), "smooth flattens a lone bump on flat ground", now=at(bump))
    seqs = [await stroke("paint", P(10, 50, Y + 8), 8, state="minecraft:glass") for _ in range(10)]
    limited = await c.until(lambda: [e for s in seqs for e in c.errors("rate_limited", s)], 4)
    check(limited and limited[0].get("op") == "brush", "strokes over brush-blocks-per-second are refused",
          first=limited and limited[0]["message"])
    await asyncio.sleep(1.2)
    await c.send("box_edit", **box_edit_msg("clear", P(2, 42, Y), P(18, 58, Y + 16), group=g()))
    await asyncio.sleep(1.5)

    # 9. other clients
    b = Client(a.url, a.token, ["rle_palette_v1", CAP_EDIT])
    await b.open()
    await b.until(lambda: b.welcome)
    s = await b.send("undo")
    e = await b.until(lambda: b.errors("edit_denied", s), 3)
    check(CAP_POWER not in (b.welcome.get("caps") or []) and e and e[0].get("op") == "undo", "no power_v1: undo is refused",
          error=e and e[0]["message"])
    await b.close()
    f = Client(a.url, a.token, ["rle_palette_v1", CAP_EDIT, CAP_POWER])
    await f.open()
    await f.until(lambda: f.welcome)
    s = await f.send("undo")
    e = await f.until(lambda: f.errors("edit_denied", s), 3)
    check(e and e[0]["message"] == "nothing to undo", "a fresh session has nothing to undo (journals are per session)",
          error=e and e[0]["message"])
    await f.close()

    # clean up what's left
    for p in row + [P(12, 2), P(16, 2), P(17, 2), door, top]:
        if c.mirror.loaded and at(p) not in (AIR,):
            await brk(p, None)
            await asyncio.sleep(0.06)
    await asyncio.sleep(0.5)
    await c.close()
    check(not c.invalid, "every message from the server validates (tabletop_protocol.validate)", invalid=c.invalid[:3])
    if a.log:
        with open(a.log, encoding="utf-8", errors="replace") as fh:
            text = fh.read()
        check("POWER_NEIGHBOURS on" in text, "server log: box edits update their neighbours (the bridge is on)")
    return rep


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default="ws://127.0.0.1:8765/tabletop")
    ap.add_argument("--token", default="")
    ap.add_argument("--report")
    ap.add_argument("--at", default="0,0", help="X,Z of the region's north-west corner on a flat world (default 0,0)")
    ap.add_argument("--log", help="the server's log file: also checks the per-tick budget and the neighbour bridge")
    ap.add_argument("--budget", type=float, default=2.0, help="the server's edit.box-budget-ms (default 2)")
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
