"""Helpers for the tabletop link protocol v1 (docs/PROTOCOL.md)."""
from __future__ import annotations

import math
import re
import time
from dataclasses import dataclass, field

PROTOCOL_VERSION = 1
SECTIONS = "sections_v1"   # chunk_snapshot encoding and hello cap (PROTOCOL.md 4.18)
SECTION = 16

# Required payload fields per message type, on top of the envelope (v, type, seq, t).
REQUIRED = {
    "hello": ["client", "protocols"],
    "welcome": ["protocol", "server", "server_version", "mc_version", "session", "world", "region", "limits", "avatar_supported"],
    "subscribe": ["origin", "dims"],
    "chunk_snapshot": ["snapshot_id", "world", "origin", "dims", "order", "palette", "encoding"],
    "block_update": ["snapshot_id", "changes"],
    "player_positions": ["players"],
    "pose": ["head"],
    "avatar_config": [],
    "avatar_state": ["enabled", "entity_uuid", "scale", "world_height_blocks"],
    "ping": ["id"],
    "pong": ["id", "echo_t"],
    "error": ["code", "message"],
    "bye": [],
    "region_request": [],
    "region_query": [],
    "region_changed": ["world", "origin", "dims", "by_session"],
    "region_info": ["region", "players", "limits"],
    "entities": ["full", "entities"],
    "actions": ["actions"],
    "block_edit": ["op", "pos"],
    "block_registry": ["count", "categories"],
    "block_entities": ["entities"],
}
# LAN discovery over UDP (PROTOCOL.md 7): not WebSocket messages, so no envelope. Required fields per type.
DISCOVERY = {
    "tabletop_discover": [],
    "tabletop_server": ["name", "port", "path", "token"],
}
DISCOVERY_PORT = 8765
# block_registry categories (PROTOCOL.md 4.24), in tab order.
REGISTRY_CATEGORIES = ("building", "colored", "natural", "functional", "redstone", "other")
ERROR_CODES = {"version_mismatch", "auth_failed", "bad_message", "unknown_type", "unsupported", "region_too_large",
               "world_not_found", "bad_snapshot", "rate_limited", "skin_failed", "internal",
               "edit_denied", "out_of_region", "bad_state"}   # the last three: block_edit (PROTOCOL.md 4.20)
# Optional snapshot extras a client can ask for in hello.caps (PROTOCOL.md 4.4.1).
CAP_LIGHT, CAP_BIOMES = "light_v1", "biomes_v1"
# Non-player entities in "entities" messages (PROTOCOL.md 4.21).
CAP_ENTITIES = "entities_v1"
# Swings, digging, block breaks/places and damage in "actions" messages (PROTOCOL.md 4.22).
CAP_ACTIONS = "actions_v1"
ACTION_KINDS = {"swing", "dig", "break", "place", "hurt"}
EQUIPMENT_SLOTS = {"main_hand", "off_hand", "head", "chest", "legs", "feet", "body", "saddle"}
STAND_PARTS = {"head", "body", "left_arm", "right_arm", "left_leg", "right_leg"}
PLAYER_POSES = {"standing", "sneaking", "swimming", "sleeping", "gliding", "riding", "spin_attack", "dying"}
# Block editing from the headset (PROTOCOL.md 4.20): block_edit, granted in welcome.caps.
CAP_EDIT = "edit_v1"
FACES = ("west", "east", "down", "up", "north", "south")
# Block entity data (PROTOCOL.md 4.29): sign text, banner patterns, heads, sherds, spawners, beacons, items, conduits.
CAP_BLOCK_ENTITIES = "block_entities_v1"
BLOCK_ENTITY_KINDS = {"sign", "banner", "skull", "decorated_pot", "spawner", "trial_spawner", "beacon", "campfire", "shelf",
                      "vault", "conduit"}
DYES = {"white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue",
        "brown", "green", "red", "black"}

# [talk] Chat, commands and server status (PROTOCOL.md 4.26-4.28).
CAP_CHAT, CAP_COMMAND, CAP_STATUS = "chat_v1", "command_v1", "status_v1"
CHAT_KINDS = ("chat", "join", "quit", "death", "advancement", "broadcast", "say", "me", "operator", "whisper")
CONTROL_REQUIRED = {
    "chat": ["kind", "text"],
    "chat_send": ["text"],
    "command": ["line"],
    "command_result": ["ref_seq", "ok", "ms", "lines"],
    "command_output": ["ref_seq", "lines"],
    "command_complete": ["line"],
    "command_suggestions": ["ref_seq", "start", "items"],
    "server_status": ["tps", "mspt", "memory", "players", "tick", "worlds"],
    "world_query": [],
    "world_info": ["world", "env", "time", "day", "weather", "difficulty", "spawn", "tick", "gamerules"],
    "players_query": [],
    "players_info": ["players"],
}
REQUIRED.update(CONTROL_REQUIRED)
ERROR_CODES |= {"chat_denied", "command_denied"}
WEATHERS = ("clear", "rain", "thunder")

# [hands] Using blocks, items and mobs (PROTOCOL.md "Using blocks and items", "Acting on mobs"; cap interact_v1).
CAP_INTERACT = "interact_v1"
ENTITY_OPS = ("attack", "kill", "remove", "use", "name", "move", "hold", "drop", "freeze", "unfreeze", "glow", "info")
ITEM_CATEGORIES = ("use", "spawn_eggs")
LIGHTNING_ITEM = "tabletop:lightning"
INTERACT_REQUIRED = {
    "interact": ["pos", "face"],
    "item_registry": ["count", "categories"],
    "entity_op": ["uuid", "op"],
    "entity_info": ["uuid", "id", "entity_type", "pos"],
}
REQUIRED.update(INTERACT_REQUIRED)
ERROR_CODES |= {"interact_denied", "not_found"}
_ID = re.compile(r"^[a-z0-9_.\-]+:[a-z0-9_./\-]+$")

# [power] Undo/redo, box edits and brushes (PROTOCOL.md "Undo and redo", "Box edits", "Brushes"; cap power_v1).
CAP_POWER = "power_v1"
BOX_OPS = ("fill", "replace", "clear", "walls", "hollow", "clone")
BRUSH_OPS = ("paint", "replace", "erase", "smooth")
BRUSH_SHAPES = ("sphere", "cylinder")
JOB_OPS = BOX_OPS + ("undo", "redo")
JOB_PHASES = ("read", "write", "fixup", "done", "failed")
POWER_LIMITS = ("undo_depth", "box_max_blocks", "brush_max_radius", "brush_blocks_per_second")
POWER_REQUIRED = {
    "undo": [],
    "redo": [],
    "history": ["undo", "redo", "undo_blocks", "redo_blocks"],
    "box_edit": ["op", "from", "to"],
    "box_progress": ["op", "phase", "processed", "total", "changed", "skipped", "finished"],
    "brush": ["op", "center", "radius"],
}
REQUIRED.update(POWER_REQUIRED)
_STATE = re.compile(r"^[a-z0-9_.\-]+(:[a-z0-9_./\-]+)?(\[[a-z0-9_=,]*\])?$")

# [living] Time of day and weather (PROTOCOL.md "World state: time and weather"), display entities.
CAP_WORLD = "world_v1"
REQUIRED["world_state"] = ["world", "dimension", "skylight", "time", "day_time", "rain", "thunder"]
DIMENSIONS = ("overworld", "the_nether", "the_end", "custom")
BILLBOARDS = ("fixed", "vertical", "horizontal", "center")

