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
| `block_edit` | C→S | break or place one block from the headset (§4.20, needs `edit_v1`) |

### 4.1 `hello` (C→S)

```json
{"v":1,"type":"hello","seq":0,"t":1760370000000,
 "client":"quest-app","client_version":"0.1.0",
 "protocols":[1],
 "token":"changeme",
 "caps":["rle_palette_v1","avatar","light_v1","biomes_v1"]}
```

- `client`: free string, e.g. `quest-app` or `fake-quest`.
- `protocols`: protocol versions the client speaks. The server picks the highest one it also supports.
- `token`: shared secret from the plugin `config.yml` (`auth.token`). If it's empty in config, auth is disabled (LAN dev only). A wrong token gets `error` `auth_failed` and then close code 4003.
- `caps`: capability strings. v1 defines `rle_palette_v1` (required) and `avatar` (client will send poses). Optional extras: `light_v1` and `biomes_v1` (§4.5.1), `edit_v1` (§4.20); unknown caps are ignored.
- The client MUST NOT send anything else before it gets `welcome`. The server closes with 4002 if `hello` doesn't arrive within 5 s.

### 4.2 `welcome` (S→C)

```json
{"v":1,"type":"welcome","seq":0,"t":1760370000012,
 "protocol":1,"server":"tabletop-plugin","server_version":"0.1.0",
 "mc_version":"26.3","session":"6f1c…",
 "world":"world",
 "region":{"origin":[100,60,-40],"dims":[32,64,32]},
 "limits":{"max_dims":[64,384,64],"pose_hz_max":60,"player_hz":10},
 "avatar_supported":true,
 "caps":["light_v1","biomes_v1"]}
```

- `session`: opaque id for logs.
- `caps` (optional): the optional features granted to this session (§4.5.1). Missing or empty means none (e.g. an older server).
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
- `biomes`, `light`: `null` unless the session was granted `biomes_v1` / `light_v1` (§4.5.1).
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

### 4.5.1 Light and biomes (optional, additive: `light_v1`, `biomes_v1`)

Per-block light and biomes let a client draw Minecraft-style lighting (dark caves, torch light, smooth lighting) and per-biome grass, foliage and water colours. Both are **opt-in** so older clients never pay the bytes:

- The client lists `light_v1` and/or `biomes_v1` in `hello.caps`. The server grants each one unless it is turned off in its config (`stream.light`, `stream.biomes`) and echoes the granted ones in `welcome.caps`. Without a grant, `chunk_snapshot.light` / `.biomes` stay `null` and `block_update` carries no `light`.
- Nothing else changes: old servers ignore the caps and send `null`, and clients fall back to fixed tints and no lighting.

**`chunk_snapshot.light`** (with `light_v1`):

```json
"light":{"encoding":"rle_v1","data":[0,16,240,14,254,1,0,1]}
```

- One packed value per block, `sky * 16 + block` (0..255): the sky and block light levels 0..15 the server stores for that position (Paper `ChunkSnapshot#getBlockSkyLight` / `getBlockEmittedLight`). Opaque blocks normally hold 0; light-emitting blocks hold their own emission. Cells above the build height are `240` (open sky), below it `0`.
- Same yzx order as `data`, and the same pair encoding: `[value, runLength, …]`, runs summing to `dx*dy*dz`. A wrong total is a `bad_snapshot`.

**`chunk_snapshot.biomes`** (with `biomes_v1`):

```json
"biomes":{"res":4,"q_origin":[25,15,-11],"q_dims":[2,1,2],
 "palette":["minecraft:plains","minecraft:swamp"],
 "climate":[[0.8,0.4],[0.8,0.9]],
 "encoding":"rle_palette_v1","data":[0,2,1,2]}
```

