"""Block editing from the headset against a running plugin (PROTOCOL.md 4.20).

Connects like the Quest (hello caps edit_v1), finds the surface block in the middle of the region by looking down
through air and water (as the headset's ray does), then:
  1. breaks it and waits for the block_update, puts it back, and places a gold block on top;
  2. checks that a second, fresh connection sees the gold block in its snapshot (the world really changed) and that
     it can't edit without edit_v1;
  3. error paths: occupied cell, outside the region, unknown state, a deny-listed block, a malformed face;
  4. a burst of 40 edits: about 20 pass the rate limit (edit.max-per-second), the rest get rate_limited;
  5. breaks the gold block again;
  6. block menu (PROTOCOL.md 4.24): the block_registry arrived between welcome and the first snapshot; places a
     registry block that isn't in the region yet (diamond block, so it comes back through palette_add), checks a fresh
     connection's snapshot has it, refuses a state that isn't in the registry (fire) with bad_state, then breaks it;
  7. building like vanilla, on a stone platform it builds high in the region and removes again: short grass and a
     single snow layer are replaced, two layers aren't; placed leaves are persistent and a waterlogged slab placed in
     air is dry; doors, beds and tall grass get both halves, breaking one half removes both, a door needs room above
     (and inside the region); fences, panes, bars, walls and stairs connect to their neighbours; with --fifo (the
     server console) a cow in the cell refuses a stone but not a torch, and edits outside a small world border are
     refused. Refusals carry readable names ("gold block is in the way").
  The world ends as it started (apart from water that flowed).

  python tools/edit_check.py --url ws://127.0.0.1:8765/tabletop [--token T] [--report out.json] [--fifo stdin.fifo]
"""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time

import websockets

from tabletop_protocol import CAP_EDIT, Envelope, WorldMirror, block_edit, registry_id, registry_states, validate

SKIP = {"air", "cave_air", "void_air", "barrier", "light", "structure_void", "water", "lava", "bubble_column"}


def name(state: str) -> str:
    return state.split("[", 1)[0].split(":", 1)[-1]


class Client:
    def __init__(self, url: str, token: str, caps: list[str]) -> None:
        self.url, self.token, self.caps = url, token, caps
        self.env, self.mirror, self.msgs = Envelope(), WorldMirror(), []
        self.welcome: dict | None = None
        self.registry: dict | None = None
        self.registry_bytes = 0
        self.order: list[str] = []   # first message types, to check welcome -> block_registry -> chunk_snapshot

    async def open(self) -> None:
        self.ws = await websockets.connect(self.url, max_size=8 * 1024 * 1024)
        await self.send("hello", client="edit-check", client_version="0.1.0", protocols=[1], token=self.token, caps=self.caps)
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
            if len(self.order) < 3:
                self.order.append(m["type"])
            if m["type"] == "welcome":
                self.welcome = m
            elif m["type"] == "block_registry":
                self.registry, self.registry_bytes = m, len(raw.encode() if isinstance(raw, str) else raw)
            elif m["type"] == "chunk_snapshot":
                self.mirror.apply_snapshot(m)
            elif m["type"] == "block_update":
                self.mirror.apply_block_update(m)
            self.msgs.append(m)

    async def until(self, pred, timeout: float = 5.0):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            r = pred()
            if r:
                return r
            await asyncio.sleep(0.005)
        return None

    def errors(self, code: str | None = None, pos=None, ref_seq: int | None = None) -> list[dict]:
        return [m for m in self.msgs if m["type"] == "error" and (code is None or m["code"] == code)
                and (pos is None or m.get("pos") == list(pos)) and (ref_seq is None or m.get("ref_seq") == ref_seq)]

    async def close(self) -> None:
        await self.ws.close()
        self.task.cancel()