# [sound] Spatial sound (PROTOCOL.md "Sound"; cap sound_v1) and asset delivery ("Asset delivery"; cap assets_v1).
CAP_SOUND, CAP_ASSETS = "sound_v1", "assets_v1"
REQUIRED["sound_groups"] = ["count", "groups"]
REQUIRED["assets"] = ["chunk", "files"]
REQUIRED["asset_get"] = ["name", "offset", "length"]
ACTION_KINDS |= {"sound", "sound_stop"}
ERROR_CODES |= {"asset_denied"}
ASSET_NAMES = ("entity_models.json", "sounds.zip", "pack.zip")
ASSET_MAX_CHUNK = 1 << 20
SOUND_GROUP_KEYS = ("break", "step", "place", "hit", "fall")
_SOUND_ID = re.compile(r"^([a-z0-9_.\-]+:)?[a-z0-9_./\-]+$")

# [operator] Marks (bookmarks, spawn, worlds; cap marks_v1), containers and signs (interact_v1). PROTOCOL.md "Marks:
# bookmarks, spawn and worlds", "Containers", "Signs".
CAP_MARKS = "marks_v1"
OPERATOR_REQUIRED = {
    "bookmark": ["op", "name"],
    "bookmarks": ["max", "marks"],
    "container_query": ["pos"],
    "container": ["pos"],
    "container_edit": ["pos"],
    "container_close": ["pos"],
    "sign_edit": ["pos", "side", "lines"],
}
REQUIRED.update(OPERATOR_REQUIRED)
ERROR_CODES |= {"mark_denied"}
WORLD_ENVS = ("normal", "nether", "the_end", "custom")


def validate(msg: dict) -> None:
    t = msg.get("type")
    if t in DISCOVERY:
        validate_discovery(msg)
        return
    if msg.get("v") != PROTOCOL_VERSION:
        raise ValueError(f"bad protocol version in {t}: {msg.get('v')}")
    if t not in REQUIRED:
        raise ValueError(f"unknown message type {t}")
    for k in ("seq", "t"):
        if not isinstance(msg.get(k), int):
            raise ValueError(f"{t} envelope field {k} missing or not int")
    missing = [k for k in REQUIRED[t] if k not in msg]
    if t == "chunk_snapshot":
        # rle_palette_v1 carries "data"; sections_v1 (PROTOCOL.md 4.18) carries "sections", "part" and "parts".
        enc = msg.get("encoding")
        if enc == "rle_palette_v1":
            missing += [k for k in ("data",) if k not in msg]
        elif enc == SECTIONS:
            missing += [k for k in ("sections", "part", "parts") if k not in msg]
        else:
            raise ValueError(f"unknown snapshot encoding {enc}")
    if missing:
        raise ValueError(f"{t} missing {missing}")
    if t == "error" and msg["code"] not in ERROR_CODES:
        raise ValueError(f"unknown error code {msg['code']}")
    if t in ("welcome", "region_info") and not isinstance(msg["limits"].get("max_view_width"), int):
        raise ValueError(f"{t} limits need max_view_width (PROTOCOL.md 4.25)")
    if t == "region_changed" and "limits" in msg:
        # next version: region_changed carries the new world's limits, build_height included (PROTOCOL.md 4.13)
        bh = msg["limits"].get("build_height")
        if bh is not None and not (isinstance(bh, list) and len(bh) == 2 and all(isinstance(v, int) for v in bh) and bh[0] < bh[1]):
            raise ValueError(f"region_changed limits.build_height must be [minY, maxY), got {bh}")
    if t == "player_positions":
        for p in msg["players"]:
            if p.get("skin_model") not in (None, "wide", "slim"):
                raise ValueError(f"player_positions skin_model must be wide or slim, got {p['skin_model']}")
            if p.get("pose") not in (None, *PLAYER_POSES):
                raise ValueError(f"player_positions pose {p['pose']} is not one of {sorted(PLAYER_POSES)}")
            _check_equipment(p.get("equipment"))
    if t == "entities":
        for e in msg["entities"]:
            if not isinstance(e.get("id"), int):
                raise ValueError(f"entities entry without an int id: {e}")
            if msg["full"] and ("type" not in e or "pos" not in e):
                raise ValueError(f"full entities entry {e['id']} needs type and pos")
            if "pos" in e and len(e["pos"]) != 3:
                raise ValueError(f"entities entry {e['id']} pos must have 3 numbers")
            _check_equipment(e.get("equipment"))
            _check_stand_pose(e.get("stand_pose"))
            _check_living(e)   # [living]
        if not all(isinstance(i, int) for i in msg.get("removed") or []):
            raise ValueError("entities removed must be a list of ids")
    if t == "actions":
        for a in msg["actions"]:
            k = a.get("kind")
            if k not in ACTION_KINDS:
                raise ValueError(f"unknown action kind {k}")
            if k in ("swing", "hurt") and not isinstance(a.get("id"), int):
                raise ValueError(f"{k} action needs an entity id")
            if k == "swing" and a.get("hand") not in ("main", "off"):
                raise ValueError("swing hand must be main or off")
            if k in ("dig", "break", "place") and (len(a.get("pos") or []) != 3 or not all(isinstance(v, int) for v in a["pos"])):
                raise ValueError(f"{k} action needs an integer block pos")
            if k == "dig" and not -1 <= a.get("stage", -2) <= 9:
                raise ValueError("dig stage must be -1..9")
            if k in ("break", "place") and not isinstance(a.get("block"), str):
                raise ValueError(f"{k} action needs a block state")
            if k == "sound" and (not isinstance(a.get("sound"), str) or not _vec(a.get("pos"))
                                 or not isinstance(a.get("volume"), (int, float)) or not 0.5 <= a.get("pitch", 0) <= 2.0):
                raise ValueError(f"sound action needs a sound id, pos, volume and a pitch 0.5..2: {a}")   # [sound]
            if k == "sound_stop" and not _int_pos(a.get("pos")):
                raise ValueError("sound_stop needs an integer block pos")
    if t == "block_edit":
        validate_block_edit(msg)
    if t == "block_registry":
        validate_block_registry(msg)
    if t == "block_entities":
        validate_block_entities(msg)
    if t in CONTROL_REQUIRED:
        validate_control(msg)   # [talk]
    if t in INTERACT_REQUIRED:
        validate_interact(msg)   # [hands]
    if t in OPERATOR_REQUIRED:
        validate_operator(msg)   # [operator]
    if t in ("welcome", "region_info") and "worlds" in msg:
        validate_worlds(msg["worlds"])   # [operator]
    if t == "region_info" and "spawn" in msg and not _ints(msg["spawn"], 3):
        raise ValueError("region_info spawn must be [x, y, z] integers")
    if t == "welcome" and "interact" in msg and not isinstance(msg["interact"], dict):
        raise ValueError("welcome interact must be an object")
    if t in ("sound_groups", "assets", "asset_get"):
        validate_sound(msg)   # [sound]
    if t == "world_state":
        validate_world_state(msg)   # [living]
    if t in POWER_REQUIRED or t in ("block_edit", "interact") or (t == "welcome" and CAP_POWER in (msg.get("caps") or [])):
        validate_power(msg)   # [power]


def _group_ok(v) -> bool:
    return v is None or (isinstance(v, int) and not isinstance(v, bool) and 0 <= v <= 0xFFFFFFFF)


