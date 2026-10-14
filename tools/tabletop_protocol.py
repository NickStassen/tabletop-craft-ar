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
}
ERROR_CODES = {"version_mismatch", "auth_failed", "bad_message", "unknown_type", "unsupported", "region_too_large",
               "world_not_found", "bad_snapshot", "rate_limited", "skin_failed", "internal"}


def validate(msg: dict) -> None:
    t = msg.get("type")
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
        self.world, self.origin, self.dims = msg["world"], tuple(msg["origin"]), dims
        self.snapshot_id, self.palette, self.grid = msg["snapshot_id"], list(msg["palette"]), grid
        self.view_margin = tuple(msg.get("view_margin") or (0, 0, 0))
        return True

    def _apply_sections(self, msg: dict) -> bool:
        """sections_v1 (PROTOCOL.md 4.18): collect the parts, then copy the base (delta) and decode the listed sections."""
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
        base = m0.get("base")
        n = [(d + SECTION - 1) // SECTION for d in dims]
        listed = set()
        if base is not None:
            sx, sy, sz = base["shift"]
            if base["snapshot_id"] != self.snapshot_id or self.dims != dims or self.world != m0["world"]:
                raise ValueError("delta against a snapshot we don't have")
            moved = (self.origin[0] + sx, self.origin[1] + sy, self.origin[2] + sz)
            if any(s % SECTION for s in (sx, sy, sz)) or moved != tuple(m0["origin"]):
                raise ValueError("bad delta shift")
            if palette[: len(self.palette)] != self.palette:
                raise ValueError("delta palette must extend the base palette")
            for y in range(dy):
                oy = y + sy
                if not 0 <= oy < dy:
                    continue
                for z in range(dz):
                    oz = z + sz
                    if not 0 <= oz < dz:
                        continue
                    x0, x1 = max(0, -sx), min(dx, dx - sx)
                    if x1 > x0:
                        dst, src = (y * dz + z) * dx, (oy * dz + oz) * dx + sx
                        grid[dst + x0:dst + x1] = self.grid[src + x0:src + x1]
        for i, m in enumerate(parts):
            if m["part"] != i or m["snapshot_id"] != m0["snapshot_id"]:
                raise ValueError("parts out of order")
            for sec in m["sections"]:
                kx, ky, kz = sec["s"]
                if not (0 <= kx < n[0] and 0 <= ky < n[1] and 0 <= kz < n[2]) or (kx, ky, kz) in listed:
                    raise ValueError("bad or repeated section key")
                listed.add((kx, ky, kz))
                decode_section(sec["data"], grid, dims, (kx, ky, kz), len(palette))
        if base is None and len(listed) != n[0] * n[1] * n[2]:
            raise ValueError("full sectioned snapshot is missing sections")
        self.world, self.origin, self.dims = m0["world"], tuple(m0["origin"]), dims
        self.snapshot_id, self.palette, self.grid = m0["snapshot_id"], palette, grid
        self.view_margin = tuple(m0.get("view_margin") or (0, 0, 0))
        self.sections_sent = len(listed)
        return True

    def apply_block_update(self, msg: dict) -> bool:
        if msg["snapshot_id"] != self.snapshot_id:
            return False  # stale, per spec
        self.palette.extend(msg.get("palette_add") or [])
        for x, y, z, p in msg["changes"]:
            self.grid[self.index(x, y, z)] = p
        return True

    def at_world(self, x: int, y: int, z: int) -> str:
        ox, oy, oz = self.origin
        return self.palette[self.grid[self.index(x - ox, y - oy, z - oz)]]

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
