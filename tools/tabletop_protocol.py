"""Helpers for the tabletop link protocol v1 (docs/PROTOCOL.md)."""
from __future__ import annotations

import math
import time
from dataclasses import dataclass, field

PROTOCOL_VERSION = 1

# Required payload fields per message type, on top of the envelope (v, type, seq, t).
REQUIRED = {
    "hello": ["client", "protocols"],
    "welcome": ["protocol", "server", "server_version", "mc_version", "session", "world", "region", "limits", "avatar_supported"],
    "subscribe": ["origin", "dims"],
    "chunk_snapshot": ["snapshot_id", "world", "origin", "dims", "order", "palette", "encoding", "data"],
    "block_update": ["snapshot_id", "changes"],
    "player_positions": ["players"],
    "pose": ["head"],
    "avatar_config": [],
    "avatar_state": ["enabled", "entity_uuid", "scale", "world_height_blocks"],
    "ping": ["id"],
    "pong": ["id", "echo_t"],
    "error": ["code", "message"],
    "bye": [],
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


@dataclass
class WorldMirror:
    """Client-side copy of the subscribed region, kept current from chunk_snapshot + block_update."""

    world: str = ""
    origin: tuple[int, int, int] = (0, 0, 0)
    dims: tuple[int, int, int] = (0, 0, 0)
    snapshot_id: int = -1
    palette: list[str] = field(default_factory=list)
    grid: list[int] = field(default_factory=list)

    @property
    def loaded(self) -> bool:
        return self.snapshot_id >= 0

    def index(self, x: int, y: int, z: int) -> int:
        dx, _, dz = self.dims
        return (y * dz + z) * dx + x

    def apply_snapshot(self, msg: dict) -> None:
        if msg["order"] != "yzx" or msg["encoding"] != "rle_palette_v1":
            raise ValueError("unsupported order/encoding")
        dims = tuple(msg["dims"])
        grid = decode_rle(msg["data"], dims[0] * dims[1] * dims[2])
        if grid and max(grid) >= len(msg["palette"]):
            raise ValueError("palette index out of range")
        self.world, self.origin, self.dims = msg["world"], tuple(msg["origin"]), dims
        self.snapshot_id, self.palette, self.grid = msg["snapshot_id"], list(msg["palette"]), grid

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