async def run(a: argparse.Namespace) -> dict:
    rep: dict = {"checks": []}

    def check(ok: bool, what: str, **info) -> None:
        rep["checks"].append({"ok": bool(ok), "what": what, **info})
        print(("PASS " if ok else "FAIL ") + what + (f" {info}" if info else ""), flush=True)

    c = Client(a.url, a.token, ["rle_palette_v1", CAP_EDIT])
    await c.open()
    await c.until(lambda: c.mirror.loaded and c.welcome)
    caps = (c.welcome or {}).get("caps") or []
    check(CAP_EDIT in caps, "welcome grants edit_v1", caps=caps)
    m = c.mirror
    ox, oy, oz = m.origin
    dx, dy, dz = m.dims
    lx, lz = dx // 2, dz // 2
    ly = next((y for y in range(dy - 1, -1, -1) if name(m.palette[m.grid[m.index(lx, y, lz)]]) not in SKIP), None)
    if ly is None or ly + 2 >= dy:
        check(False, "found a surface block with room above it in the middle column")
        return rep
    pos, above = (ox + lx, oy + ly, oz + lz), (ox + lx, oy + ly + 1, oz + lz)
    original = m.at_world(*pos)
    rep["surface"] = {"pos": pos, "state": original, "above": m.at_world(*above)}

    t0 = time.perf_counter()
    await c.send("block_edit", **block_edit("break", pos, face="up"))
    got = await c.until(lambda: m.at_world(*pos) != original and m.at_world(*pos))
    check(got is not None and name(got) in ("air", "water"), "break -> block_update", pos=pos, was=original, now=got,
          ms=round((time.perf_counter() - t0) * 1000))
    t0 = time.perf_counter()
    await c.send("block_edit", **block_edit("place", pos, original, "up"))
    got = await c.until(lambda: m.at_world(*pos) == original)
    check(got is not None, "place the original back -> block_update", ms=round((time.perf_counter() - t0) * 1000))
    t0 = time.perf_counter()
    await c.send("block_edit", **block_edit("place", above, "minecraft:gold_block", "up"))
    got = await c.until(lambda: m.at_world(*above) == "minecraft:gold_block")
    check(got is not None, "place gold_block -> block_update", pos=above,
          ms=round((time.perf_counter() - t0) * 1000))

    # A fresh connection reads the world itself: the gold block is really there. Without edit_v1 it can't edit.
    b = Client(a.url, a.token, ["rle_palette_v1"])
    await b.open()
    await b.until(lambda: b.mirror.loaded)
    check(b.mirror.loaded and b.mirror.at_world(*above) == "minecraft:gold_block", "second connection's snapshot has the gold block")
    check(CAP_EDIT not in (b.welcome.get("caps") or []), "no edit_v1 granted without asking")
    await b.send("block_edit", **block_edit("break", above))
    check(await b.until(lambda: b.errors("edit_denied", above)) is not None, "edit without edit_v1 -> edit_denied")
    await b.close()

    cases = [
        ("edit_denied", block_edit("place", above, "minecraft:stone", "up")),                 # occupied by gold
        ("out_of_region", block_edit("place", (ox - 1, oy + ly, oz), "minecraft:stone")),
        ("bad_state", block_edit("place", (above[0], above[1] + 1, above[2]), "minecraft:not_a_block")),
        ("edit_denied", block_edit("place", (above[0], above[1] + 1, above[2]), "minecraft:command_block")),
    ]
    for code, payload in cases:
        await c.send("block_edit", **payload)
        check(await c.until(lambda code=code, payload=payload: c.errors(code, payload["pos"])) is not None,
              f"{payload['op']} {payload.get('state')} at {payload['pos']} -> {code}")
    occ = c.errors("edit_denied", above)
    check(bool(occ) and occ[0]["message"] == "gold block is in the way", "refusals name blocks readably", message=occ and occ[0]["message"])
    bad = c.env.wrap("block_edit", op="place", pos=list(above), state="minecraft:stone", face="sideways")
    await c.ws.send(json.dumps(bad))
    check(await c.until(lambda: c.errors("bad_message", ref_seq=bad["seq"])) is not None, "bad face -> bad_message")

    await asyncio.sleep(1.2)   # let the bucket refill
    n0 = len(c.errors("edit_denied", above))
    for _ in range(40):
        await c.send("block_edit", **block_edit("place", above, "minecraft:stone", "up"))
    await c.until(lambda: len(c.errors("rate_limited", above)) + len(c.errors("edit_denied", above)) - n0 >= 40)
    limited, passed = len(c.errors("rate_limited", above)), len(c.errors("edit_denied", above)) - n0
    check(15 <= limited <= 22 and limited + passed == 40, "burst of 40 edits: about 20 rate_limited", rate_limited=limited, passed=passed)

    await asyncio.sleep(1.1)
    t0 = time.perf_counter()
    await c.send("block_edit", **block_edit("break", above, face="up"))
    got = await c.until(lambda: m.at_world(*above) != "minecraft:gold_block" and m.at_world(*above))
    check(got is not None, "break gold_block again", now=got, ms=round((time.perf_counter() - t0) * 1000))

    await registry_checks(c, check, above, a, rep)
    await vanilla_checks(c, check, a, rep)
    await c.close()
    rep["ok"] = all(x["ok"] for x in rep["checks"])
    return rep