def validate_power(msg: dict) -> None:
    """[power] group on block_edit and interact; undo, redo, history, box_edit, box_progress, brush; welcome limits."""
    t = msg["type"]
    if not _group_ok(msg.get("group")):
        raise ValueError(f"{t} group must be a non-negative integer")
    if t == "welcome":
        lim = msg["limits"]
        for k in POWER_LIMITS:
            if not isinstance(lim.get(k), int) or lim[k] < 0:
                raise ValueError(f"a power_v1 welcome needs limits.{k} (a non-negative integer)")
    elif t == "history":
        if not all(isinstance(msg[k], int) and msg[k] >= 0 for k in POWER_REQUIRED["history"]):
            raise ValueError("history counts must be non-negative integers")
    elif t == "box_edit":
        op = msg["op"]
        if op not in BOX_OPS:
            raise ValueError(f"box_edit op must be one of {BOX_OPS}")
        if not (_ints(msg["from"], 3) and _ints(msg["to"], 3)):
            raise ValueError("box_edit from and to must be [x, y, z] integers")
        if op not in ("clear", "clone") and not (isinstance(msg.get("state"), str) and _STATE.match(msg["state"])):
            raise ValueError(f"box_edit {op} needs a block state")
        if op == "replace" and not (isinstance(msg.get("filter"), str) and _STATE.match(msg["filter"])):
            raise ValueError("box_edit replace needs a filter")
        if op == "clone":
            if not _ints(msg.get("dest"), 3):
                raise ValueError("box_edit clone needs dest [x, y, z]")
            if msg.get("rotate", 0) not in (0, 1, 2, 3):
                raise ValueError("box_edit rotate must be 0..3 quarter turns")
    elif t == "box_progress":
        if msg["op"] not in JOB_OPS or msg["phase"] not in JOB_PHASES:
            raise ValueError(f"box_progress op {msg['op']} / phase {msg['phase']} unknown")
        if not isinstance(msg["finished"], bool) or msg["processed"] > msg["total"]:
            raise ValueError("box_progress finished must be a bool and processed <= total")
        for p in msg.get("skipped_pos") or []:
            if not _ints(p, 3):
                raise ValueError("box_progress skipped_pos entries must be [x, y, z]")
    elif t == "brush":
        op = msg["op"]
        if op not in BRUSH_OPS or msg.get("shape", "sphere") not in BRUSH_SHAPES:
            raise ValueError(f"brush op must be one of {BRUSH_OPS} and shape one of {BRUSH_SHAPES}")
        if not _ints(msg["center"], 3) or not isinstance(msg["radius"], int) or msg["radius"] < 0:
            raise ValueError("brush needs center [x, y, z] and a radius >= 0")
        if op in ("paint", "replace") and not (isinstance(msg.get("state"), str) and _STATE.match(msg["state"])):
            raise ValueError(f"brush {op} needs a block state")
        if op == "replace" and not isinstance(msg.get("filter"), str):
            raise ValueError("brush replace needs a filter")


def box_edit_msg(op: str, a, b, state: str | None = None, filter: str | None = None, dest=None, rotate: int = 0,
                 group: int | None = None) -> dict:
    """[power] box_edit payload; wrap it with Envelope.wrap("box_edit", **payload)."""
    d: dict = {"op": op, "from": [int(v) for v in a], "to": [int(v) for v in b]}
    if state is not None:
        d["state"] = state
    if filter is not None:
        d["filter"] = filter
    if dest is not None:
        d["dest"] = [int(v) for v in dest]
        d["rotate"] = rotate
    if group is not None:
        d["group"] = group
    return d


def brush_msg(op: str, center, radius: int, shape: str = "sphere", state: str | None = None, filter: str | None = None,
              height: int | None = None, group: int | None = None) -> dict:
    """[power] brush payload; wrap it with Envelope.wrap("brush", **payload)."""
    d: dict = {"op": op, "shape": shape, "center": [int(v) for v in center], "radius": radius}
    for k, v in (("state", state), ("filter", filter), ("height", height), ("group", group)):
        if v is not None:
            d[k] = v
    return d


def _vec(v) -> bool:
    return isinstance(v, list) and len(v) == 3 and all(isinstance(x, (int, float)) and not isinstance(x, bool) for x in v)


def validate_interact(msg: dict) -> None:
    """[hands] interact, item_registry, entity_op and entity_info."""
    t = msg["type"]
    if t == "interact":
        if not _ints(msg["pos"], 3):
            raise ValueError("interact pos must be [x, y, z] integers")
        if msg["face"] not in FACES:
            raise ValueError(f"interact face must be one of {FACES}")
        item = msg.get("item")
        if item is not None and not (isinstance(item, str) and _ID.match(item)):
            raise ValueError(f"interact item must be a namespaced item id, got {item!r}")
        hit = msg.get("hit")
        if hit is not None and not (_vec(hit) and all(-0.01 <= v <= 1.01 for v in hit)):
            raise ValueError("interact hit must be [x, y, z] inside the block")
        if msg.get("dir") is not None and not _vec(msg["dir"]):
            raise ValueError("interact dir must be [x, y, z]")
    elif t == "item_registry":
        cats = msg["categories"]
        if [c.get("id") for c in cats] != list(ITEM_CATEGORIES):
            raise ValueError(f"item_registry categories must be {ITEM_CATEGORIES}")
        seen: set[str] = set()
        for c in cats:
            if not _strings(c.get("items")) or not all(_ID.match(i) for i in c["items"]):
                raise ValueError(f"item_registry {c['id']} needs a list of item ids")
            for i in c["items"]:
                if i in seen:
                    raise ValueError(f"item_registry lists {i} twice")
                seen.add(i)
        if not all(i.endswith("_spawn_egg") for i in cats[1]["items"]):
            raise ValueError("item_registry spawn_eggs must only list spawn eggs")
        if msg["count"] != len(seen):
            raise ValueError(f"item_registry count {msg['count']} but {len(seen)} items listed")
    elif t == "entity_op":
        if not isinstance(msg["uuid"], str) or len(msg["uuid"]) != 36:
            raise ValueError("entity_op uuid must be a UUID string")
        op = msg["op"]
        if op not in ENTITY_OPS:
            raise ValueError(f"entity_op op must be one of {ENTITY_OPS}")
        if op in ("move", "hold") and not _vec(msg.get("to")):
            raise ValueError(f"entity_op {op} needs to [x, y, z]")
        if op == "name" and not isinstance(msg.get("text"), str):
            raise ValueError("entity_op name needs text")
        if msg.get("item") is not None and not (isinstance(msg["item"], str) and _ID.match(msg["item"])):
            raise ValueError("entity_op item must be a namespaced item id")
    elif t == "entity_info":
        if not _vec(msg["pos"]) or not isinstance(msg["entity_type"], str):
            raise ValueError("entity_info needs entity_type and pos [x, y, z]")
        h = msg.get("health")
        if h is not None and not (isinstance(h, list) and len(h) == 2):
            raise ValueError("entity_info health must be [health, max]")


def validate_worlds(worlds) -> None:
    """[operator] welcome.worlds / region_info.worlds: name, env, build height [min_y, max_y), spawn."""
    if not isinstance(worlds, list) or not worlds:
        raise ValueError("worlds must be a non-empty list")
    for w in worlds:
        if not isinstance(w.get("name"), str) or w.get("env") not in WORLD_ENVS:
            raise ValueError(f"world entry {w} needs a name and env in {WORLD_ENVS}")
        if not (isinstance(w.get("min_y"), int) and isinstance(w.get("max_y"), int) and w["min_y"] < w["max_y"]):
            raise ValueError(f"world {w.get('name')} needs min_y < max_y")
        if not _ints(w.get("spawn"), 3):
            raise ValueError(f"world {w.get('name')} spawn must be [x, y, z] integers")