- One biome per world-aligned 4x4x4 cell, vanilla's storage resolution. `q_origin = floor(origin / 4)` per axis (quart coordinates) and `q_dims` covers every cell the region touches. A block at region-local `(x,y,z)` uses cell `floor((origin + local) / 4) - q_origin`, linear index `(qy * qdz + qz) * qdx + qx` (x fastest, like the block grid).
- `palette`: biome keys (`minecraft:plains`, or a datapack key). `data`: `rle_palette_v1` pairs over the cells.
- `climate` (optional, may be missing or contain `null`): `[temperature, downfall]` per palette entry from `World#getTemperature` / `getHumidity` at the lowest cell where the biome was seen. Vanilla colours use the base temperature, which this equals except for biomes only seen above sea level + 17 (height cooling), so clients SHOULD prefer their own table for vanilla ids and use `climate` for unknown ones. Grass/foliage colour maps, per-biome override colours, water colours and the swamp/dark-forest modifiers are client knowledge (the Quest ships a table taken from the 26.3 biome data).
- Biomes are only sent in snapshots (they change only through commands like `/fillbiome`; the next snapshot picks that up).

**`block_update.light`** (with `light_v1`):

```json
{"v":1,"type":"block_update","seq":4,"t":1760370001050,"snapshot_id":7,
 "palette_add":["minecraft:torch"],"changes":[[1,1,1,3]],
 "light":[[1,1,1,254],[2,1,1,253],[1,1,2,253]]}
```

- `[x, y, z, value]`, region-local, packed as above, for every cell whose light differs from what this session last received. Applied after `changes`. A `block_update` MAY carry only `light` (`changes: []`).
- The light engine updates light a little after the block change event, so the server diffs the region again 4 ticks after a change event, and the periodic safety diff (`stream.safety-diff-ticks`, default 20) catches the rest. More than 16384 light changes (or 4096 block changes) in one diff are sent as a fresh `chunk_snapshot` instead.

Sizes on real terrain (seed 12345, see TESTING.md "Lighting and biome colours"): light adds about 15% to a 64x96x64 snapshot (9 KB) and 25% to a 128x96x128 one (60 KB); biomes add under 2 KB. Fixtures: `tools/fixtures/chunk_snapshot_extras.json`, `block_update_light.json`; `hello.json` / `welcome.json` show the caps.

### 4.6 `player_positions` (S→C)

Sent at `limits.player_hz` (default 10 Hz) whenever there's at least one player in the region bounds expanded by 16 blocks horizontally, plus once with an empty list when the last one leaves.

```json
{"v":1,"type":"player_positions","seq":4,"t":1760370001050,
 "players":[
  {"uuid":"0f1e…","name":"Alex","pos":[112.5,64.0,-30.25],"yaw":135.0,"pitch":10.0,
   "sneaking":false,"skin":"https://textures.minecraft.net/texture/…","skin_model":"slim"}
 ]}
```