async def registry_checks(c: Client, check, pos, a: argparse.Namespace, rep: dict) -> None:
    """Block menu (PROTOCOL.md 4.24): place a registry block the region doesn't contain yet, refuse a non-registry one."""
    reg = c.registry
    check(c.order[:3] == ["welcome", "block_registry", "chunk_snapshot"], "block_registry between welcome and the first snapshot",
          order=c.order)
    cats = {x["id"]: len(x["blocks"]) for x in (reg or {}).get("categories", [])}
    check(reg is not None and reg["count"] > 500, "block_registry lists the placeable blocks",
          count=reg and reg["count"], bytes=c.registry_bytes, categories=cats)
    if reg is None:
        return
    rep["registry"] = {"count": reg["count"], "bytes": c.registry_bytes, "variants": len(reg.get("variants") or {})}
    states = registry_states(reg)
    m = c.mirror
    present = {registry_id(s) for s in m.palette}
    want = next((b for b in ("minecraft:diamond_block", "minecraft:emerald_block", "minecraft:lapis_block", "minecraft:sponge")
                 if b in states and b not in present), None)
    check(want is not None, "a registry block that isn't in the region", block=want)
    if want is None:
        return
    state = states[want]
    t0 = time.perf_counter()
    await c.send("block_edit", **block_edit("place", pos, state, "up"))
    got = await c.until(lambda: m.at_world(*pos) == state)
    check(got is not None and state in m.palette, f"place {want} from the registry -> block_update with palette_add",
          pos=pos, ms=round((time.perf_counter() - t0) * 1000))
    b = Client(a.url, a.token, ["rle_palette_v1"])
    await b.open()
    await b.until(lambda: b.mirror.loaded)
    check(b.mirror.loaded and b.mirror.at_world(*pos) == state, f"fresh connection's snapshot has the {want}")
    await b.close()
    above = (pos[0], pos[1] + 1, pos[2])
    fire = "minecraft:fire[age=0,east=false,north=false,south=false,up=false,west=false]"
    seq = await c.send("block_edit", **block_edit("place", above, fire, "up"))
    err = await c.until(lambda: c.errors("bad_state", above, ref_seq=seq))
    check(err is not None and "block_registry" in err[0]["message"], "place a state that isn't in the registry (fire) -> bad_state",
          message=err and err[0]["message"])
    await c.send("block_edit", **block_edit("break", pos, face="up"))
    got = await c.until(lambda: m.at_world(*pos) != state and m.at_world(*pos))
    check(got is not None, f"break the {want} again", now=got)


async def edit(c: Client, op: str, pos, state: str | None = None, face: str = "up") -> int:
    return await c.send("block_edit", **block_edit(op, pos, state, face))


async def state_at(c: Client, pos, pred, timeout: float = 3.0):
    """Waits until the mirror's state at pos satisfies pred (a state string or a predicate); returns it or None."""
    test = pred if callable(pred) else (lambda s: s == pred)
    return await c.until(lambda: (lambda s: s if s and test(s) else None)(c.mirror.at_world(*pos)), timeout)


async def refused(c: Client, seq: int, code: str = "edit_denied", timeout: float = 3.0):
    err = await c.until(lambda: [m for m in c.msgs if m["type"] == "error" and m.get("ref_seq") == seq], timeout)
    return err[0] if err and err[0]["code"] == code else None


def shifted(p, dx=0, dy=0, dz=0):
    return (p[0] + dx, p[1] + dy, p[2] + dz)