def validate_operator(msg: dict) -> None:
    """[operator] bookmark, bookmarks, container_query, container, container_edit, container_close, sign_edit."""
    t = msg["type"]
    if t == "bookmark":
        if msg["op"] not in ("add", "remove"):
            raise ValueError("bookmark op must be add or remove")
        if not isinstance(msg["name"], str) or not msg["name"].strip() or len(msg["name"]) > 64:
            raise ValueError("bookmark name must be 1..32 characters")
        if msg["op"] == "add" and not (isinstance(msg.get("world"), str) and _ints(msg.get("pos"), 3)):
            raise ValueError("bookmark add needs world and pos [x, y, z] integers")
    elif t == "bookmarks":
        names = set()
        for k in msg["marks"]:
            if not isinstance(k.get("name"), str) or not isinstance(k.get("world"), str) or not _ints(k.get("pos"), 3):
                raise ValueError(f"bookmarks entry {k} needs name, world and pos")
            if k["name"].lower() in names:
                raise ValueError(f"bookmarks lists {k['name']} twice")
            names.add(k["name"].lower())
        if len(msg["marks"]) > msg["max"]:
            raise ValueError("bookmarks lists more than max")
    elif t in ("container_query", "container_close"):
        if not _ints(msg["pos"], 3):
            raise ValueError(f"{t} pos must be [x, y, z] integers")
    elif t == "container":
        if not _ints(msg["pos"], 3):
            raise ValueError("container pos must be [x, y, z] integers")
        if msg.get("closed"):
            if not isinstance(msg.get("reason"), str):
                raise ValueError("a closed container needs a reason")
            return
        for k in ("block", "title", "size", "items", "edit"):
            if k not in msg:
                raise ValueError(f"container missing {k}")
        if msg["edit"] not in ("edit", "view") or not isinstance(msg["size"], int) or msg["size"] < 1:
            raise ValueError("container edit must be edit or view, size >= 1")
        if msg.get("other") is not None and not _ints(msg["other"], 3):
            raise ValueError("container other must be [x, y, z] integers")
        slots = set()
        for e in msg["items"]:
            if not (isinstance(e, list) and len(e) == 3 and isinstance(e[0], int) and 0 <= e[0] < msg["size"]
                    and isinstance(e[1], str) and _ID.match(e[1]) and isinstance(e[2], int) and e[2] > 0):
                raise ValueError(f"container item {e} must be [slot, item id, count > 0] inside size")
            if e[0] in slots:
                raise ValueError(f"container lists slot {e[0]} twice")
            slots.add(e[0])
    elif t == "container_edit":
        if not _ints(msg["pos"], 3):
            raise ValueError("container_edit pos must be [x, y, z] integers")
        op = msg.get("op", "set")
        if op not in ("set", "sort"):
            raise ValueError("container_edit op must be set or sort")
        if op == "set":
            if not isinstance(msg.get("slot"), int) or not 0 <= msg["slot"] <= 255:
                raise ValueError("container_edit set needs slot 0..255")
            item = msg.get("item")
            if item is not None and not (isinstance(item, str) and _ID.match(item)):
                raise ValueError("container_edit item must be a namespaced item id or null")
            c = msg.get("count", 0 if item is None else 1)
            if not isinstance(c, int) or not 0 <= c <= 127:
                raise ValueError("container_edit count must be 0..127")
            was = msg.get("was")
            if was is not None and not (isinstance(was, list) and len(was) == 2 and (was[0] is None or _ID.match(str(was[0])))
                                        and isinstance(was[1], int)):
                raise ValueError("container_edit was must be [item or null, count]")
    elif t == "sign_edit":
        if not _ints(msg["pos"], 3) or msg["side"] not in ("front", "back"):
            raise ValueError("sign_edit needs pos [x, y, z] and side front or back")
        if not (isinstance(msg["lines"], list) and len(msg["lines"]) == 4 and all(isinstance(x, str) for x in msg["lines"])):
            raise ValueError("sign_edit lines must be 4 strings")
        if msg.get("color") is not None and msg["color"] not in DYES:
            raise ValueError(f"sign_edit color must be a dye colour, got {msg['color']}")
        if msg.get("glowing") is not None and not isinstance(msg["glowing"], bool):
            raise ValueError("sign_edit glowing must be a boolean")


def bookmark(op: str, name: str, world: str | None = None, pos=None) -> dict:
    """[operator] bookmark payload; wrap it with Envelope.wrap("bookmark", **payload)."""
    d: dict = {"op": op, "name": name}
    if world is not None:
        d["world"] = world
    if pos is not None:
        d["pos"] = [int(v) for v in pos]
    return d


def container_edit(pos, slot: int | None = None, item: str | None = None, count: int = 0, was=None, op: str = "set") -> dict:
    """[operator] container_edit payload: set slot to count x item (None or 0 empties it), or op "sort"."""
    d: dict = {"pos": [int(v) for v in pos]}
    if op != "set":
        d["op"] = op
        return d
    d.update({"slot": slot, "item": item, "count": count})
    if was is not None:
        d["was"] = list(was)
    return d


def container_slots(msg: dict) -> dict[int, tuple[str, int]]:
    """[operator] A container message's slots: slot -> (item, count)."""
    return {e[0]: (e[1], e[2]) for e in msg.get("items", [])}


def interact(pos, face: str, item: str | None = None, sneak: bool = False, hit=None, dir=None) -> dict:
    """[hands] interact payload; wrap it with Envelope.wrap("interact", **payload)."""
    d: dict = {"pos": [int(v) for v in pos], "face": face, "item": item, "sneak": sneak}
    if hit is not None:
        d["hit"] = [float(v) for v in hit]
    if dir is not None:
        d["dir"] = [float(v) for v in dir]
    return d


def entity_op(uuid: str, op: str, **fields) -> dict:
    """[hands] entity_op payload (to, yaw, dir, item, text as keyword fields)."""
    return {"uuid": uuid, "op": op, **fields}


def _int_pos(p) -> bool:
    return isinstance(p, list) and len(p) == 3 and all(isinstance(v, int) for v in p)


def validate_block_entities(msg: dict) -> None:
    """block_entities (PROTOCOL.md 4.29): entries by kind with integer world positions, removed positions, reset."""
    if "reset" in msg and not isinstance(msg["reset"], bool):
        raise ValueError("block_entities reset must be a boolean")
    for e in msg["entities"]:
        k = e.get("kind")
        if k not in BLOCK_ENTITY_KINDS:
            raise ValueError(f"unknown block entity kind {k}")
        if not _int_pos(e.get("pos")):
            raise ValueError(f"{k} entry needs an integer world pos")
        if k == "sign":
            for side in ("front", "back"):
                s = e.get(side)
                if not isinstance(s, dict) or len(s.get("lines") or []) != 4 or not all(isinstance(x, str) for x in s["lines"]):
                    raise ValueError(f"sign {side} needs 4 lines")
                if s.get("color") not in DYES or not isinstance(s.get("glowing"), bool):
                    raise ValueError(f"sign {side} needs a dye colour and glowing")
        if k == "banner" and not all(isinstance(p.get("pattern"), str) and p.get("color") in DYES for p in e.get("patterns") or [None]):
            raise ValueError("banner patterns need a pattern key and a dye colour")
        if k == "decorated_pot" and (len(e.get("sherds") or []) != 4 or not all(isinstance(x, str) for x in e["sherds"])):
            raise ValueError("decorated_pot needs 4 sherds (back, left, right, front)")
        if k in ("spawner", "trial_spawner") and not isinstance(e.get("entity"), str):
            raise ValueError(f"{k} needs an entity type")
        if k == "beacon" and not 0 <= e.get("tier", -1) <= 4:
            raise ValueError("beacon tier must be 0..4")
        if k in ("campfire", "shelf") and (len(e.get("items") or []) != (4 if k == "campfire" else 3)):
            raise ValueError(f"{k} needs one item (or null) per slot")
        if k == "vault" and not isinstance(e.get("item"), str):
            raise ValueError("vault needs an item")
    if not all(_int_pos(p) for p in msg.get("removed") or []):
        raise ValueError("block_entities removed must be integer positions")


