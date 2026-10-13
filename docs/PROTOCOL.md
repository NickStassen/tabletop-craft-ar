# PROTOCOL.md — Tabletop link protocol, version 1

Status: **v1, normative.** The server plugin (`server-plugin/`), the Quest app (`quest-app/`) and the fake client (`tools/`) must all implement exactly this. Any breaking change bumps `PROTOCOL_VERSION` and is recorded in the changelog at the bottom.

## 1. Transport

- WebSocket (RFC 6455), plaintext `ws://` on LAN. Default endpoint: `ws://<server-host>:8765/tabletop` (port and path configurable in the plugin's `config.yml`).
- Every message is **one UTF-8 JSON text frame** holding one JSON object. Binary frames are reserved for a later version. A v1 peer that gets a binary frame replies with `error` code `unsupported` and ignores the frame.
- Why JSON and not binary in v1: a 32x32x64 band is only tens of KB once run-length encoded (see §6.3), pose traffic at 30 Hz is about 10 KB/s, and JSON is easy to debug with `websocat` or the fake client. A binary chunk encoding is planned only if measurements show it's needed (PLAN.md, Phase 5).
- Permessage-deflate is optional. The server MAY offer it, and the client MAY decline. Nothing in this spec depends on compression.
- Max frame size the server accepts: 64 KiB. Max frame the server sends: 4 MiB.

## 2. Envelope

Every message has these top-level fields:

| field | type | req | meaning |
|---|---|---|---|
| `v` | int | yes | Protocol version. Always `1` in this spec. |
| `type` | string | yes | Message type, one of §4. |
| `seq` | int (uint32, wraps) | yes | Per-sender monotonically increasing counter, starting at 0 on each connection. |
| `t` | int | yes | Sender's wall clock, milliseconds since the Unix epoch (int64). Used for latency stats only, never for ordering. |

Unknown top-level fields and unknown fields inside a payload MUST be ignored (forward compatibility). A message with an unknown `type` gets an `error` (`unknown_type`) and is otherwise ignored. A message whose `v` is not supported gets `error` `version_mismatch`, and the server closes the connection with close code 4001.

Payload fields sit **at the top level next to the envelope fields** (no nested `data` object).

## 3. Coordinate conventions

- **World coordinates (server):** Minecraft block coordinates, right-handed, +Y up, +X east, +Z south. Positions are doubles in blocks.
- **Region-local coordinates:** integers `x ∈ [0,dx)`, `y ∈ [0,dy)`, `z ∈ [0,dz)`, with `world = origin + local`.
- **Tabletop coordinates (Quest → server):** meters, in a right-handed frame whose axes are parallel to the world's: +X east, +Y up (table normal), +Z south. Its origin is the point on the table where the region's local (0,0,0) block corner sits, at the bottom of the band. So a pose position `p` in meters maps to the world as `origin + p / block_size_m`.
- **Unity conversion (the only place handedness matters):** Unity is left-handed. The Quest app parents the miniature under a `TabletopAnchor` transform and renders block `(x,y,z)` at anchor-local `(x, y, -z) * block_size_m`, which keeps the world un-mirrored. To send a pose, it takes the anchor-local Unity position `(ux,uy,uz)` and rotation `(qx,qy,qz,qw)` and sends `p = [ux, uy, -uz]`, `q = [-qx, -qy, qz, qw]`. Server → Quest positions go through the inverse of the same mapping.
- **Quaternions:** `[x, y, z, w]`, unit length, in the tabletop frame. Because of the Z mirror, the identity quaternion means **facing −Z (north)**, up +Y (Unity's forward maps there). The server derives yaw and pitch from the look vector `f = q·(0,0,-1)` as `yaw = atan2(-f.x, f.z)` in degrees and `pitch = -asin(f.y)` in degrees, which is Minecraft's convention.
- **Yaw/pitch for players (server → Quest):** Minecraft convention, degrees. Yaw 0 = facing +Z (south), 90 = −X (west), and so on, increasing clockwise when seen from above. Pitch: −90 is straight up, +90 straight down.

## 4. Message types

Direction: **S→C** means server to Quest/client, **C→S** client to server.

| type | dir | when |
|---|---|---|
| `hello` | C→S | first message after connect |
| `welcome` | S→C | reply to `hello` |
| `subscribe` | C→S | choose or move the region |
| `chunk_snapshot` | S→C | full region data |
| `block_update` | S→C | one or more blocks changed in the region |
| `player_positions` | S→C | periodic list of players in or near the region |
| `pose` | C→S | head + hands, about 30 Hz |
| `avatar_config` | C→S | set skin/scale/visibility of the giant |
| `avatar_state` | S→C | the server's view of the avatar after config |
| `ping` | both | latency probe |
| `pong` | both | reply to ping |
| `error` | both | problem report |
| `bye` | both | graceful close |

### 4.1 `hello` (C→S)

```json
{"v":1,"type":"hello","seq":0,"t":1760370000000,
 "client":"quest-app","client_version":"0.1.0",
 "protocols":[1],
 "token":"changeme",
 "caps":["rle_palette_v1","avatar"]}
```

- `client`: free string, e.g. `quest-app` or `fake-quest`.
- `protocols`: protocol versions the client speaks. The server picks the highest one it also supports.
- `token`: shared secret from the plugin `config.yml` (`auth.token`). If it's empty in config, auth is disabled (LAN dev only). A wrong token gets `error` `auth_failed` and then close code 4003.
- `caps`: capability strings. v1 defines `rle_palette_v1` (required) and `avatar` (client will send poses).
- The client MUST NOT send anything else before it gets `welcome`. The server closes with 4002 if `hello` doesn't arrive within 5 s.

### 4.2 `welcome` (S→C)

```json
{"v":1,"type":"welcome","seq":0,"t":1760370000012,
 "protocol":1,"server":"tabletop-plugin","server_version":"0.1.0",
 "mc_version":"1.21.11","session":"6f1c…",
 "world":"world",
 "region":{"origin":[100,60,-40],"dims":[32,64,32]},
 "limits":{"max_dims":[64,384,64],"pose_hz_max":60,"player_hz":10},
 "avatar_supported":true}
```

- `session`: opaque id for logs.
- `region`: the default region from config. The server immediately follows `welcome` with a `chunk_snapshot` for it unless the client sends `subscribe` first. (Send order: `welcome`, then a snapshot, which the server builds async, so a `subscribe` arriving in the meantime replaces it.)
- `limits.max_dims`: the largest `dims` the server accepts in `subscribe`.

### 4.3 `subscribe` (C→S)

```json
{"v":1,"type":"subscribe","seq":1,"t":1760370000100,
 "world":"world","origin":[100,60,-40],"dims":[32,64,32]}
```

- `origin`: int[3], world block coords of the region's min corner.
- `dims`: int[3] `[dx,dy,dz]`, each ≥ 1 and ≤ `limits.max_dims`. Y is clamped to the world's min/max build height; a clamp sends back the actual values in the next `chunk_snapshot`.
- Optional `follow`: player name string. When it's set, the server re-centers X/Z on that player whenever they move more than `dx/4` from the center, and sends a new snapshot. v1 servers MAY reply `error` `unsupported` for `follow`.
- The server replies with `chunk_snapshot`. From then on `block_update` and `player_positions` only cover this region (the previous subscription is replaced).

### 4.4 `chunk_snapshot` (S→C)

```json
{"v":1,"type":"chunk_snapshot","seq":2,"t":1760370000150,
 "snapshot_id":7,
 "world":"world","origin":[100,60,-40],"dims":[32,64,32],
 "order":"yzx",
 "palette":["minecraft:air","minecraft:stone","minecraft:grass_block[snowy=false]","minecraft:oak_log[axis=y]"],
 "encoding":"rle_palette_v1",
 "data":[0,40960,1,20480,2,1024,0,3072],
 "biomes":null,
 "light":null}
```

- `snapshot_id`: int, increments per snapshot on this connection. `block_update` messages name the snapshot they apply to.
- `palette`: array of **full block-state strings** exactly as `BlockData#getAsString()` produces them (namespaced id plus `[k=v,...]` with properties sorted alphabetically, as Minecraft does). Index 0 SHOULD be `minecraft:air` when air is present. The palette only lists states that appear in this region.
- `order`: always `"yzx"` in v1. Linear index `i = (y * dz + z) * dx + x`, so X varies fastest, then Z, then Y (the same order Minecraft uses inside a section).
- `encoding: "rle_palette_v1"`: `data` is a flat int array of pairs `[paletteIndex, runLength, paletteIndex, runLength, …]` over the linear order. The sum of all `runLength` values MUST equal `dx*dy*dz`, and each `runLength` ≥ 1. A decoder that gets a wrong total MUST drop the snapshot and send `error` `bad_snapshot`.
- `biomes`, `light`: reserved, always `null` in v1.
- Size: if a snapshot would be over 4 MiB, the server sends `error` `region_too_large` instead.

### 4.5 `block_update` (S→C)

```json
{"v":1,"type":"block_update","seq":3,"t":1760370001000,
 "snapshot_id":7,
 "palette_add":["minecraft:torch"],
 "changes":[[5,12,7,4],[6,12,7,0]]}
```

- `changes`: array of `[x, y, z, paletteIndex]` with **region-local** coordinates.
- `palette_add`: new states appended to the **end** of the current palette in this order, before the changes are applied. So the first new state gets index `palette.length`. Can be empty or missing.
- The server batches the changes from one tick into a single message (at most one `block_update` per tick, 20/s).
- If `snapshot_id` doesn't match the client's current snapshot, the client drops the message. (It's for an old subscription.)
- If more than 4096 changes occur in one tick (an explosion, or a `/fill`), the server MAY send a fresh `chunk_snapshot` instead.
- Sources the server MUST cover: block break and place by players, physics, fluids, fire, explosions, pistons, and commands. Implementation: listen to the main events and also run a cheap per-tick diff of affected sections. See ARCHITECTURE.md §3.3.

### 4.6 `player_positions` (S→C)

Sent at `limits.player_hz` (default 10 Hz) whenever there's at least one player in the region bounds expanded by 16 blocks horizontally, plus once with an empty list when the last one leaves.

```json
{"v":1,"type":"player_positions","seq":4,"t":1760370001050,
 "players":[
  {"uuid":"0f1e…","name":"Alex","pos":[112.5,64.0,-30.25],"yaw":135.0,"pitch":10.0,
   "sneaking":false,"skin":"https://textures.minecraft.net/texture/…"}
 ]}
```

- `pos`: world coordinates (doubles) of the player's feet. The client subtracts `origin` itself.
- `skin`: optional skin texture URL taken from the player's profile. It's sent only in the first message that includes that UUID on the connection (and after it changes), and it can be `null`.
- The giant avatar's own entities are **not** included.

### 4.7 `pose` (C→S)

The Quest sends poses at about 30 Hz (allowed 1–`limits.pose_hz_max`). The server processes only the newest pose per tick and drops older ones.

```json
{"v":1,"type":"pose","seq":120,"t":1760370002000,
 "ts":123456789,
 "head":{"p":[0.42,0.55,0.31],"q":[0.0,0.38268,0.0,0.92388]},
 "left":{"p":[0.30,0.30,0.20],"q":[0,0,0,1],"tracked":true},
 "right":{"p":[0.55,0.28,0.22],"q":[0,0,0,1],"tracked":false}}
```

- `ts`: int64, the client's monotonic clock in microseconds when the pose was sampled (for jitter stats; the server doesn't need to sync clocks).
- `p`: position in meters in the **tabletop frame** (§3). `q`: quaternion `[x,y,z,w]` in the same frame.
- `left` / `right`: controller or hand-tracking wrist pose. `tracked:false` means the last known pose is stale; the server hides or freezes that hand. If `tracked` is missing, it means `true`.
- Server mapping to the world: `world = origin + p / block_size_m`, where `block_size_m` comes from the latest `avatar_config` (default 0.01). See ARCHITECTURE.md §5 for clamping.

### 4.8 `avatar_config` (C→S)

```json
{"v":1,"type":"avatar_config","seq":2,"t":1760370000200,
 "enabled":true,
 "skin_player":"PlayerName",
 "skin_url":null,
 "model":"wide",
 "block_size_m":0.01,
 "max_scale":16.0,
 "show_hands":true,
 "name_tag":"Tabletop Giant"}
```

- `enabled`: spawn (true) or despawn (false) the giant.
- Skin source, in priority order: `skin_url` (must be a `https://textures.minecraft.net/texture/…` URL), then `skin_player` (an account name that the server resolves through the Mojang profile lookup once and caches), then the default skin. Every field is optional. Missing fields keep their previous value.
- `model`: `"wide"` or `"slim"`.
- `block_size_m`: how many real meters one block occupies on the table (same as the scale the Quest renders the miniature at). Range 0.002–0.1.
- `max_scale`: cap on the Mannequin scale attribute, clamped by the server to the attribute's real max (16 on current versions).
- `name_tag`: optional custom name, or `null` for none.

### 4.9 `avatar_state` (S→C)

Sent after each `avatar_config`, and when the server changes something by itself (for example the avatar despawned because the region moved).

```json
{"v":1,"type":"avatar_state","seq":5,"t":1760370000300,
 "enabled":true,"entity_uuid":"9a…","scale":16.0,"world_height_blocks":28.8,
 "skin_resolved":true,"note":"scale clamped from 175.0 to 16.0"}
```

### 4.10 `ping` / `pong` (both directions)

```json
{"v":1,"type":"ping","seq":9,"t":1760370003000,"id":42}
{"v":1,"type":"pong","seq":9,"t":1760370003004,"id":42,"echo_t":1760370003000}
```

- Either side MAY ping. The receiver MUST answer with `pong` carrying the same `id` and `echo_t` = the ping's `t`. RTT = now − `echo_t` on the original sender.
- The client SHOULD ping every 2 s. The server closes a connection after 15 s with no messages at all (close code 4004).

### 4.11 `error` (both directions)

```json
{"v":1,"type":"error","seq":10,"t":1760370003100,
 "code":"region_too_large","message":"dims 128x384x128 exceed max 64x384x64","ref_seq":1,"fatal":false}
```

Codes (strings, closed set in v1):

| code | fatal | meaning |
|---|---|---|
| `version_mismatch` | yes | no common protocol version |
| `auth_failed` | yes | bad or missing token |
| `bad_message` | no | invalid JSON, or missing or ill-typed fields |
| `unknown_type` | no | `type` not recognized |
| `unsupported` | no | known feature not implemented (e.g. `follow`, binary frames) |
| `region_too_large` | no | `dims` beyond limits or snapshot > 4 MiB |
| `world_not_found` | no | `world` doesn't exist |
| `bad_snapshot` | no | (client → server) RLE total mismatch; the server SHOULD resend a snapshot |
| `rate_limited` | no | too many messages (e.g. poses > `pose_hz_max`) |
| `skin_failed` | no | the skin lookup failed; the default skin is in use |
| `internal` | no | server-side exception |

`ref_seq` is the `seq` of the offending message, if any. When `fatal` is true, the sender closes the socket right after.

### 4.12 `bye` (both directions)

```json
{"v":1,"type":"bye","seq":200,"t":1760370100000,"reason":"user quit"}
```

Then it closes with code 1000. When the Quest client disconnects (with or without a `bye`), the server despawns the giant after 10 s unless a new connection sends `avatar_config` with the same `skin_player`.

## 5. Close codes

| code | meaning |
|---|---|
| 1000 | normal |
| 4001 | version mismatch |
| 4002 | hello timeout |
| 4003 | auth failed |
| 4004 | idle timeout |
| 4005 | server shutting down / plugin disabled |

## 6. Implementation notes (non-normative)

1. **Encoder reference (Java):** walk `i = 0 … N−1` in yzx order, look up `ChunkSnapshot.getBlockData(x&15, y, z&15).getAsString()`, map it to a palette index with a `HashMap<String,Integer>` (cache by `BlockData` identity), and emit a pair whenever the index changes.
2. **Decoder reference (C#/Python):** expand the pairs into a `ushort[]` of length N and check the total.
3. **Size check:** a typical 32x64x32 band (65,536 blocks), mostly air above and stone below, has about 500–3,000 runs, so 5–30 KB of JSON. A worst-case noisy 32x384x32 region (393,216 blocks, all runs of 1) is about 2.5 MB of JSON, which is still under the 4 MiB cap. See RESEARCH.md §2.
4. **Conformance:** `tools/` ships golden JSON fixtures for every message type. Both the Java unit tests and the fake client round-trip them.

## Changelog

- **v1 (2026-10-13):** initial.