async def vanilla_checks(c: Client, check, a: argparse.Namespace, rep: dict) -> None:
    """Building like vanilla 26.3 (PROTOCOL.md 4.20) on a stone platform built high in the region, removed afterwards:
    replaceable cells, placed states (leaves, waterlogging), two-block structures, connections to neighbours, and with
    --fifo (the server console) mobs in the way and the world border."""
    m = c.mirror
    ox, oy, oz = m.origin
    dx, dy, dz = m.dims
    if dy < 12:
        check(False, "region tall enough for the building checks", dims=m.dims)
        return
    # A 5x5 stone platform 8 blocks under the region's top, at the region's centre (all air there: checked).
    y0 = oy + dy - 8
    x0, z0 = ox + dx // 2 - 2, oz + dz // 2 - 2
    cells = [(x0 + i, y0 + k, z0 + j) for i in range(5) for j in range(5) for k in range(0, 5)]
    if any(name(m.at_world(*p)) not in ("air", "cave_air") for p in cells):
        check(False, "free space for the building checks", at=(x0, y0, z0))
        return
    await asyncio.sleep(1.1)   # a full rate-limit bucket (edit.max-per-second)
    A = (x0 + 1, y0 + 1, z0 + 1)   # on the platform, over its one grass block (plants need it)
    floor = [(x0 + i, y0, z0 + j) for i in range(5) for j in range(5)]
    want = {p: "minecraft:grass_block[snowy=false]" if p == shifted(A, dy=-1) else "minecraft:stone" for p in floor}
    for k, p in enumerate(floor):
        if k == 18:
            await asyncio.sleep(1.0)
        await edit(c, "place", p, want[p])
    ok = await c.until(lambda: all(m.at_world(*p) == want[p] for p in floor), 5)
    check(ok is not None, "stone platform for the building checks", at=(x0, y0, z0))
    if ok is None:
        return
    await asyncio.sleep(1.1)
    try:
        await replaceable_checks(c, check, A)
        await asyncio.sleep(1.1)
        await state_checks(c, check, A)
        await asyncio.sleep(1.1)
        await two_block_checks(c, check, A, (x0 + 3, oy + dy - 1, z0 + 3))
        await asyncio.sleep(1.1)
        await connection_checks(c, check, A, rep)
        await asyncio.sleep(1.1)
        if a.fifo:
            await console_checks(c, check, A, a.fifo)
        else:
            print("SKIP mobs in the way and the world border (no --fifo)", flush=True)
    finally:
        # Clear everything above the platform, then the platform.
        await asyncio.sleep(1.1)
        left = [p for p in cells if p[1] > y0 and name(m.at_world(*p)) not in ("air", "cave_air")]
        for k, p in enumerate(left + floor):
            if k and k % 18 == 0:
                await asyncio.sleep(1.0)
            await edit(c, "break", p)
        gone = await c.until(lambda: all(name(m.at_world(*p)) in ("air", "cave_air") for p in cells), 5)
        check(gone is not None, "building area cleared again")


async def replaceable_checks(c: Client, check, A) -> None:
    m = c.mirror
    await edit(c, "place", A, "minecraft:short_grass")
    check(await state_at(c, A, "minecraft:short_grass") is not None, "short grass placed on the platform")
    await edit(c, "place", A, "minecraft:stone")
    check(await state_at(c, A, "minecraft:stone") is not None, "a block placed into short grass replaces it (vanilla canBeReplaced)")
    await edit(c, "break", A)
    await state_at(c, A, lambda s: name(s) == "air")
    seq = await edit(c, "place", shifted(A, dx=1), "minecraft:short_grass")
    err = await refused(c, seq)
    check(err is not None and err["message"] == "short grass can't stay there", "short grass on stone is refused (vanilla canSurvive)",
          message=err and err["message"])
    await edit(c, "place", A, "minecraft:snow[layers=1]")
    await state_at(c, A, "minecraft:snow[layers=1]")
    await edit(c, "place", A, "minecraft:oak_planks")
    check(await state_at(c, A, "minecraft:oak_planks") is not None, "a single snow layer is replaced")
    await edit(c, "break", A)
    await state_at(c, A, lambda s: name(s) == "air")
    await edit(c, "place", A, "minecraft:snow[layers=2]")
    await state_at(c, A, "minecraft:snow[layers=2]")
    seq = await edit(c, "place", A, "minecraft:oak_planks")
    err = await refused(c, seq)
    check(err is not None and err["message"] == "snow is in the way", "two snow layers are not replaced (readable refusal)",
          message=err and err["message"])
    await edit(c, "break", A)
    await state_at(c, A, lambda s: name(s) == "air")
    check(name(m.at_world(*A)) == "air", "replaceable checks cleaned up")


async def state_checks(c: Client, check, A) -> None:
    await edit(c, "place", A, "minecraft:oak_leaves[distance=7,persistent=false,waterlogged=false]")
    got = await state_at(c, A, lambda s: name(s) == "oak_leaves")
    check(got is not None and "persistent=true" in got, "placed leaves are persistent (no decay)", state=got)
    await edit(c, "break", A)
    await state_at(c, A, lambda s: name(s) == "air")
    await edit(c, "place", A, "minecraft:oak_slab[type=bottom,waterlogged=true]")
    got = await state_at(c, A, lambda s: name(s) == "oak_slab")
    check(got is not None and "waterlogged=false" in got, "a waterlogged slab placed in air is dry (no water source from a pick)",
          state=got)
    await edit(c, "break", A)
    await state_at(c, A, lambda s: name(s) == "air")