def _ints(v, n: int) -> bool:
    return isinstance(v, list) and len(v) == n and all(isinstance(x, int) and not isinstance(x, bool) for x in v)


def _strings(v) -> bool:
    return isinstance(v, list) and all(isinstance(x, str) for x in v)


def validate_control(msg: dict) -> None:
    """[talk] chat, commands and status messages (PROTOCOL.md 4.26-4.28)."""
    t = msg["type"]
    if t == "chat":
        if msg["kind"] not in CHAT_KINDS:
            raise ValueError(f"chat kind {msg['kind']} is not one of {CHAT_KINDS}")
        f = msg.get("from")
        if f is not None and not (isinstance(f, dict) and isinstance(f.get("name"), str)):
            raise ValueError("chat from needs a name")
        if not isinstance(msg["text"], str):
            raise ValueError("chat text must be a string")
    elif t == "chat_send":
        if not isinstance(msg["text"], str) or not msg["text"].strip() or len(msg["text"]) > 256:
            raise ValueError("chat_send text must be 1..256 characters")
    elif t in ("command", "command_complete"):
        line = msg["line"]
        if not isinstance(line, str) or len(line) > 256 or any(ord(c) < 32 for c in line):
            raise ValueError(f"{t} line must be one line of at most 256 characters")
        if t == "command" and not line.strip().lstrip("/").strip():
            raise ValueError("command line is empty")
        at = msg.get("at")
        if at is not None and not (isinstance(at, dict) and _ints(at.get("pos"), 3)):
            raise ValueError("command at needs pos [x, y, z] integers")
    elif t in ("command_result", "command_output"):
        if not _strings(msg["lines"]):
            raise ValueError(f"{t} lines must be strings")
        if t == "command_result" and not isinstance(msg["ok"], bool):
            raise ValueError("command_result ok must be a boolean")
    elif t == "command_suggestions":
        if not isinstance(msg["start"], int) or msg["start"] < 0 or not _strings(msg["items"]) or len(msg["items"]) > 50:
            raise ValueError("command_suggestions needs start >= 0 and at most 50 string items")
    elif t == "server_status":
        if len(msg["tps"]) != 3 or not all(isinstance(x, (int, float)) for x in msg["tps"]):
            raise ValueError("server_status tps must be [1m, 5m, 15m]")
        for w in msg["worlds"]:
            if not isinstance(w.get("name"), str) or w.get("weather") not in WEATHERS:
                raise ValueError(f"server_status world entry {w} needs a name and weather")
    elif t == "world_info":
        if msg["weather"] not in WEATHERS or not _ints(msg["spawn"], 3) or not isinstance(msg["gamerules"], dict):
            raise ValueError("world_info needs weather, spawn [x, y, z] and a gamerules object")
        for k, v in msg["gamerules"].items():
            if not isinstance(k, str) or not isinstance(v, (bool, int, float, str)):
                raise ValueError(f"world_info gamerule {k} has a bad value {v!r}")
    elif t == "players_info":
        for p in msg["players"]:
            for k in ("name", "uuid", "world", "gamemode"):
                if not isinstance(p.get(k), str):
                    raise ValueError(f"players_info entry needs {k}")
            if len(p.get("pos") or []) != 3:
                raise ValueError("players_info pos must have 3 numbers")


def suggestion_apply(line: str, start: int, item: str) -> str:
    """[talk] The line after accepting a command_suggestions item: everything from start is replaced."""
    return line[:start] + item


def validate_discovery(msg: dict) -> None:
    """LAN discovery over UDP (PROTOCOL.md 7): the headset's probe and a plugin's reply. No envelope (seq, t)."""
    if msg.get("v") != PROTOCOL_VERSION:
        raise ValueError(f"bad protocol version in {msg.get('type')}: {msg.get('v')}")
    for k in DISCOVERY[msg["type"]]:
        if k not in msg:
            raise ValueError(f"{msg['type']} missing {k}")
    if msg["type"] == "tabletop_server":
        if not (isinstance(msg["port"], int) and 0 < msg["port"] < 65536):
            raise ValueError(f"tabletop_server port must be 1..65535, got {msg['port']}")
        if not (isinstance(msg["path"], str) and msg["path"].startswith("/")):
            raise ValueError(f"tabletop_server path must start with /, got {msg['path']}")
        if not isinstance(msg["token"], bool):
            raise ValueError("tabletop_server token must be true or false")


def validate_block_registry(msg: dict) -> None:
    """block_registry (PROTOCOL.md 4.24): categories with default states, count, items and variants maps."""
    cats = msg["categories"]
    if not isinstance(cats, list) or not all(isinstance(c, dict) and isinstance(c.get("id"), str) for c in cats):
        raise ValueError("block_registry categories must be objects with an id")
    seen: set[str] = set()
    for c in cats:
        blocks = c.get("blocks")
        if not isinstance(blocks, list) or not all(isinstance(s, str) and s.strip() for s in blocks):
            raise ValueError(f"block_registry category {c['id']} needs a list of block states")
        for s in blocks:
            bid = registry_id(s)
            if bid in seen:
                raise ValueError(f"block_registry lists {bid} twice")
            seen.add(bid)
    if msg["count"] != len(seen):
        raise ValueError(f"block_registry count {msg['count']} but {len(seen)} blocks listed")
    for k in ("items", "variants"):
        v = msg.get(k)
        if v is not None and not (isinstance(v, dict) and all(isinstance(a, str) and isinstance(b, str) for a, b in v.items())):
            raise ValueError(f"block_registry {k} must map block ids to ids")
    for var, home in (msg.get("variants") or {}).items():
        if home not in seen:
            raise ValueError(f"block_registry variant {var} points at {home}, which isn't listed")


def registry_id(state: str) -> str:
    """Block id of a registry entry (its default state): 'minecraft:oak_log[axis=y]' -> 'minecraft:oak_log'."""
    return state.split("[", 1)[0]


def registry_states(msg: dict) -> dict[str, str]:
    """block_registry -> {block id: default state}, in menu order."""
    return {registry_id(s): s for c in msg["categories"] for s in c["blocks"]}


def _check_stand_pose(pose: dict | None) -> None:
    """An armour stand's stand_pose (PROTOCOL.md 4.21): every part once, [x, y, z] degrees."""
    if pose is None:
        return
    if set(pose) != STAND_PARTS or not all(isinstance(v, list) and len(v) == 3 for v in pose.values()):
        raise ValueError(f"stand_pose needs [x, y, z] for each of {sorted(STAND_PARTS)}, got {pose}")


def _check_equipment(eq: dict | None) -> None:
    if eq is not None and not set(eq) <= EQUIPMENT_SLOTS:
        raise ValueError(f"unknown equipment slots {sorted(set(eq) - EQUIPMENT_SLOTS)}")