- `pos`: world coordinates (doubles) of the player's feet. The client subtracts `origin` itself.
- `yaw`/`pitch`: the player's head (look) direction, §3 convention. The body yaw isn't sent; clients derive it as the game client does (it lags the head by up to 50° and turns toward the walking direction).
- `skin`: optional skin texture URL taken from the player's profile. It's sent only in the first message that includes that UUID on the connection (and after it changes), and it can be `null` (no textures, e.g. on an offline-mode server: show the game's default skin for that UUID). Profile URLs are usually `http://textures.minecraft.net/texture/<hash>`; clients may fetch the same path over https and SHOULD refuse other hosts.
- `skin_model` (optional, additive): `"wide"` (4 px arms, Mojang's "classic") or `"slim"` (3 px arms), from the textures payload's metadata. Sent together with a non-null `skin`; missing means `"wide"`.
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
- Skin source, in priority order: `skin_url` (must be a `https://textures.minecraft.net/texture/…` URL), then `skin_player` (an account name that the server resolves through the Mojang profile lookup once and caches), then the default skin. Every field is optional. Missing fields keep their previous value. A blank `skin_player` means the server's default (plugin `config.yml` `avatar.skin-player`).
- The plugin resolves `skin_player` off the main thread (also on an offline-mode server; it needs internet access) and then gives the giant the signed textures. Until that finishes, and if it fails, the giant carries the bare name, which Java clients since 1.21.9 resolve themselves. A failed lookup sends `error` `skin_failed`.
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

- `skin_resolved`: the server holds signed textures for `skin_player` (it also sends a fresh `avatar_state` when a lookup finishes). `false` doesn't always mean Java players see the default skin: their clients may still resolve the name.
- What the giant looks like and where it is are server policy (ARCHITECTURE.md §3.5, `config.yml` `avatar.placement`):
  - `floating` (default): a player head and two hands, no body, at exactly the mapped head and hand poses of §4.7 (position and full orientation), sized like a real head and hands at `block_size_m`. Here `scale` is the head's scale relative to a player's 0.5-block head, `world_height_blocks` is the head's size in blocks, `entity_uuid` is the head display, and `note` reports a capped head size.
  - `ground`: a Mannequin standing on the terrain under the headset's X/Z, clamped into the region, with hand offsets scaled to its size.
  - `exact`: a Mannequin whose head and hands are at the mapped poses.
- With `debug.avatar-state` on, the `debug` object also reports `placement` and `skin_player`. Floating adds `head`, `head_q`, `head_size_blocks`, `hand_size_blocks` and `hand_block`. Ground and exact add `feet`, `ground_y` and `body_yaw`.

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
| `edit_denied` | no | a `block_edit` was refused: no `edit_v1`, occupied cell, denied state (§4.20) |
| `out_of_region` | no | a `block_edit` outside the session's region (§4.20) |
| `bad_state` | no | a `block_edit` state that isn't a placeable block state (§4.20) |

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
- **v1, light and biomes (2026-10-13, additive):** optional per-block light and 4x4x4 biomes in `chunk_snapshot`, light changes in `block_update`, opted into with the `light_v1` / `biomes_v1` caps and echoed in `welcome.caps` (§4.5.1). Peers without the caps are unaffected.
- **v1, block editing (2026-10-13, additive):** `block_edit` (C→S) breaks or places one block inside the region, opted into with the `edit_v1` cap; error codes `edit_denied`, `out_of_region`, `bad_state`; an error about an edit carries its `op` and `pos` (§4.20). Peers without the cap are unaffected.

## 4.13–4.16 Runtime region control (v1, additive)

- `region_request` (client→server): `{"origin":[x,y,z],"dims":[dx,dy,dz]}` or `{"center":[x,y,z],"size":[dx,dy,dz],"y_origin"?:y}` (origin = center − size/2). The server checks it against `max_region` in config.yml (x, y, z, blocks, bytes). Over the cap it returns `error{code:"region_too_large", max_dims, max_blocks, max_bytes}`.
- `region_changed` (server→all shared sessions): `{world, origin, dims, by_session, ref_seq}`, followed by a full `chunk_snapshot`. When several clients write, the last request wins.
- `region_query` → `region_info{region, players:[{name,uuid,world,pos,yaw}], limits, ref_seq}` (used for "center on me").
- `welcome.limits` gains `max_dims`, `max_blocks`, `max_bytes`. `chunk_snapshot` may carry `server_timing{read_ms,remap_ms,encode_ms}`, which clients ignore if they don't recognise it.
- Growth always triggers a full re-snapshot. Incremental edge growth is deferred.
- Fixtures: `tools/fixtures/region_request.json`, `region_query.json`, `region_changed.json`, `region_info.json`, `error_region_too_large.json` (also parsed by the C# tests).
- Changelog: v1.1 adds the messages above. v1 clients are unaffected.
- Changelog (additive, no version bump): adds `player_positions[].skin_model` (§4.6) and documents the giant's placement and skin lookup (§4.8, §4.9). Fixture: `tools/fixtures/player_positions.json`.

## 4.17–4.19 Moving around: view margin, sectioned and delta snapshots (v1.2, additive)

These let a headset glide across the world while the miniature stays a box on the table. Everything here is optional: a peer that doesn't know a field ignores it, and the server only uses the new snapshot encoding with clients that ask for it. `v` stays 1.

### 4.17 View margin, build height, server caps, pose frame

- `region_request` MAY carry `view_margin: [mx, my, mz]` (ints ≥ 0): blocks on each side of the region that the client keeps loaded but doesn't show. The visible box (the "window") is `dims − 2·view_margin`, and the client slides it inside the region by fractional blocks; terrain from the margin slides into view while the next region loads. The server clamps each value to `⌊(dims − 1) / 2⌋`, keeps it with the shared region (config.yml `region.view_margin`) and echoes it as `view_margin` in `region_changed`, `welcome.region`, `region_info.region` and every `chunk_snapshot`. Missing means `[0,0,0]` (the window is the whole region, as in v1).
- `welcome.caps` (§4.5.1) lists what is in effect for the session: the optional client caps it granted (`light_v1`, `biomes_v1`, and `sections_v1` when the client asked for it and `stream.sections-v1` is on) plus `view_margin` and `pose_snapshot_id`, which this server always supports. A client only asks for a region with a margin it added itself (padding the region) when `caps` has `view_margin`; otherwise the margin would be lost and the region would look bigger after every request.
- `welcome.limits` and `region_info.limits` MAY carry `build_height: [minY, maxY)` of the region's world. Clients never request a region outside it (the server would clamp `dims.y`).
- `pose` MAY carry `snapshot_id`: the snapshot whose `origin` the tabletop frame refers to. A headset moves its region root only when it applies a new snapshot, so poses sent while a moved region is in flight are still relative to the old origin. The server maps a pose with the origin of that snapshot (it remembers the last 8 per connection), or the current region if the id is unknown or absent. Without it, the giant jumps by the shift for a round trip.

```json
{"v":1,"type":"region_request","seq":51,"t":1760370002500,"world":"world","origin":[16,60,0],"dims":[32,16,16],"view_margin":[8,0,0]}
```

Navigation (Quest app, docs/QUEST_SETUP.md "Moving around"): when the window has moved more than a few blocks past the region centre in the direction of motion, the client sends `region_request` with the same `dims` and `view_margin` and the `origin` moved by 16 blocks (one section) on that axis. Origins always move by whole 16-block sections, so the client can keep its section meshes and the server can send a delta (4.18). One request is in flight at a time. The region is shared (4.13), so other headsets follow; the last writer wins.

### 4.18 `chunk_snapshot` encoding `sections_v1` (cap `sections_v1`)

A client that puts `"sections_v1"` in `hello.caps` MAY receive `chunk_snapshot` with `"encoding":"sections_v1"` instead of `rle_palette_v1`. The server MUST NOT send it to other clients (`stream.sections-v1: false` in config.yml turns it off).

```json
{"v":1,"type":"chunk_snapshot","seq":30,"t":1760370003000,"snapshot_id":9,"world":"world",
 "origin":[16,60,0],"dims":[32,16,16],"view_margin":[8,0,0],"order":"yzx",
 "palette":["minecraft:air","minecraft:stone","minecraft:grass_block[snowy=false]","minecraft:oak_log[axis=y]"],
 "encoding":"sections_v1","base":{"snapshot_id":8,"shift":[16,0,0]},"part":0,"parts":1,
 "sections":[{"s":[1,0,0],"data":[3,16,0,4080]}],"biomes":null,"light":null}
```

- The region is cut into 16×16×16 **sections**: section `s = [sx,sy,sz]` covers region-local blocks `16·s` up to `min(16·s + 16, dims)` on each axis (edge sections are smaller when `dims` isn't a multiple of 16). `data` is `rle_palette_v1` pairs (§4.4) over that box, in yzx order inside the section (x fastest, then z, then y). The runs MUST sum to the section's volume.
- `part` (0-based) and `parts` (≥ 1): one snapshot MAY be split over several messages with the same `snapshot_id`, sent in order. Each part carries the full `palette`, `origin`, `dims` and (if any) `base`; together the parts list each section at most once. The server keeps each message under `stream.max-message-bytes` (default 1 MiB), so regions bigger than the 4 MiB frame limit (§1) can be sent. Other message types MAY arrive between parts; `block_update`s for this `snapshot_id` only follow the last part. The client applies the snapshot when it has all parts.
- Without `base`, every section of the region is listed (a full snapshot).
- With `base: {snapshot_id, shift}` it is a **delta** against the client's current snapshot `snapshot_id` (as updated by the `block_update`s it got for it). `shift` is new `origin` − old `origin`, each component a multiple of 16, and `dims` are the same. The client first copies every block of the new region that was in the old one (new local `p` = old local `p + shift`), then decodes the listed sections over it. The server only leaves out sections that lie fully inside the old region and whose blocks are all unchanged. The base palette stays valid: `palette` starts with the client's current palette, unchanged, followed by new entries.
- **Light and biomes** (§4.5.1). With `light_v1` granted, the top-level `light` is `{"encoding":"rle_v1","in_sections":true}` and every listed section also carries `light`: pairs `[value, run]` of packed light (`sky·16 + block`, 0..255) over the section's box, in the same order as `data`. A delta lists a section when its blocks *or* its light changed, and unlisted sections keep the base's light (the base must have light). With `biomes_v1`, part 0 carries the region's whole `biomes` object as in §4.5.1, deltas included, and the other parts have `biomes: null`. Fixture: `tools/fixtures/chunk_snapshot_sections_light.json`.
- A client that can't apply a snapshot (unknown base, wrong totals, a missing section) drops it and sends `error` `bad_snapshot`, optionally with `snapshot_id`. The server then sends the region again as a full snapshot (at most every 2 s per connection). It holds what the client should have had: the last snapshot plus every `block_update` sent for it. Anything newer follows as `block_update`s for the resend's `snapshot_id`, as after any snapshot.
- `server_timing` (if present) is on part 0.
- Sizes on real terrain (docs/TESTING.md "Navigation and bigger areas"): a full sectioned snapshot is about 15% bigger than the same region as one `rle_palette_v1` message, and a one-section shift of a 160×128×160 region is a delta of about 90–160 KB instead of about 800 KB.

### 4.19 Fixtures and tests

- `tools/fixtures/chunk_snapshot_sections.json` (full, snapshot 8), `chunk_snapshot_delta.json` (snapshot 9 as a delta against 8, one section), `region_request_nav.json` (origin form with `view_margin`) and `chunk_snapshot_sections_light.json` (per-section light, biomes on part 0). Java `SectionCodecTest`, Python `test_tabletop_protocol.py` and C# `Tests~/NavTests.cs` decode them and rebuild snapshot 9 from 8.
- `tools/nav_e2e.py` runs a `sections_v1` headset across the world on a live server and checks each delta-assembled region block by block against a v1 client's full snapshot of the same region.
- Changelog: v1.2 (2026-10-13) adds `view_margin`, the server features in `welcome.caps`, `limits.build_height`, `pose.snapshot_id`, the `sections_v1` encoding (with per-section light), and the full resend after `bad_snapshot`. v1 and v1.1 clients are unaffected.
- Changelog (no field changes): the default giant is now `floating` (a head and two hands at the exact poses); `avatar_state.scale` and `world_height_blocks` describe the head in that mode (§4.9).

## 4.20 Block editing from the headset (`block_edit`, `edit_v1`; additive)

The headset breaks and places blocks in the region, like a creative-mode player. It's opt-in per session: the client lists `edit_v1` in `hello.caps`, and the server grants it when its config allows editing (`edit.enabled`, default true; with `edit.require-token: true` only when `auth.token` is set, so every connected headset presented the token) and echoes it in `welcome.caps`. Without the grant every `block_edit` gets `error edit_denied`.

```json
{"v":1,"type":"block_edit","seq":42,"t":1760370004000,
 "op":"place","pos":[105,64,-35],"state":"minecraft:oak_log[axis=x]","face":"east"}
{"v":1,"type":"block_edit","seq":43,"t":1760370004100,"op":"break","pos":[104,63,-35],"face":"up"}
```

| field | type | req | meaning |
|---|---|---|---|
| `op` | string | yes | `break`: the block at `pos` is removed. `place`: `state` goes into `pos`. |
| `pos` | int[3] | yes | World block coordinates of the cell that changes. For `place` that is the cell in front of the clicked face (clicked block + face normal), not the clicked block. Must be inside the session's current region. |
| `state` | string | for `place` | Full block-state string as in the palette (`BlockData#getAsString`). Applied as given: the client picks the orientation (`axis`, `facing`). |
| `face` | string | no | The clicked face of the targeted block: `west`, `east`, `down`, `up`, `north`, `south` (normals −X, +X, −Y, +Y, −Z, +Z). Informational (logged). |

Server behaviour:
- Edits are applied on the main thread in arrival order. Each session may make `edit.max-per-second` edits (default 20): a token bucket holding one second's worth, so a burst of 20 passes and the rest get `rate_limited`.
- `break` of air does nothing. Breaking a block that holds water (waterlogged, kelp, seagrass) leaves a water source, and placing a waterloggable state into a water source sets `waterlogged=true`, as in vanilla. Block physics run after the edit (`edit.physics`), so sand falls and water flows into a gap.
- `place` needs a replaceable cell (air, fluids, short grass and the like: `Block#isReplaceable`) and a state that isn't air or listed in `edit.deny` (by default the operator blocks: command blocks, structure blocks, jigsaws, test blocks). Two-block structures (doors, beds, tall plants) are placed as the one state given.
- There is no acknowledgement. The change reaches every session through the normal `block_update` diff (the edit marks the region dirty), typically 100–200 ms after the edit arrives. Protection plugins are not consulted (no `BlockPlaceEvent`/`BlockBreakEvent`), so only the region bounds, the rate limit and the deny list restrict edits.
- A refused edit gets an `error` with `ref_seq` and, additively, the edit's `op` and `pos`:

| code | when |
|---|---|
| `edit_denied` | no `edit_v1` grant, occupied cell, a state from `edit.deny`, chunk not loaded |
| `out_of_region` | `pos` is outside the session's region |
| `bad_state` | `state` isn't a block state, or is air (use `break`) |
| `rate_limited` | over `edit.max-per-second` |
| `bad_message` | malformed: no `op`/`pos`, non-integer `pos`, unknown `face` (this reply has no `pos`) |

```json
{"v":1,"type":"error","seq":57,"t":1760370004120,"code":"edit_denied","message":"cell is occupied by minecraft:stone",
 "ref_seq":42,"fatal":false,"op":"place","pos":[105,64,-35]}
```

Client guidance (non-normative; the Quest's `World/BlockEdits.cs`): show an edit at once only when the resulting state is already in the session palette. Never append to the palette locally, because `palette_add` indices must keep matching the server's. Remember the cell's old value: a `block_update` that touches the cell confirms the edit, an `error` with that `pos` reverts it, and if neither arrives within 3 s the client reverts too (the server diffs the region at least once a second, so silence means the world kept the old value). An older server answers `unknown_type`: revert everything and stop editing.

Fixtures: `tools/fixtures/block_edit.json`, `block_edit_break.json`, `error_edit_denied.json` (Java `FixtureTest`, Python and C# tests).
