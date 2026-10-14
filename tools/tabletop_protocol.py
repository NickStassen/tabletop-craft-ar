"""Helpers for the tabletop link protocol v1 (docs/PROTOCOL.md)."""
from __future__ import annotations

import math
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
    if t == "block_edit":
        validate_block_edit(msg)
    if t == "block_registry":
        validate_block_registry(msg)


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