def _numbers(v, n: int) -> bool:
    return isinstance(v, list) and len(v) == n and all(isinstance(x, (int, float)) and not isinstance(x, bool) for x in v)


def _check_living(e: dict) -> None:
    """[living] Leads, gear and looks of an entities entry (PROTOCOL.md "Living entities")."""
    for k in ("leash", "owner"):
        if e.get(k) is not None and not (isinstance(e[k], int) and not isinstance(e[k], bool)):
            raise ValueError(f"entities {k} must be an entity id, got {e[k]}")
    if "peek" in e and not 0 <= e["peek"] <= 1:
        raise ValueError(f"shulker peek must be 0..1, got {e['peek']}")
    if e.get("attach") is not None and e["attach"] not in FACES:
        raise ValueError(f"shulker attach must be one of {FACES}, got {e['attach']}")
    if "xp" in e and not (isinstance(e["xp"], int) and e["xp"] >= 0):
        raise ValueError(f"experience orb xp must be a non-negative int, got {e['xp']}")
    d = e.get("display")
    if d is None:
        return
    for k, n in (("translation", 3), ("left_rotation", 4), ("scale", 3), ("right_rotation", 4)):
        if not _numbers(d.get(k), n):
            raise ValueError(f"display {k} needs {n} numbers, got {d.get(k)}")
    if d.get("billboard") not in BILLBOARDS:
        raise ValueError(f"display billboard must be one of {BILLBOARDS}, got {d.get('billboard')}")
    if "brightness" in d and not (_ints(d["brightness"], 2) and all(0 <= v <= 15 for v in d["brightness"])):
        raise ValueError(f"display brightness must be [block, sky] 0..15, got {d['brightness']}")
    if "text" in d:
        if not isinstance(d["text"], str) or d.get("align") not in ("center", "left", "right"):
            raise ValueError("text display needs text and align center|left|right")
        if not (isinstance(d.get("line_width"), int) and isinstance(d.get("background"), int) and 0 <= d.get("text_opacity", -1) <= 255):
            raise ValueError("text display needs line_width, background (ARGB) and text_opacity 0..255")


def validate_world_state(msg: dict) -> None:
    """[living] world_state (PROTOCOL.md "World state: time and weather")."""
    if msg["dimension"] not in DIMENSIONS:
        raise ValueError(f"world_state dimension must be one of {DIMENSIONS}, got {msg['dimension']}")
    if not (isinstance(msg["day_time"], int) and 0 <= msg["day_time"] < 24000 and isinstance(msg["time"], int)):
        raise ValueError("world_state time must be an int and day_time 0..23999")
    if msg["rain"] not in (0, 1) or msg["thunder"] not in (0, 1) or (msg["thunder"] and not msg["rain"]):
        raise ValueError("world_state rain and thunder are 0 or 1, and thunder only while it rains")


def validate_block_edit(msg: dict) -> None:
    """block_edit (PROTOCOL.md 4.20): op break|place, integer world pos, state for place, optional face."""
    if msg["op"] not in ("break", "place"):
        raise ValueError(f"block_edit op must be break or place, got {msg['op']}")
    pos = msg["pos"]
    if not isinstance(pos, list) or len(pos) != 3 or not all(isinstance(v, int) and not isinstance(v, bool) for v in pos):
        raise ValueError(f"block_edit pos must be [x, y, z] integers, got {pos}")
    if msg["op"] == "place" and not (isinstance(msg.get("state"), str) and msg["state"].strip()):
        raise ValueError("block_edit place needs a state")
    if msg.get("face") is not None and msg["face"] not in FACES:
        raise ValueError(f"block_edit face must be one of {FACES}, got {msg['face']}")


def decode_rle(data: list[int], volume: int) -> list[int]:
    """rle_palette_v1: pairs [paletteIndex, runLength, ...]."""
    if len(data) % 2:
        raise ValueError("data length must be even")
    out: list[int] = []
    for i in range(0, len(data), 2):
        v, n = data[i], data[i + 1]
        if n < 1:
            raise ValueError("runLength must be >= 1")
        out.extend([v] * n)
    if len(out) != volume:
        raise ValueError(f"runs cover {len(out)} of {volume}")
    return out


def decode_section(data: list[int], grid: list[int], dims: tuple[int, int, int], key: tuple[int, int, int], palette_size: int) -> None:
    """One sections_v1 section: [paletteIndex, runLength] pairs over its box (clamped to dims) in yzx order."""
    dx, dy, dz = dims
    x0, y0, z0 = (k * SECTION for k in key)
    sx, sy, sz = min(SECTION, dx - x0), min(SECTION, dy - y0), min(SECTION, dz - z0)
    if len(data) % 2:
        raise ValueError("section data must be pairs")
    cells = [((y0 + y) * dz + z0 + z) * dx + x0 + x for y in range(sy) for z in range(sz) for x in range(sx)]
    p = 0
    for i in range(0, len(data), 2):
        v, run = data[i], data[i + 1]
        if run < 1 or p + run > len(cells) or not 0 <= v < palette_size:
            raise ValueError(f"bad run in section {key}")
        for c in cells[p:p + run]:
            grid[c] = v
        p += run
    if p != len(cells):
        raise ValueError(f"section {key} runs cover {p} of {len(cells)}")


def decode_light(obj: dict, volume: int) -> list[int]:
    """chunk_snapshot.light (PROTOCOL.md 4.4.1): rle_v1 pairs of packed sky << 4 | block over the yzx grid."""
    if obj.get("encoding") != "rle_v1":
        raise ValueError(f"unsupported light encoding {obj.get('encoding')}")
    out = decode_rle(obj["data"], volume)
    if out and (min(out) < 0 or max(out) > 255):
        raise ValueError("light value out of range")
    return out


@dataclass
class BiomeGrid:
    """chunk_snapshot.biomes (PROTOCOL.md 4.4.1): one biome per 4x4x4 world-aligned cell."""

    q_origin: tuple[int, int, int]
    q_dims: tuple[int, int, int]
    palette: list[str]
    cells: list[int]
    climate: list | None = None

    @staticmethod
    def from_json(obj: dict) -> BiomeGrid:
        if obj.get("res") != 4 or obj.get("encoding") != "rle_palette_v1":
            raise ValueError("unsupported biome res/encoding")
        qd = tuple(obj["q_dims"])
        cells = decode_rle(obj["data"], qd[0] * qd[1] * qd[2])
        if cells and max(cells) >= len(obj["palette"]):
            raise ValueError("biome palette index out of range")
        climate = obj.get("climate")
        if climate is not None and len(climate) != len(obj["palette"]):
            raise ValueError("climate must have one entry per palette entry")
        return BiomeGrid(tuple(obj["q_origin"]), qd, list(obj["palette"]), cells, climate)

    def at_world(self, x: int, y: int, z: int) -> str:
        qx, qy, qz = (x >> 2) - self.q_origin[0], (y >> 2) - self.q_origin[1], (z >> 2) - self.q_origin[2]
        dx, _, dz = self.q_dims
        return self.palette[self.cells[(qy * dz + qz) * dx + qx]]