async def two_block_checks(c: Client, check, A, top) -> None:
    m = c.mirror
    up = shifted(A, dy=1)
    door = "minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]"
    await edit(c, "place", A, door)
    ok = await c.until(lambda: m.at_world(*A) == door and m.at_world(*up) == door.replace("half=lower", "half=upper"))
    check(ok is not None, "door: both halves placed", lower=m.at_world(*A), upper=m.at_world(*up))
    await edit(c, "break", up, face="north")
    ok = await c.until(lambda: name(m.at_world(*A)) == "air" and name(m.at_world(*up)) == "air")
    check(ok is not None, "door: breaking the top half removes both", lower=m.at_world(*A), upper=m.at_world(*up))
    await edit(c, "place", A, door.replace("half=lower", "half=upper"))
    ok = await c.until(lambda: m.at_world(*A) == door and m.at_world(*up) == door.replace("half=lower", "half=upper"))
    check(ok is not None, "door sent as its top half is placed from the bottom (both halves)")
    await edit(c, "break", A)
    await c.until(lambda: name(m.at_world(*A)) == "air" and name(m.at_world(*up)) == "air")
    bed = "minecraft:red_bed[facing=east,occupied=false,part=foot]"
    east = shifted(A, dx=1)
    await edit(c, "place", A, bed)
    ok = await c.until(lambda: m.at_world(*A) == bed and m.at_world(*east) == bed.replace("part=foot", "part=head"))
    check(ok is not None, "bed: foot and head placed", foot=m.at_world(*A), head=m.at_world(*east))
    await edit(c, "break", east)
    ok = await c.until(lambda: name(m.at_world(*A)) == "air" and name(m.at_world(*east)) == "air")
    check(ok is not None, "bed: breaking the head removes the foot")
    await edit(c, "place", A, "minecraft:tall_grass[half=lower]")
    ok = await c.until(lambda: m.at_world(*A) == "minecraft:tall_grass[half=lower]"
                       and m.at_world(*up) == "minecraft:tall_grass[half=upper]")
    check(ok is not None, "tall grass: both halves placed")
    await edit(c, "place", A, "minecraft:oak_planks")
    ok = await c.until(lambda: m.at_world(*A) == "minecraft:oak_planks" and name(m.at_world(*up)) == "air")
    check(ok is not None, "replacing tall grass removes its top half too", top=m.at_world(*up))
    await edit(c, "break", A)
    await state_at(c, A, lambda s: name(s) == "air")
    await edit(c, "place", up, "minecraft:stone")
    await state_at(c, up, "minecraft:stone")
    seq = await edit(c, "place", A, door)
    err = await refused(c, seq)
    check(err is not None and err["message"].startswith("no room for the oak door"), "door without room above: refused",
          message=err and err["message"])
    await edit(c, "break", up)
    await state_at(c, up, lambda s: name(s) == "air")
    # At the region's top layer the other half would be outside the region.
    base = shifted(top, dy=-1)
    if name(m.at_world(*top)) == "air" and name(m.at_world(*base)) == "air":
        await edit(c, "place", base, "minecraft:stone")
        await state_at(c, base, "minecraft:stone")
        seq = await edit(c, "place", top, door)
        err = await refused(c, seq)
        check(err is not None and "outside the area" in err["message"], "door on the region's top layer: refused (top half outside)",
              message=err and err["message"])
        await edit(c, "break", base)
        await state_at(c, base, lambda s: name(s) == "air")