@dataclass
class WorldMirror:
    """Client-side copy of the subscribed region, kept current from chunk_snapshot + block_update."""

    world: str = ""
    origin: tuple[int, int, int] = (0, 0, 0)
    dims: tuple[int, int, int] = (0, 0, 0)
    snapshot_id: int = -1
    palette: list[str] = field(default_factory=list)
    grid: list[int] = field(default_factory=list)
    view_margin: tuple[int, int, int] = (0, 0, 0)          # PROTOCOL.md 4.17
    parts: list[dict] = field(default_factory=list)          # sectioned snapshot parts received so far
    sections_sent: int = 0                                   # sections in the last sectioned snapshot (delta size)
    light: list[int] | None = None   # packed sky << 4 | block, when the server sent light
    biomes: BiomeGrid | None = None

    @property
    def loaded(self) -> bool:
        return self.snapshot_id >= 0

    def index(self, x: int, y: int, z: int) -> int:
        dx, _, dz = self.dims
        return (y * dz + z) * dx + x

    def apply_snapshot(self, msg: dict) -> bool:
        """Applies a chunk_snapshot. Returns False while parts of a sectioned snapshot are still missing."""
        if msg["order"] != "yzx":
            raise ValueError("unsupported order")
        if msg["encoding"] == SECTIONS:
            return self._apply_sections(msg)
        if msg["encoding"] != "rle_palette_v1":
            raise ValueError("unsupported encoding")
        dims = tuple(msg["dims"])
        grid = decode_rle(msg["data"], dims[0] * dims[1] * dims[2])
        if grid and max(grid) >= len(msg["palette"]):
            raise ValueError("palette index out of range")
        light = decode_light(msg["light"], len(grid)) if msg.get("light") else None
        biomes = BiomeGrid.from_json(msg["biomes"]) if msg.get("biomes") else None
        self.world, self.origin, self.dims = msg["world"], tuple(msg["origin"]), dims
        self.snapshot_id, self.palette, self.grid = msg["snapshot_id"], list(msg["palette"]), grid
        self.light, self.biomes = light, biomes
        self.view_margin = tuple(msg.get("view_margin") or (0, 0, 0))
        return True

    def _apply_sections(self, msg: dict) -> bool:
        """sections_v1 (PROTOCOL.md 4.18): collect the parts, then copy the base (delta) and decode the listed sections.

        With light (top-level "light" with in_sections), every listed section carries its own light runs and unlisted
        sections of a delta keep the base's light; biomes, when sent, come whole on part 0.
        """
        if msg["part"] == 0 or (self.parts and self.parts[0]["snapshot_id"] != msg["snapshot_id"]):
            self.parts = []
        self.parts.append(msg)
        if len(self.parts) < msg["parts"]:
            return False
        parts, self.parts = self.parts, []
        m0 = parts[0]
        dx, dy, dz = dims = tuple(m0["dims"])
        palette = list(m0["palette"])
        grid = [0] * (dx * dy * dz)
        with_light = bool(m0.get("light"))
        if with_light and m0["light"].get("encoding") != "rle_v1":
            raise ValueError("unsupported light encoding")
        light = [0] * len(grid) if with_light else None
        base = m0.get("base")
        n = [(d + SECTION - 1) // SECTION for d in dims]
        listed = set()
        if base is not None:
            # The base may have other dims (a resize, PROTOCOL.md 4.25): copy the overlap, new local p = old p + shift.
            sx, sy, sz = base["shift"]
            if base["snapshot_id"] != self.snapshot_id or self.world != m0["world"]:
                raise ValueError("delta against a snapshot we don't have")
            moved = (self.origin[0] + sx, self.origin[1] + sy, self.origin[2] + sz)
            if any(s % SECTION for s in (sx, sy, sz)) or moved != tuple(m0["origin"]):
                raise ValueError("bad delta shift")
            if palette[: len(self.palette)] != self.palette:
                raise ValueError("delta palette must extend the base palette")
            if with_light and self.light is None:
                raise ValueError("delta with light against a snapshot without light")
            odx, ody, odz = self.dims
            x0, x1 = max(0, -sx), min(dx, odx - sx)
            for y in range(dy):
                oy = y + sy
                if not 0 <= oy < ody:
                    continue
                for z in range(dz):
                    oz = z + sz
                    if not 0 <= oz < odz or x1 <= x0:
                        continue
                    dst, src = (y * dz + z) * dx, (oy * odz + oz) * odx + sx
                    grid[dst + x0:dst + x1] = self.grid[src + x0:src + x1]
                    if light is not None:
                        light[dst + x0:dst + x1] = self.light[src + x0:src + x1]
        for i, m in enumerate(parts):
            if m["part"] != i or m["snapshot_id"] != m0["snapshot_id"]:
                raise ValueError("parts out of order")
            for sec in m["sections"]:
                kx, ky, kz = sec["s"]
                if not (0 <= kx < n[0] and 0 <= ky < n[1] and 0 <= kz < n[2]) or (kx, ky, kz) in listed:
                    raise ValueError("bad or repeated section key")
                listed.add((kx, ky, kz))
                decode_section(sec["data"], grid, dims, (kx, ky, kz), len(palette))
                if light is not None:
                    if "light" not in sec:
                        raise ValueError(f"section {sec['s']} has no light")
                    decode_section(sec["light"], light, dims, (kx, ky, kz), 256)
        if base is None and len(listed) != n[0] * n[1] * n[2]:
            raise ValueError("full sectioned snapshot is missing sections")
        if base is not None:
            # Unlisted sections must lie fully inside the base region, or their blocks would be missing.
            shift, odims = base["shift"], self.dims
            for kx in range(n[0]):
                for ky in range(n[1]):
                    for kz in range(n[2]):
                        if (kx, ky, kz) in listed:
                            continue
                        for a, k in enumerate((kx, ky, kz)):
                            lo = k * SECTION + shift[a]
                            if lo < 0 or min(dims[a], k * SECTION + SECTION) + shift[a] > odims[a]:
                                raise ValueError(f"section {(kx, ky, kz)} is neither listed nor inside the base")
        self.world, self.origin, self.dims = m0["world"], tuple(m0["origin"]), dims
        self.snapshot_id, self.palette, self.grid = m0["snapshot_id"], palette, grid
        self.light = light
        self.biomes = BiomeGrid.from_json(m0["biomes"]) if m0.get("biomes") else None
        self.view_margin = tuple(m0.get("view_margin") or (0, 0, 0))
        self.sections_sent = len(listed)
        return True

    def apply_block_update(self, msg: dict) -> bool:
        if msg["snapshot_id"] != self.snapshot_id:
            return False  # stale, per spec
        self.palette.extend(msg.get("palette_add") or [])
        for x, y, z, p in msg["changes"]:
            self.grid[self.index(x, y, z)] = p
        if self.light is not None:
            for x, y, z, v in msg.get("light") or []:
                self.light[self.index(x, y, z)] = v
        return True

    def at_world(self, x: int, y: int, z: int) -> str:
        ox, oy, oz = self.origin
        return self.palette[self.grid[self.index(x - ox, y - oy, z - oz)]]

    def light_at_world(self, x: int, y: int, z: int) -> tuple[int, int]:
        """(sky, block) light at a world position; needs a snapshot with light."""
        ox, oy, oz = self.origin
        v = self.light[self.index(x - ox, y - oy, z - oz)]
        return v >> 4, v & 15

    def count(self, prefix: str) -> int:
        ids = {i for i, s in enumerate(self.palette) if s == prefix or s.startswith(prefix + "[")}
        return sum(1 for g in self.grid if g in ids)


class Envelope:
    """Adds v/seq/t to outgoing messages."""

    def __init__(self) -> None:
        self.seq = 0

    def wrap(self, type_: str, **payload) -> dict:
        m = {"v": PROTOCOL_VERSION, "type": type_, "seq": self.seq, "t": int(time.time() * 1000)}
        self.seq = (self.seq + 1) & 0xFFFFFFFF
        m.update(payload)
        return m


def block_edit(op: str, pos, state: str | None = None, face: str | None = None) -> dict:
    """block_edit payload (PROTOCOL.md 4.20); wrap it with Envelope.wrap("block_edit", **payload)."""
    d: dict = {"op": op, "pos": [int(v) for v in pos]}
    if state is not None:
        d["state"] = state
    if face is not None:
        d["face"] = face
    return d


def parse_region(spec: str) -> dict:
    """'ox,oy,oz,dx,dy,dz' -> origin+dims payload; 'c:cx,cy,cz,dx,dy,dz[,y_origin]' -> center+size payload."""
    if spec.startswith("c:"):
        v = [float(x) for x in spec[2:].split(",")]
        if len(v) not in (6, 7):
            raise ValueError("center form needs cx,cy,cz,dx,dy,dz[,y_origin]")
        d = {"center": v[:3], "size": [int(x) for x in v[3:6]]}
        if len(v) == 7:
            d["y_origin"] = int(v[6])
        return d
    v = [int(x) for x in spec.split(",")]
    if len(v) != 6:
        raise ValueError("region needs ox,oy,oz,dx,dy,dz")
    return {"origin": v[:3], "dims": v[3:]}


def yaw_quat(theta: float) -> list[float]:
    """Rotation about +Y by theta radians, [x, y, z, w]. Identity faces -Z (north)."""
    return [0.0, math.sin(theta / 2), 0.0, math.cos(theta / 2)]


def scripted_pose(t: float, center=(0.16, 0.0, 0.16), radius: float = 0.10, height: float = 0.45, period: float = 4.0) -> dict:
    """Pose payload: head circles above the table centre (tabletop metres), always looking at the centre."""
    a = 2 * math.pi * t / period
    hx, hz = center[0] + radius * math.cos(a), center[2] + radius * math.sin(a)
    # Look at centre: direction d = centre - head. With identity facing -Z, rotation about +Y by theta
    # gives forward (-sin theta, 0, -cos theta).
    dx, dz = center[0] - hx, center[2] - hz
    theta = math.atan2(-dx, -dz)
    q = yaw_quat(theta)
    sway = 0.03 * math.sin(2 * a)
    return {
        "ts": int(time.monotonic() * 1e6),
        "head": {"p": [round(hx, 4), height, round(hz, 4)], "q": q},
        "left": {"p": [round(hx - 0.05, 4), height - 0.12 + sway, round(hz, 4)], "q": q, "tracked": True},
        "right": {"p": [round(hx + 0.05, 4), height - 0.12 - sway, round(hz, 4)], "q": q, "tracked": True},
    }


class BlockEntityMirror:
    """Client-side copy of the block entity data (PROTOCOL.md 4.29), by world position."""

    def __init__(self) -> None:
        self.entries: dict[tuple[int, int, int], dict] = {}
        self.messages = 0
        self.resets = 0

    def apply(self, msg: dict) -> None:
        self.messages += 1
        if msg.get("reset"):
            self.resets += 1
            self.entries.clear()
        for e in msg["entities"]:
            self.entries[tuple(e["pos"])] = e
        for p in msg.get("removed") or []:
            self.entries.pop(tuple(p), None)

    def prune(self, origin, dims) -> None:
        """Drops entries outside a new region (the server forgets them too)."""
        self.entries = {p: e for p, e in self.entries.items() if all(origin[i] <= p[i] < origin[i] + dims[i] for i in range(3))}

    def of_kind(self, kind: str) -> list[dict]:
        return [e for e in self.entries.values() if e["kind"] == kind]


class EntityMirror:
    """Client-side copy of the entities in the region (PROTOCOL.md 4.21): full lists replace it, deltas merge."""

    def __init__(self) -> None:
        self.entities: dict[int, dict] = {}
        self.messages = 0
        self.fulls = 0

    def apply(self, msg: dict) -> None:
        self.messages += 1
        if msg["full"]:
            self.fulls += 1
            self.entities = {}
        for e in msg["entities"]:
            cur = self.entities.get(e["id"])
            if cur is None:
                if "type" not in e:
                    raise ValueError(f"delta for unknown entity {e['id']}")
                self.entities[e["id"]] = dict(e)
            else:
                for k, v in e.items():   # present fields replace; null/false/[] clear
                    cur[k] = v
        for i in msg.get("removed") or []:
            self.entities.pop(i, None)

    def of_type(self, type_: str) -> list[dict]:
        return [e for e in self.entities.values() if e["type"] == type_]


def validate_sound(msg: dict) -> None:
    """[sound] sound_groups, assets and asset_get (PROTOCOL.md "Sound", "Asset delivery")."""
    t = msg["type"]
    if t == "sound_groups":
        n = 0
        for g in msg["groups"]:
            for k in SOUND_GROUP_KEYS:
                if g.get(k) is not None and not (isinstance(g[k], str) and _SOUND_ID.match(g[k])):
                    raise ValueError(f"sound group {k} must be a sound id, got {g.get(k)}")
            if not isinstance(g.get("volume"), (int, float)) or not isinstance(g.get("pitch"), (int, float)):
                raise ValueError("sound group needs volume and pitch")
            if not g.get("blocks") or not all(isinstance(b, str) and _ID.match(b) for b in g["blocks"]):
                raise ValueError("sound group needs block ids")
            n += len(g["blocks"])
        if n != msg["count"]:
            raise ValueError(f"sound_groups count {msg['count']} but {n} blocks listed")
    elif t == "assets":
        if not isinstance(msg["chunk"], int) or not 1024 <= msg["chunk"] <= ASSET_MAX_CHUNK:
            raise ValueError("assets chunk must be 1024..1 MiB")
        for f in msg["files"]:
            if f.get("name") not in ASSET_NAMES or not isinstance(f.get("size"), int) or f["size"] <= 0:
                raise ValueError(f"assets file must be one of {ASSET_NAMES} with a size: {f}")
            if not (isinstance(f.get("sha256"), str) and re.fullmatch(r"[0-9a-f]{64}", f["sha256"])):
                raise ValueError("assets sha256 must be 64 lower-case hex digits")
    elif t == "asset_get":
        if msg["name"] not in ASSET_NAMES or not isinstance(msg["offset"], int) or msg["offset"] < 0:
            raise ValueError("asset_get needs a known name and an offset >= 0")
        if not isinstance(msg["length"], int) or not 1 <= msg["length"] <= ASSET_MAX_CHUNK:
            raise ValueError("asset_get length must be 1..1 MiB")


def asset_frame(name: str, offset: int, data: bytes) -> bytes:
    """[sound] A binary asset frame: b"TTA1", the name's length and ASCII name, the offset (8 bytes big-endian), the data."""
    n = name.encode("ascii")
    if not 1 <= len(n) <= 64:
        raise ValueError("asset name must be 1..64 ASCII bytes")
    return b"TTA1" + bytes([len(n)]) + n + offset.to_bytes(8, "big") + data


def parse_asset_frame(frame: bytes) -> tuple[str, int, bytes]:
    """[sound] (name, offset, data) of a binary asset frame; ValueError for anything else."""
    if len(frame) < 15 or frame[:4] != b"TTA1":
        raise ValueError("not an asset frame")
    n = frame[4]
    if not 1 <= n <= 64 or len(frame) <= 13 + n:
        raise ValueError("bad asset frame header")
    return frame[5:5 + n].decode("ascii"), int.from_bytes(frame[5 + n:13 + n], "big"), frame[13 + n:]