async def connection_checks(c: Client, check, A, rep: dict) -> None:
    """Placed fences, panes, walls, bars and stairs take their shape from their neighbours (vanilla getStateForPlacement)."""
    m = c.mirror
    east = shifted(A, dx=1)
    found = {}
    for block, side in (("minecraft:oak_fence", "west=true"), ("minecraft:glass_pane", "west=true"),
                        ("minecraft:iron_bars", "west=true"), ("minecraft:cobblestone_wall", "west=low")):
        await edit(c, "place", A, block)
        await state_at(c, A, lambda s, b=block: s.startswith(b))
        await edit(c, "place", east, block)
        got = await state_at(c, east, lambda s, b=block: s.startswith(b))
        first = await state_at(c, A, lambda s, b=block: s.startswith(b) and "east=" in s and "east=false" not in s and "east=none" not in s)
        found[block] = got
        check(got is not None and side in got and first is not None, f"{name(block)} placed next to another connects (both ways)",
              placed=got, neighbour=m.at_world(*A))
        await edit(c, "break", east)
        await edit(c, "break", A)
        await c.until(lambda: name(m.at_world(*A)) == "air" and name(m.at_world(*east)) == "air")
        await asyncio.sleep(0.6)
    # Beside a block that doesn't change itself (a fence pair also connects through the neighbour's own update, so
    # this is the case that needs the placed block's shape computed: plugin 0.3.0 left this fence unconnected).
    await edit(c, "place", east, "minecraft:stone")
    await state_at(c, east, "minecraft:stone")
    await edit(c, "place", A, "minecraft:oak_fence")
    got = await state_at(c, A, lambda s: s.startswith("minecraft:oak_fence"))
    found["oak_fence beside stone"] = got
    check(got is not None and "east=true" in got, "a fence placed beside a stone block connects to it", placed=got)
    await edit(c, "break", A)
    await edit(c, "break", east)
    await c.until(lambda: name(m.at_world(*A)) == "air" and name(m.at_world(*east)) == "air")
    north = shifted(A, dz=-1)
    await edit(c, "place", north, "minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]")
    await state_at(c, north, lambda s: name(s) == "oak_stairs")
    await edit(c, "place", A, "minecraft:oak_stairs[facing=north,half=bottom,shape=straight,waterlogged=false]")
    got = await state_at(c, A, lambda s: name(s) == "oak_stairs")
    found["oak_stairs"] = got
    check(got is not None and "shape=outer_right" in got, "stairs placed against a perpendicular stair take the corner shape", placed=got)
    rep["connections"] = found
    await edit(c, "break", A)
    await edit(c, "break", north)
    await c.until(lambda: name(m.at_world(*A)) == "air" and name(m.at_world(*north)) == "air")


def console(fifo: str, line: str) -> None:
    with open(fifo, "w") as f:
        f.write(line + "\n")


async def console_checks(c: Client, check, A, fifo: str) -> None:
    console(fifo, f"summon minecraft:cow {A[0] + 0.5} {A[1]} {A[2] + 0.5} {{NoAI:1b,Silent:1b,Tags:[\"edit_check\"]}}")
    await asyncio.sleep(1.0)
    seq = await edit(c, "place", A, "minecraft:stone")
    err = await refused(c, seq)
    check(err is not None and err["message"] == "a cow is in the way", "a solid block can't go into a mob (readable refusal)",
          message=err and err["message"])
    await edit(c, "place", A, "minecraft:torch")
    check(await state_at(c, A, "minecraft:torch") is not None, "a torch (no collision) can go where the mob stands")
    await edit(c, "break", A)
    console(fifo, "kill @e[type=minecraft:cow,tag=edit_check]")
    await state_at(c, A, lambda s: name(s) == "air")
    await asyncio.sleep(0.5)
    # World border: a 3-block border around A; a cell 4 blocks east is outside it.
    out = shifted(A, dx=3)
    console(fifo, f"worldborder center {A[0] + 0.5} {A[2] + 0.5}")
    console(fifo, "worldborder set 3")
    await asyncio.sleep(1.0)
    try:
        seq = await edit(c, "place", out, "minecraft:stone")
        err = await refused(c, seq)
        check(err is not None and err["message"] == "outside the world border", "no edits outside the world border",
              message=err and err["message"])
        await edit(c, "place", A, "minecraft:stone")
        check(await state_at(c, A, "minecraft:stone") is not None, "edits inside the world border still work")
        await edit(c, "break", A)
        await state_at(c, A, lambda s: name(s) == "air")
    finally:
        console(fifo, "worldborder center 0 0")
        console(fifo, "worldborder set 59999968")
        await asyncio.sleep(0.5)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default="ws://127.0.0.1:8765/tabletop")
    ap.add_argument("--token", default="")
    ap.add_argument("--report")
    ap.add_argument("--fifo", help="the server console (a FIFO, as tools/e2e.sh makes): adds the mob-in-the-way and world border checks")
    a = ap.parse_args()
    rep = asyncio.run(run(a))
    if a.report:
        with open(a.report, "w") as f:
            json.dump(rep, f, indent=2)
    print("ALL PASS" if rep.get("ok") else "FAILED")
    return 0 if rep.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
