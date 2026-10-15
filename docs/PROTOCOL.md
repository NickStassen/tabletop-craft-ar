# PROTOCOL.md — Tabletop link protocol, v0.3.1

The project version (repo `VERSION` file) versions the plugin, the Quest app and this protocol together. Before 1.0 there is no backwards compatibility: plugin, app and tools of the same version go together. The wire field `v` stays `1`.

Status: **v1, normative.** The server plugin (`server-plugin/`), the Quest app (`quest-app/`) and the fake client (`tools/`) must all implement exactly this. Any breaking change bumps `PROTOCOL_VERSION` and is recorded in the changelog at the bottom.

## 1. Transport

- WebSocket (RFC 6455), plaintext `ws://` on LAN. Default endpoint: `ws://<server-host>:8765/tabletop` (port and path configurable in the plugin's `config.yml`).
- Every message is **one UTF-8 JSON text frame** holding one JSON object. Binary frames are reserved for a later version. A v1 peer that gets a binary frame replies with `error` code `unsupported` and ignores the frame.
- Why JSON and not binary in v1: a 32x32x64 band is only tens of KB once run-length encoded (see §6.3), pose traffic at 30 Hz is about 10 KB/s, and JSON is easy to debug with `websocat` or the fake client. A binary chunk encoding is planned only if measurements show it's needed (PLAN.md, Phase 5).
- Permessage-deflate is optional. The server MAY offer it, and the client MAY decline. Nothing in this spec depends on compression.
- Max frame size the server accepts: 64 KiB. Max frame the server sends: 4 MiB.
- Finding the server on the LAN is a separate small UDP exchange (§7).

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
| `entities` | S→C | non-player entities in the region, as deltas (cap `entities_v1`, §4.21) |
| `actions` | S→C | swings, digging, block breaks/places, damage this tick (cap `actions_v1`, §4.22) |
| `pose` | C→S | head + hands, about 30 Hz |
| `avatar_config` | C→S | set skin/scale/visibility of the giant |
| `avatar_state` | S→C | the server's view of the avatar after config |
| `ping` | both | latency probe |
| `pong` | both | reply to ping |
| `error` | both | problem report |
| `bye` | both | graceful close |
| `block_edit` | C→S | break or place one block from the headset (§4.20, needs `edit_v1`) |
| `block_registry` | S→C | every block the headset may place, by menu category (§4.24), once after `welcome` |
| `chat` | S→C | a chat, join, quit, death, advancement, broadcast, say/me, whisper or headset line (§4.26, needs `chat_v1`) |
| `chat_send` | C→S | the headset says something in game chat (§4.26) |
| `command` | C→S | run a server command, optionally at the block the headset points at (§4.27, needs `command_v1`) |
| `command_result` / `command_output` | S→C | a command's captured feedback, then any late output (§4.27) |
| `command_complete` / `command_suggestions` | C→S / S→C | tab completion for a partial command line (§4.27) |
| `server_status` | S→C | TPS, MSPT, memory, players, worlds, every 2 s (§4.28, needs `status_v1`) |
| `world_query` / `world_info` | C→S / S→C | a world's time, weather, difficulty, tick state and game rules (§4.28) |
| `players_query` / `players_info` | C→S / S→C | every online player with health, food, game mode, ping (§4.28) |

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
- `caps`: capability strings. v1 defines `rle_palette_v1` (required) and `avatar` (client will send poses). Optional extras: `light_v1` and `biomes_v1` (§4.5.1), `edit_v1` (§4.20), `entities_v1` (§4.21), `actions_v1` (§4.22), `chat_v1`, `command_v1` and `status_v1` (§4.26–4.28); unknown caps are ignored.
- `operator_name` (optional string): the name the headset wants in game chat (§4.26). The server keeps `[A-Za-z0-9_]`, at most 16 characters.
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

Sent at `limits.player_hz` (default 10 Hz) whenever there's at least one player in the region bounds expanded by `stream.players-margin` (16) blocks on every axis, plus once with an empty list when the last one leaves. Next version: the margin applies to Y too (a player deep in a cave under the region isn't listed), and spectators, invisible players (the flag or the potion) and vanished players (a true `vanished` metadata value, as vanish plugins set, or hidden from any other online player) are never listed.

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
- Additive (sent to every client; old ones ignore them), see `tools/fixtures/player_positions_riding.json`:
  - `id`: the player's entity id, the id that `entities` lists in a vehicle's `passengers` and `actions` uses for swings and damage.
  - `vehicle`: entity id of what the player rides (boat, minecart, horse, pig...), missing when not riding. Clients seat the figure relative to that entity (§4.21).
  - `pose`: missing = standing; else `sneaking`, `swimming`, `sleeping`, `gliding` (elytra), `riding` (whenever `vehicle` is set), `spin_attack` or `dying`.
  - `equipment`: object of the non-empty slots `main_hand`, `off_hand`, `head`, `chest`, `legs`, `feet` → item key (`minecraft:diamond_sword`); missing when all are empty.
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
- Next version: the client SHOULD treat 6 s without any inbound bytes as a dead link (Wi-Fi gone without a TCP FIN, a frozen server), drop the socket and reconnect. The Quest app does, and clears its RTT while the link is down. A big snapshot in transit counts as traffic.

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
| `chat_denied` | no | a `chat_send` without `chat_v1`, or `chat.send` is off (§4.26) |
| `command_denied` | no | a `command` or `command_complete` without `command_v1`, or a command the server's policy refuses (§4.27) |

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

A client SHOULD NOT reconnect by itself after 4001 or 4003: nothing changes until the app, the plugin, the token or the address does. The Quest app stops retrying and shows its setup card (QUEST_SETUP.md Part D); it reconnects with backoff after every other close.

## 6. Implementation notes (non-normative)

1. **Encoder reference (Java):** walk `i = 0 … N−1` in yzx order, look up `ChunkSnapshot.getBlockData(x&15, y, z&15).getAsString()`, map it to a palette index with a `HashMap<String,Integer>` (cache by `BlockData` identity), and emit a pair whenever the index changes.
2. **Decoder reference (C#/Python):** expand the pairs into a `ushort[]` of length N and check the total.
3. **Size check:** a typical 32x64x32 band (65,536 blocks), mostly air above and stone below, has about 500–3,000 runs, so 5–30 KB of JSON. A worst-case noisy 32x384x32 region (393,216 blocks, all runs of 1) is about 2.5 MB of JSON, which is still under the 4 MiB cap. See RESEARCH.md §2.
4. **Conformance:** `tools/` ships golden JSON fixtures for every message type. Both the Java unit tests and the fake client round-trip them.

## 7. LAN discovery (UDP, outside the WebSocket; next version)

So the headset can find a server without typing its address (QUEST_SETUP.md Part D, "Find servers"). It is a probe and a reply, each one UDP datagram holding one UTF-8 JSON object. There is no envelope (`seq`, `t`).

```json
{"type":"tabletop_discover","v":1}
{"type":"tabletop_server","v":1,"name":"A Minecraft Server","port":8765,"path":"/tabletop","token":false,"version":"0.3.0","mc":"26.3","players":2}
```

- **Probe (headset → broadcast).** The client sends `tabletop_discover` to UDP port 8765 (the plugin's `discovery.port`; the app's tabletop.json `discovery_port` if changed) at `255.255.255.255` and at its own /24's broadcast address, then listens on its sending socket for about 1.2 s.
- **Reply (plugin → the probe's sender, unicast).** `tabletop_server` with `name` (config `discovery.name`, else the server's MOTD as plain text, one line, at most 48 characters), the WebSocket `port` and `path` (`bind.port`, `bind.path`), `token` (true when `auth.token` is set, so the client knows it needs one), the plugin `version`, the game version `mc` and the number of online `players`. The client builds the URL from the reply's source address: `ws://<source>:<port><path>`.
- The plugin answers only probes with `v` 1 from private (10/8, 172.16/12, 192.168/16), link-local or loopback addresses, at most 20 replies a second, and ignores everything else. `discovery.enabled: false` turns it off.
- Why a probe and not a periodic beacon: an Android app receives broadcast or multicast only while it holds a `WifiManager.MulticastLock` (with the `CHANGE_WIFI_MULTICAST_STATE` permission), while sending a broadcast and receiving a unicast reply need neither. The Quest app's manifest is unchanged.
- **Fallback (client only).** If nothing answers (a firewall that lets only TCP 8765 through), the Quest app connects to TCP 8765 on every host of its /24 (400 ms each, 48 at a time) and lists the hosts that accept as "port open". It never sends `hello` to them; connecting is the user's choice.
- Fixtures: `tools/fixtures/discovery_probe.json`, `discovery_reply.json` (Java `LanBeaconTest`, Python `test_discovery_*`, C# `Tests~/ComfortTests.cs`).

## Changelog

- **v1 (2026-10-13):** initial.
- **v1, light and biomes (2026-10-13, additive):** optional per-block light and 4x4x4 biomes in `chunk_snapshot`, light changes in `block_update`, opted into with the `light_v1` / `biomes_v1` caps and echoed in `welcome.caps` (§4.5.1). Peers without the caps are unaffected.
- **v1, block editing (2026-10-13, additive):** `block_edit` (C→S) breaks or places one block inside the region, opted into with the `edit_v1` cap; error codes `edit_denied`, `out_of_region`, `bad_state`; an error about an edit carries its `op` and `pos` (§4.20). Peers without the cap are unaffected.
- **v0.2.0: block menu (2026-10-14, required, not backwards compatible):** the server sends `block_registry` (§4.24) to every client right after `welcome`, and a `block_edit` `place` must name a registry block or variant (else `bad_state`, §4.20). No cap: plugin, Quest app and tools must match.
- **next version: faster gliding (§4.17, §4.25), no field changes.** A glide `region_request` may move the origin by several whole sections at once; the default `max_region` is 352×384×352 and 25,165,824 blocks (a config still holding the 0.3.0 defaults is raised on start); a server reads only the sections of a moved or resized region that weren't in the old one (the delta lists exactly those). Fixture `region_changed.json` carries the new default limits.
- **next version: chat, commands and server status (§4.26–4.28).** New caps `chat_v1`, `command_v1` (needs `auth.token` by default) and `status_v1`; messages `chat`, `chat_send`, `command`, `command_result`, `command_output`, `command_complete`, `command_suggestions`, `server_status`, `world_query`, `world_info`, `players_query`, `players_info`; `hello.operator_name`; `welcome.chat` and `welcome.commands`; errors `chat_denied`, `command_denied`. Fixtures `hello_control.json`, `welcome_control.json`, `chat*.json`, `command*.json`, `error_command_denied.json`, `server_status.json`, `world_query.json`, `world_info.json`, `players_query.json`, `players_info.json`.
- **v0.3.0: visible width presets (§4.25).** `limits.max_view_width` (required in `welcome.limits`, `region_info.limits` and `region_too_large`); a `region_request` whose visible width is over it is narrowed around its centre instead of refused; `sections_v1` deltas may change `dims` (a section-aligned resize, §4.18). Fixtures `chunk_snapshot_grow.json`, `chunk_snapshot_shrink.json`, `region_request_resize.json`.
- **v0.3.1: sessions and discovery.** `region_changed` carries `limits` like `region_info` (with the new world's `build_height`, §4.13); LAN discovery over UDP (§7); `player_positions` filters on Y and leaves out spectators, invisible and vanished players (§4.6); clients drop a link after 6 s of silence (§4.10) and don't retry after 4001/4003 (§5). Fixtures `region_changed.json` (now with `limits`), `discovery_probe.json`, `discovery_reply.json`.
- **v0.3.1: building like vanilla (§4.20).** No new fields. `block_edit` `pos` may be the clicked block itself when it is replaceable; the server places doors, beds and tall plants with both halves (and removes both on a break), checks vanilla replaceability and survival, refuses blocks with a collision box in a player or mob and edits outside the world border, makes placed leaves persistent, sets `waterlogged` from the cell, computes connection properties from the neighbours, and names blocks readably in refusals ("stone is in the way"; fixture `error_edit_denied.json`).
- **v0.3.1: armour stands (§4.21).** `entities` entries carry `arms`, `base_plate` and `stand_pose` for armour stands; fixture `entities_armor_stand.json` (Java `EntityDeltaTest`, Python, C# `EntityTests`).

## 4.13–4.16 Runtime region control (v1, additive)

- `region_request` (client→server): `{"origin":[x,y,z],"dims":[dx,dy,dz]}` or `{"center":[x,y,z],"size":[dx,dy,dz],"y_origin"?:y}` (origin = center − size/2). The server checks it against `max_region` in config.yml (x, y, z, blocks, bytes). Over the cap it returns `error{code:"region_too_large", max_dims, max_blocks, max_bytes}`.
- `region_changed` (server→all shared sessions): `{world, origin, dims, by_session, ref_seq}`, followed by a full `chunk_snapshot`. When several clients write, the last request wins. Next version: it also carries `view_margin` (§4.17) and `limits` as in `region_info` (`max_dims`, `max_blocks`, `max_bytes`, `max_view_width` and the region's world's `build_height`), so a client that moved to another world (area setup centred on a player in the Nether, say) glides within the right height at once.
- `region_query` → `region_info{region, players:[{name,uuid,world,pos,yaw}], limits, ref_seq}` (used for "center on me").
- `welcome.limits` gains `max_dims`, `max_blocks`, `max_bytes`. `chunk_snapshot` may carry `server_timing{read_ms,remap_ms,encode_ms}`, which clients ignore if they don't recognise it.
- Growth triggers a full re-snapshot for `rle_palette_v1` clients. For `sections_v1` clients a resize whose origin moves by whole sections is a delta with only the new sections (§4.25); `limits` also carry `max_view_width` (§4.25).
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

Navigation (Quest app, docs/QUEST_SETUP.md "Moving around"): when the window has moved more than a few blocks past the region centre in the direction of motion, the client sends `region_request` with the same `dims` and `view_margin` and the `origin` moved by 16 blocks (one section) on that axis; while it moves steadily one way it may move the origin by several sections at once, so that the margin ahead of the window is runway (next version; QUEST_SETUP.md Part H). Origins always move by whole 16-block sections, so the client can keep its section meshes and the server can send a delta (4.18). One request is in flight at a time. The region is shared (4.13), so other headsets follow; the last writer wins.

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
- With `base: {snapshot_id, shift}` it is a **delta** against the client's current snapshot `snapshot_id` (as updated by the `block_update`s it got for it). `shift` is new `origin` − old `origin`, each component a multiple of 16. The `dims` may differ from the base's (a resize, §4.25); the two regions overlap on every axis. The client first copies every block of the new region that was in the old one (new local `p` = old local `p + shift`), then decodes the listed sections over it. The server only leaves out sections that lie fully inside the old region and whose blocks are all unchanged. The base palette stays valid: `palette` starts with the client's current palette, unchanged, followed by new entries.
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
| `pos` | int[3] | yes | World block coordinates of the cell that changes. For `place` that is the cell in front of the clicked face (clicked block + face normal), or the clicked block itself when that block is replaceable (short grass, a single snow layer, vines: vanilla places into it). Must be inside the session's current region. For a two-block structure it is the lower half's or the foot's cell. |
| `state` | string | for `place` | Full block-state string as in the palette (`BlockData#getAsString`). The client picks the orientation the way vanilla's `getStateForPlacement` does (`axis`, `facing`, `face`, `half`, `type`, `rotation`, `hinge`, wall forms, see the client guidance below); the server keeps it, apart from the adjustments listed under "Server behaviour". |
| `face` | string | no | The clicked face of the targeted block: `west`, `east`, `down`, `up`, `north`, `south` (normals −X, +X, −Y, +Y, −Z, +Z). Informational (logged). |

Server behaviour:
- Edits are applied on the main thread in arrival order. Each session may make `edit.max-per-second` edits (default 20): a token bucket holding one second's worth, so a burst of 20 passes and the rest get `rate_limited`.
- `break` of air does nothing. Breaking a block that holds water (waterlogged, kelp, seagrass) leaves a water source, as in vanilla. Block physics run after the edit (`edit.physics`), so sand falls and water flows into a gap.
- Edits outside the world border are refused (`edit_denied`, "outside the world border").
- `place` follows vanilla 26.3 (`BlockItem#place`), server side in `EditWorld`/`EditRules`:
  - the cell must be replaceable (vanilla `canBeReplaced`): air, fluids, fire, light, short and tall grass, ferns, bushes, dead bushes, seagrass, vines, glow lichen, sculk vein, resin clumps, hanging roots, nether roots and sprouts, leaf litter, and snow only as a single layer; never by the same block (stacking snow or candles and merging slabs are not supported). Else `edit_denied` "`<block>` is in the way";
  - the state must be able to stay there (`canSurvive`: a torch needs a block under or behind it, grass needs dirt, a door a floor). Else `edit_denied` "`<block>` can't stay there";
  - a state with a collision box must not go into a player (not in spectator mode), a mob, an armour stand, a boat, a minecart or an end crystal (vanilla `isUnobstructed`; the giant avatar's own entities don't count). Else `edit_denied` "`<player name>` is in the way" / "a cow is in the way";
  - the state isn't air and isn't listed in `edit.deny` (by default the operator blocks: command blocks, structure blocks, jigsaws, test blocks);
  - adjustments: leaves get `persistent=true` (placed leaves never decay); `waterlogged` is set to whether the cell held a water source (a still water block or a block holding water), true or false, whatever the state said; a single block's connection properties (fences, panes, bars, walls, stair `shape`, redstone wire, `snowy` grass...) are computed from its neighbours with vanilla `Block.updateFromNeighbourShapes`;
  - two-block structures get both halves: a door or double plant (`half=lower`, also small dripleaf and the pitcher plant) its `half=upper` one cell up, a bed (`part=foot`) its head one cell along `facing`. A state naming the top half or the head is placed from the bottom half or the foot at `pos`. The second cell must be inside the region and the build height, inside the world border and replaceable, and nobody may stand in it; else `edit_denied` "no room for the `<block>` ...". Breaking either half removes both (without drops), and replacing one half of tall grass or a large fern removes its other half.
- Since v0.2.0 the state's block must also be in the session's `block_registry` (§4.24), or be one of its `variants` (a wall torch picked from a wall is placed as is). Anything else (fire, water, portals, piston heads, potted plants: blocks without an item form) gets `bad_state` with the message "`<block>` is not a placeable block (not in block_registry)".
- Refusal messages name blocks by their id path with spaces ("oak stairs", "command block"), not as raw state strings, so a client can show them as they are.
- There is no acknowledgement. The change reaches every session through the normal `block_update` diff (the edit marks the region dirty), typically 100–200 ms after the edit arrives. Protection plugins are not consulted (no `BlockPlaceEvent`/`BlockBreakEvent`), so only the region bounds, the rate limit and the deny list restrict edits.
- A refused edit gets an `error` with `ref_seq` and, additively, the edit's `op` and `pos`:

| code | when |
|---|---|
| `edit_denied` | no `edit_v1` grant, the cell isn't replaceable, the block can't stay there, a player or mob is in the way, no room for the other half, outside the world border, a state from `edit.deny`, chunk not loaded |
| `out_of_region` | `pos` is outside the session's region |
| `bad_state` | `state` isn't a block state, is air (use `break`), or (since v0.2.0) isn't a `block_registry` block or variant |
| `rate_limited` | over `edit.max-per-second` |
| `bad_message` | malformed: no `op`/`pos`, non-integer `pos`, unknown `face` (this reply has no `pos`) |

```json
{"v":1,"type":"error","seq":57,"t":1760370004120,"code":"edit_denied","message":"stone is in the way",
 "ref_seq":42,"fatal":false,"op":"place","pos":[105,64,-35]}
```

Client guidance (non-normative; the Quest's `World/BlockEdits.cs`): show an edit at once only when the resulting state is already in the session palette. Never append to the palette locally, because `palette_add` indices must keep matching the server's. Remember the cell's old value: a `block_update` that touches the cell confirms the edit, an `error` with that `pos` reverts it, and if neither arrives within 3 s the client reverts too (the server diffs the region at least once a second, so silence means the world kept the old value). An older server answers `unknown_type`: revert everything and stop editing. A sectioned delta (§4.18) keeps the guesses in the sections it copied from the old grid (moved with the origin) and drops those in the sections it sent; a full snapshot drops them all. An edit whose result isn't shown (a state new to the palette) leaves its cell looking unchanged, so treat that cell as taken for about a second (or until a `block_update` or `error` for it) instead of sending it again while a trigger is held.

Placement (non-normative; `World/BlockPlacement.cs`, after vanilla 26.3's `getStateForPlacement`; the "look" is the controller ray, standing in for the player's view): aiming at a replaceable block places into its cell. Logs and pillars take the clicked face's axis; most blocks with a horizontal `facing` face the user, stairs, doors, fence gates, beds, campfires, calibrated sculk sensors and decorated pots face the look, anvils its clockwise side; observers face the nearest look direction (6-way), pistons, dispensers, droppers and barrels the opposite; end rods, lightning rods, amethyst and shulker boxes point out of the clicked face, hoppers into the clicked block (down from above); torches, signs, banners, heads, coral fans and hanging signs use their wall form (the registry's `variants`) on a side face; buttons, levers and grindstones get `face` floor/wall/ceiling, bells `attachment`; slabs, stairs and trapdoors take the top half from the underside or the upper half of a side; doors get their hinge from the neighbours and the hit point; lanterns hang under a block; vines and lichen cling to the face; rails run along the look; signs, banners and heads get their 16-step `rotation` from the yaw. A B pick selects the block's registry default state (a wall torch picks the torch, a door's top half the door).

Fixtures: `tools/fixtures/block_edit.json`, `block_edit_break.json`, `error_edit_denied.json` (Java `FixtureTest`, Python and C# tests).

## 4.21–4.23 Entities and actions (additive, caps `entities_v1`, `actions_v1`)

Non-player entities (boats, minecarts, mobs, dropped items, item frames, paintings, falling blocks...) and what players and mobs do (arm swings, digging, breaking and placing blocks, taking damage), so the miniature shows them. Both are opt-in: a client lists `entities_v1` and/or `actions_v1` in `hello.caps`, the server grants each unless it's off in config.yml (`entities.enabled`, `actions.enabled`) and lists the granted ones in `welcome.caps`. Clients without them never get these messages. `v` stays 1.

### 4.21 `entities` (S→C)

Sent with `player_positions` (10 Hz) to sessions with `entities_v1` whenever something changed, and always after the session's region changed.

```json
{"v":1,"type":"entities","seq":5,"t":1760370001050,"full":true,
 "entities":[
  {"id":101,"type":"minecraft:oak_boat","uuid":"3c1e…","pos":[112.5,62.53,-30.25],"yaw":90.0,"pitch":0.0,
   "passengers":[7,102],"size":[1.38,0.56]},
  {"id":102,"type":"minecraft:pig","uuid":"3c1e…","pos":[112.1,62.6,-30.25],"yaw":80.0,"pitch":0.0,"body_yaw":90.0,
   "vehicle":101,"size":[0.45,0.45],"baby":true,"variant":"temperate"},
  {"id":104,"type":"minecraft:zombie","uuid":"3c1e…","pos":[108.7,64.0,-28.0],"yaw":-45.0,"pitch":12.5,"body_yaw":-40.0,
   "on_ground":true,"size":[0.6,1.95],"equipment":{"main_hand":"minecraft:iron_sword","head":"minecraft:leather_helmet"}},
  {"id":105,"type":"minecraft:item","uuid":"3c1e…","pos":[110.42,64.0,-33.9],"yaw":0.0,"pitch":0.0,"on_ground":true,
   "size":[0.25,0.25],"item":"minecraft:diamond","count":3}
 ]}
```

- **Which entities:** every entity whose bounding box touches the region box grown by `entities.margin` (default 2) blocks on each side, except players (they're in `player_positions`), the giant's own entities (scoreboard tag `tabletop_avatar`), markers, interactions, text displays and area effect clouds. At most `entities.max` (default 256); past that, the ones nearest the region centre win.
- **Deltas:** the server remembers what it sent each session. An entry for an `id` the client doesn't hold yet is a **full entry** (every field that applies; false/null/empty ones are left out). An entry for a known `id` lists only what changed: `pos` when it moved by at least 0.01 block on an axis, angles when they turned by at least 1°, any other field when it differs. A field that went away is sent as `false`, `null` or `[]`. `removed` lists ids that left the region or despawned. With `full: true` the list replaces the client's whole set: entities it doesn't name are gone, and its entries are full entries. The server sends one after every region change. A client that gets a delta for an `id` it doesn't hold ignores it.
- **Fields** (all optional except `id`; a full entry always has `type` and `pos`):
  - `id` (int): the server's entity id, unique while the entity exists. `uuid` (string, full entries).
  - `type`: entity type key: `minecraft:cow`, `minecraft:oak_boat` (26.x has one type per boat wood), `minecraft:item`...
  - `pos`: world coordinates (2 decimals) of the entity's position: the feet or bottom centre, or for a hanging entity the centre of it on its wall.
  - `yaw`, `pitch`: degrees, §3 convention. For living entities they are the **head**'s, and `body_yaw` is the body's. Other entities have no `body_yaw` (their yaw is the body's).
  - `on_ground` (bool), `vehicle` (id of what it rides), `passengers` (ids in seat order, players included; see `player_positions[].id`).
  - `size`: `[width, height]` of the bounding box in blocks. Clients draw a box of this size for types they don't know, and slimes scale with it.
  - Looks: `baby`, `small` (armour stands), `sheared`, `tamed`, `angry`, `invisible` (bools). `variant`: the registry key path for vanilla ids (`temperate`, `pale`, `calico`, `desert`, `kebab`), `namespace:path` otherwise; enum values in lower case (horse colour `dark_brown`, rabbit `salt_and_pepper`, panda gene, axolotl, parrot, fox, llama colour, salmon size, pufferfish `small`/`medium`/`big`, tropical fish pattern). `color`: a dye colour (sheep wool, shulker, tropical fish body, a tamed wolf's or cat's collar). `markings`: horse markings (`white`, `whitefield`, `white_dots`, `black_dots`) or the tropical fish pattern colour. `profession`: villagers and zombie villagers; missing for none. `pose`: as for players, plus `sitting`; missing = standing. `name`: the custom name as plain text.
  - Armour stands: `arms` and `base_plate` (bools, like the other looks: left out when false) say whether the stand shows its arms and its base plate (a stand placed in survival has a base plate and no arms). `stand_pose`: the stand's pose in degrees, `{"head": [x, y, z], "body": [...], "left_arm": [...], "right_arm": [...], "left_leg": [...], "right_leg": [...]}` (0.1° steps, the game's `Rotations`); left out when every part has the game's default pose (head and body 0, left arm −10/0/−10, right arm −15/0/10, left leg −1/0/−1, right leg 1/0/1). The whole object is resent when any angle changes, and `null` clears it. Fixture `tools/fixtures/entities_armor_stand.json`.
  - Items and blocks: `item` (item key of a dropped item, an item frame's item or an item display's item), `count` (dropped stacks over 1), `block` (block state of a falling block or a block display), `facing` (`north`, `south`... of item frames, paintings and leash knots), `rotation` (an item frame's item rotation, 0–7).
  - `equipment`: as for players, plus `body` (horse or wolf armour, llama carpet) and `saddle`. The whole object is resent when any slot changes.
- Cost (TESTING.md "Entities and actions"): one sample per region per send, about 0.1–0.2 ms of main thread for 200 mobs; about 12–16 KB/s with 200 wandering mobs, under 100 B/s for a still scene.

### 4.22 `actions` (S→C)

At most one per tick, to sessions with `actions_v1`, with what happened inside their region during that tick.

```json
{"v":1,"type":"actions","seq":8,"t":1760370001100,
 "actions":[
  {"kind":"swing","id":7,"hand":"main"},
  {"kind":"dig","pos":[105,63,-36],"stage":4,"id":7},
  {"kind":"break","pos":[106,63,-36],"block":"minecraft:stone","id":7},
  {"kind":"place","pos":[104,64,-36],"block":"minecraft:oak_planks","id":7},
  {"kind":"hurt","id":104,"crit":true},
  {"kind":"swing","id":104,"hand":"main"}
 ]}
```

- `swing`: entity `id` (a player's `player_positions[].id` or a mob's) swings its `hand` (`main`/`off`). Players: every swing packet (attacking, digging, using items). Mobs: when they hit something.
- `dig`: the block at world `pos` is being broken, crack `stage` 0–9 (the game's destroy stages); `-1` = the cracks are gone (digging stopped). `id` = who digs, if known. A `break` at the same `pos` also clears them, and clients drop cracks that got no update for 20 s, like the game.
- `break`: the block state `block` at `pos` was broken by a player (`id`) or an explosion (no `id`; at most 48 blocks per explosion). The block itself goes away through `block_update` as usual; this is for the break particles.
- `place`: the block state `block` was placed at `pos` by player `id` (a small puff; the arm swing comes as its own `swing`).
- `hurt`: entity or player `id` took damage (the red flash); `crit` marks a critical hit.
- Repeats within a tick collapse: the latest `dig` per block, one `swing` per entity and hand, one `hurt` per entity. At most 128 actions per tick. Blocks count when they are inside the region; players up to 16 blocks out horizontally and other entities up to 2, like `player_positions` and `entities`.

### 4.23 Fixtures, tests, changelog

- Fixtures: `tools/fixtures/entities.json` (a full list: a boat carrying a player and a baby pig, a sheep, an armed zombie, a dropped stack, an item frame), `entities_delta.json` (moves, a turned boat, changed equipment, a removal), `entities_armor_stand.json` (a survival stand, a posed stand with arms and a sword, a small invisible stand with a helmet), `player_positions_riding.json` (a player in that boat with full equipment) and `actions.json`. Java `EntityDeltaTest` and `ActionBatchTest` build them from server-side state; Python `test_tabletop_protocol.py` and C# `Tests~/EntityTests.cs` apply them.
- `tools/entities_check.py` checks a live server: types, positions, the vehicle link, equipment, a removal, hurt and explosion actions, and bytes/s.
- Changelog (additive, no version bump, 2026-10-13): adds the `entities_v1` and `actions_v1` caps with the `entities` and `actions` messages, and `player_positions[].id`, `vehicle`, `pose` and `equipment` (§4.6). Clients without the caps are unaffected.

## 4.24 `block_registry` (S→C): the blocks the headset can place

The headset's block menu (docs/QUEST_SETUP.md "Block menu") lists every block the server lets it place, so the giant can build with any block, not only ones already in the region. The server sends this once per connection, right after `welcome` and before the first `chunk_snapshot`. Every client gets it (no cap; pre-1.0, plugin, app and tools change together).

```json
{"v":1,"type":"block_registry","seq":1,"t":1760370000013,
 "count":13,
 "categories":[
  {"id":"building","label":"Building Blocks","blocks":["minecraft:diamond_block","minecraft:oak_log[axis=y]","minecraft:oak_stairs[facing=north,half=bottom,shape=straight,waterlogged=false]","minecraft:stone"]},
  {"id":"colored","label":"Colored Blocks","blocks":["minecraft:white_wool","minecraft:glass"]},
  {"id":"natural","label":"Natural Blocks","blocks":["minecraft:grass_block[snowy=false]","minecraft:poppy","minecraft:wheat[age=0]"]},
  {"id":"functional","label":"Functional Blocks","blocks":["minecraft:chest[facing=north,type=single,waterlogged=false]","minecraft:torch"]},
  {"id":"redstone","label":"Redstone Blocks","blocks":["minecraft:rail[shape=north_south,waterlogged=false]","minecraft:redstone_wire[east=none,north=none,power=0,south=none,west=none]"]},
  {"id":"other","label":"Other","blocks":[]}],
 "items":{"minecraft:wheat":"minecraft:wheat_seeds","minecraft:redstone_wire":"minecraft:redstone"},
 "variants":{"minecraft:wall_torch":"minecraft:torch"}}
```

| field | type | meaning |
|---|---|---|
| `count` | int | number of blocks listed over all categories |
| `categories` | array | menu tabs in display order: `id` (`building`, `colored`, `natural`, `functional`, `redstone`, `other`), `label`, and `blocks` |
| `categories[].blocks` | string[] | each block's **default state** (`BlockData#getAsString()` of `BlockType#createBlockData()`), in menu order. The block id is the part before `[`. A block appears in one category only. |
| `items` | object | block id → item id, only where the block is placed with a differently named item (`redstone_wire` with `redstone`, crops with their seeds). Clients use it for icons and names. |
| `variants` | object | block id → registry block id, for blocks that aren't listed but are placed with a listed block's item: wall torches, wall signs and hanging signs, wall heads, wall banners, wall coral fans, filled cauldrons. A `block_edit` may place them (a B pick from a wall). |

Which blocks (server, `BlockRegistry` + `RegistryScan`):
- every item with a block form (`ItemType#getBlockType`), keeping blocks whose item is their own (not the variants above), that are enabled by the world's feature flags;
- minus air, `barrier`, `light`, `structure_void` and the legacy `petrified_oak_slab`, and minus config.yml `edit.deny` (command blocks, structure blocks, jigsaws, test blocks).

Categories follow vanilla's creative tabs as closely as the API allows. Paper exposes no tab data (`ItemType#getCreativeCategory` is deprecated and answers one value for everything), so the server assigns them from block tags and names, first match wins:
1. **redstone**: tags `rails`, `buttons`, `pressure_plates`; redstone wire, torch and block, repeater, comparator, target, lever, tripwire (hook), daylight detector, pistons, slime and honey blocks, dispenser, dropper, crafter, hopper, observer, note block, redstone lamp, TNT, sculk sensors, trapped chest, lightning rods and copper bulbs.
2. **colored**: tags `wool`, `wool_carpets`, `terracotta`, `candles`, `beds`, `banners`, `shulker_boxes`; a dye colour prefix followed by wool, carpet, terracotta, concrete, stained glass, shulker box, bed, candle or banner (their stairs and slabs too); plain glass, glass pane, tinted glass, terracotta, shulker box and candle.
3. **functional**: tags `all_signs`, `anvil`, `cauldrons`, `campfires`, `flower_pots`; workstations (crafting table, furnaces, smithing, loom, stonecutter, grindstone, enchanting table, brewing stand...), storage (chests, barrel, ender chest, shelves), bookshelves, lectern, beacon, conduit, bell, jukebox, ladder, scaffolding, torches and lanterns (not jack o'lanterns or sea lanterns), heads and skulls, copper golem statues, spawners and vaults, infested and suspicious blocks, end rod, end portal frame, dragon egg, heavy core, cake, creaking heart.
4. **natural**: tags `leaves`, `saplings`, `flowers`, `small_flowers`, `crops`, `dirt`, `sand`, `ice`, `nylium`, `corals`, `coral_blocks`, `wall_corals`, `wart_blocks`, `cave_vines`; ores, raw ore blocks, amethyst buds and clusters, mushrooms, fungi, roots, propagules, dead coral, shrubs, and a list of soils and plants (grass block, podzol, mud, clay, gravel, snow, moss, sculk, netherrack, end stone, kelp, vines, bamboo, cactus, pumpkins, melons...).
5. **building**: tags `logs`, `planks`, `stairs`, `slabs`, `walls`, `fences`, `fence_gates`, `doors`, `trapdoors`, `base_stone_overworld`, `base_stone_nether`; names containing brick, stone, planks, wood, hyphae, stem, log, copper, quartz, purpur, prismarine, deepslate, tuff, granite, diorite, andesite, basalt, `_block`, bars, chain, grate, tiles, pillar, polished/smooth/chiseled/cut, sandstone, resin...
6. **other**: anything left (0 blocks on 26.3).

Order inside a category: Paper doesn't expose vanilla's registry order either, so families are grouped (`BlockRegistry.sortKey`): oak log, stripped oak log, oak wood, stripped oak wood, oak planks, oak stairs, slab, fence, fence gate, door, trapdoor...; stone, its stairs and slab, stone bricks, mossy stone bricks...; all copper blocks by weathering, then waxed; colored blocks by kind (wool, carpet, terracotta, glazed terracotta, concrete, concrete powder, glass, panes, shulker boxes, beds, candles, banners), each in vanilla's dye order (white, light gray, gray, black, brown, red, orange, yellow, lime, green, cyan, light blue, blue, purple, magenta, pink). Families are alphabetical.

Size on Paper 26.3: 1130 blocks (building 433, colored 263, natural 228, functional 129, redstone 77, other 0), 67 variants, 14 items, **66.9 KB** of JSON; built once on plugin start and on `/tabletop reload` (about 100–170 ms on the main thread, logged as `REGISTRY ...`).

Client guidance (non-normative; the Quest's `Net/BlockRegistry.cs`): a state is placeable when its block id, or the block its `variants` entry names, is listed. Check before sending a `place` and before accepting a B pick, so the user sees "can't place fire" at once instead of a revert. Names come from the user's own pack (`assets/minecraft/lang/en_us.json`: `block.<ns>.<path>`, or `item.<ns>.<path>` of the `items` entry); without it the client derives "Oak Stairs" from the id.

Fixture: `tools/fixtures/block_registry.json`. Java `BlockRegistryTest` (filtering, categories, order, placement check) and `FixtureTest` build it from the same blocks; Python `test_tabletop_protocol.py` validates it (`validate_block_registry`: unique blocks, `count`, variants pointing at listed blocks); C# `Tests~/MenuTests.cs` parses it. `tools/edit_check.py` checks a live server: the message arrives between `welcome` and the first snapshot, a registry block new to the region (diamond block) is placed and comes back through `palette_add`, and fire is refused with `bad_state`.

## 4.25 Visible width presets: `max_view_width` and resize deltas (v0.3.0)

The headset changes how wide the visible window is (x and z: 64, 96, 128, 192 or 256 blocks, docs/QUEST_SETUP.md Part K) without reconnecting. A resize is an ordinary `region_request` (§4.13, origin form with `view_margin`, §4.17) whose window is centred where the headset looks now and whose `origin` differs from the current one by whole 16-block sections on every axis. The server answers with `region_changed` and a `sections_v1` delta (§4.18) that only lists the sections that weren't fully inside the old region: the new ring when growing, nothing (or a few changed sections) when shrinking. The height stays the same.

**`limits.max_view_width`** (int, required in `welcome.limits`, `region_info.limits` and the `region_too_large` error, from config.yml `max_region.view_width`, default 256): the widest visible window a client may ask for, i.e. `dims − 2·view_margin` on x and on z. The region with its margins must still fit `max_dims` and `max_blocks`; the defaults (next version: `max_dims` 352×384×352, 25,165,824 blocks; were 288×384×288, 16,777,216) fit a 256-wide window with the headset's 48-block glide margin on x and z, 16 on y, and up to 171 blocks of window height. A headset grows its margin with the width (16 up to 96, 32 at 128–192, 48 at 256) within these limits; on a server with smaller limits it uses a smaller margin, never a smaller width.

**Clamping.** A `region_request` whose visible width on x or z is over `max_view_width` is not refused: on that axis the server narrows `dims` to `max_view_width + 2·view_margin` (the margin is first clamped as in §4.17), and moves `origin` by half the cut **rounded to whole sections**, so the window stays centred to within 8 blocks and the reply can still be a delta. The checks of §4.13 (`max_dims`, `max_blocks`, `max_bytes`) then apply to the narrowed region as before. The reply (`region_changed`, then the snapshot) carries the accepted `origin`, `dims` and `view_margin`; clients take the accepted width from it.

```json
{"v":1,"type":"region_request","seq":52,"t":1760370002600,"world":"world",
 "origin":[-176,32,-176],"dims":[352,128,352],"view_margin":[16,16,16]}
```

With `max_view_width` 256 this window of 320 is narrowed to 256: the server sets the region `origin` [-144, 32, -144], `dims` [288, 128, 288] (cut 64, origin +32 on x and z).

**Resize deltas** (§4.18): `base.shift` is new `origin` − old `origin` (multiples of 16), `dims` are the new ones. Example (`tools/fixtures/chunk_snapshot_grow.json`): against snapshot 8 of `chunk_snapshot_sections.json` (origin [0,60,0], dims [32,16,16]), snapshot 9 has origin [-16,60,0], dims [64,16,16], `shift` [-16,0,0] and lists sections [0,0,0] and [3,0,0]; sections 1 and 2 are copied from old sections 0 and 1. `chunk_snapshot_shrink.json` (origin [16,60,0], dims [16,16,16]) lists no section at all: everything is copied. A delta that leaves out a section reaching past the old region is a `bad_snapshot`.

Client guidance (non-normative; Quest `World/AreaSizing.cs`, `World/SectionPlan.cs`): keep the view where it is in the world when the resized region arrives; re-key the section meshes by `shift / 16` exactly as for a navigation shift, mesh only new or listed sections, and refresh kept sections that touch them or lost neighbours. Scale the block size by old width / new width so the miniature keeps its size on the table.

Fixtures and tests: `chunk_snapshot_grow.json`, `chunk_snapshot_shrink.json`, `region_request_resize.json`, and `max_view_width` in `welcome.json`, `region_info.json`, `error_region_too_large.json`. Java `SectionCodecTest` (resize deltas, fixtures rebuilt from snapshot 8) and `RegionRequestTest.viewWidthIsClampedAroundTheCentre`, Python `test_resize_deltas_grow_and_shrink` / `test_limits_carry_max_view_width`, C# `Tests~/AreaTests.cs`. Live: `tools/area_e2e.py` (each resize compared block by block, light included, with a fresh client's full snapshot) and `Tests~ --live-resize ws://…`.

## 4.26–4.28 Chat, commands and server status (next version; caps `chat_v1`, `command_v1`, `status_v1`)

The headset user is the server's operator without a game account: these let them talk with Java players, run server commands and watch the server. Each is a cap the client lists in `hello.caps`; the server grants it from config.yml (sections `chat`, `commands`, `status`) and echoes it in `welcome.caps`. Clients without a cap never get its messages. Docs: QUEST_SETUP.md Part L (the operator panel), ARCHITECTURE.md §3.9.

### 4.26 Chat (`chat_v1`)

Granted when `chat.enabled` (default true). `welcome` then carries:

```json
"chat":{"send":true,"name":"Nick","max_len":256}
```

- `send`: the headset may talk (`chat.send`, default true); otherwise it only reads.
- `name`: the name Java players see, from `hello.operator_name` (the Quest sends tabletop.json `operator_name`, else its `skin_player`), keeping `[A-Za-z0-9_]` and at most 16 characters; empty → config `chat.default-name`.

**`chat` (S→C)**, to every `chat_v1` session:

```json
{"v":1,"type":"chat","seq":40,"t":1760370005000,"kind":"chat","from":{"name":"Alex","uuid":"0f1e…","id":7},"text":"hi giant"}
{"v":1,"type":"chat","seq":41,"t":1760370005100,"kind":"death","from":{"name":"Alex","uuid":"0f1e…","id":7},"text":"Alex was slain by Zombie"}
{"v":1,"type":"chat","seq":42,"t":1760370005200,"kind":"operator","from":{"name":"Nick","session":"6f1c"},"text":"hello from the table","line":"[Giant] Nick: hello from the table"}
```

| `kind` | source | `text` |
|---|---|---|
| `chat` | a player's chat message (`AsyncChatEvent`, after other plugins changed it) | the message |
| `join`, `quit`, `death`, `advancement` | the game's join, quit, death and advancement lines (none when the game shows none, e.g. `show_death_messages` off) | the whole line, flattened to plain text in en_us (`Alex was slain by Zombie`, `Alex has made the advancement [Stone Age]`) |
| `broadcast` | a plugin's `Bukkit#broadcast` | the whole line |
| `say`, `me` | `/say` or `/me` by a player allowed to run it, or by the console (`from.name` `Server`); best effort: selectors in the text aren't expanded | the text after the command |
| `operator` | a headset's line (`chat_send`, or `say`/`me` from the console tab), echoed to every `chat_v1` session including the sender | the message; `line` is the whole line as Java players saw it |
| `whisper` | `/tabletop tell <message>` by a Java player (permission `tabletop.tell`, default everyone) or the console | the message |

`from` (optional): `name`, plus `uuid` and `id` (the entity id of `player_positions[].id`) for players, or `session` for a headset. Clients match players by `uuid`, never by name. Messages from `/msg`, `/tellraw` and similar fire no server event and aren't mirrored.

**`chat_send` (C→S)**:

```json
{"v":1,"type":"chat_send","seq":60,"t":1760370005150,"text":"hello from the table"}
```

- The server removes control characters and `§`, collapses whitespace, and refuses an empty text or one over `max_len` (`bad_message`). Without `chat_v1`, or with `chat.send` off: `error` `chat_denied`. At most `chat.max-per-second` (2) per session with a burst of `chat.burst` (5); more get `rate_limited`.
- Java players see config `chat.format` (MiniMessage, default `<gold>[Giant]</gold> <name>: <message>`) with `<name>` and `<message>` inserted as plain text (tags typed on the headset stay literal). The fixed prefix keeps a headset from passing for a real player. It is a server (system) message: not signed, and chat bridges that listen to player chat don't see it. The plugin logs `CHAT session=… <line>`.
- For `chat.bubble-seconds` (5; 0 = off) the line also floats above the giant's head as a text display for Java players. It carries the scoreboard tag `tabletop_avatar`, so `entities` (§4.21) never lists it.

### 4.27 Commands (`command_v1`)

Granted when `commands.enabled` (default true) and, with `commands.require-token` (default **true**), `auth.token` is set, so every connected headset presented the token. A headset command runs with operator rights. `welcome` then carries:

```json
"commands":{"quick":["time set day","weather clear","tick freeze","tick unfreeze"],"max_len":256}
```

`quick`: config `commands.quick`, shown as buttons. `max_len`: `commands.max-length`.

**`command` (C→S)**:

```json
{"v":1,"type":"command","seq":80,"t":1760370006000,"line":"summon minecraft:lightning_bolt ~ ~1 ~","at":{"world":"world","pos":[105,64,-35]}}
```

- `line`: one command, with or without a leading `/`, at most `max_len` characters, no control characters (else `bad_message`).
- `at` (optional): the block the headset points at (`world`, default the session's region world; `pos`, integer world block coordinates). The server runs `execute in <dimension> positioned <x+0.5> <y> <z+0.5> run <line>`, so `~ ~ ~` is that block, `~ ~1 ~` the cell on top of it, and `@p`, `@e[distance=..5]` are relative to it. Plugin commands work through it too. An unknown `world` gets `world_not_found`.
- Policy, checked before anything runs (`command_denied` with the reason): the root word, without `/` or a `namespace:` prefix and resolved through aliases, must not be in `commands.deny` (default `stop, restart, reload, op, deop, ban, ban-ip, pardon, pardon-ip, whitelist, save-off, debug, jfr, perf, transfer`), and must be in `commands.allow` when that list is non-empty. `execute … run <command>` and `return run <command>` are checked for every split after a `run` word (nested up to 8 deep), so `execute as @a run stop` is refused.
- At most `commands.max-per-second` (2) per session with a burst of `commands.burst` (5); more get `rate_limited`.
- `say` and `me` are not dispatched: they go out as the headset's chat (§4.26, `chat.format` / `chat.me-format`) and the result has `as_chat: true`.
- Ops with the permission `tabletop.notify` (default op) see `[Tabletop: /<line>]` in grey italics when `commands.notify-ops` is on. The plugin logs `CMD session=… /<line> ok|failed (<ms> ms)` (a warning over 50 ms) and `CMD_DENIED`.

**`command_result` (S→C)**, once per `command`:

```json
{"v":1,"type":"command_result","seq":90,"t":1760370006010,"ref_seq":80,"ok":true,"ms":0.42,"lines":["Summoned new Lightning Bolt"]}
```

- `lines`: the feedback the command sent to its sender, flattened to plain text (at most 200 lines of 1000 characters). Syntax errors come back as the game's message (`Incorrect argument for command at position 5: time <--[HERE]`), an unknown command as `Unknown command: /<root>`.
- `ok`: the command exists, ran, and none of its feedback was an error (vanilla shows those in red). `ms`: main-thread time of the dispatch. `as_chat`: see `say`/`me`.

**`command_output` (S→C)**: feedback that arrives after the result (asynchronous plugin commands), for 5 s, grouped per tick:

```json
{"v":1,"type":"command_output","seq":95,"t":1760370007200,"ref_seq":81,"lines":["Backup finished in 1.2 s"]}
```

**`command_complete` (C→S) → `command_suggestions` (S→C)**:

```json
{"v":1,"type":"command_complete","seq":82,"t":1760370006500,"line":"gamerule adv"}
{"v":1,"type":"command_suggestions","seq":96,"t":1760370006505,"ref_seq":82,"start":9,"items":["advance_time","advance_weather"]}
```

- From the server's own tab completion (`CommandMap#tabComplete` with operator rights; brigadier for vanilla commands). `items` (at most 50) replace the line from `start`: after its last space, or the command name (without a slash; `start` 1 when the line has one). Needs `command_v1` (else `command_denied`); at most `commands.completions-per-second` (10), and clients wait about 150 ms after the last keystroke before asking.

### 4.28 Server status (`status_v1`)

Granted when `status.enabled` (default true). Without it `world_query` and `players_query` get `unsupported`. Queries share the completion limit (`commands.completions-per-second`, 10 per session); more get `rate_limited`.

**`server_status` (S→C)**, every `status.every-ticks` (40 = 2 s), about 600 bytes:

```json
{"v":1,"type":"server_status","seq":98,"t":1760370008000,"tps":[20.0,19.98,20.0],"mspt":3.21,
 "memory":{"used_mb":812,"max_mb":2048},"players":{"online":1,"max":20},
 "tick":{"rate":20.0,"frozen":false,"stepping":false,"sprinting":false},
 "worlds":[{"name":"world","env":"normal","chunks":441,"entities":118,"players":1,"time":6000,"day":12,"weather":"clear"}]}
```

`tps`: the 1, 5 and 15 minute averages (capped at the tick rate). `mspt`: average tick time. `tick`: the server tick manager (`/tick`). Per world: loaded chunks, entities, players, `time` (time of day, 0..23999), `day` (full time / 24000), `weather` `clear`, `rain` or `thunder`.

**`world_query` (C→S) → `world_info` (S→C)**:

```json
{"v":1,"type":"world_query","seq":84,"t":1760370008100}
{"v":1,"type":"world_info","seq":99,"t":1760370008110,"ref_seq":84,"world":"world","env":"normal","time":6000,"day":12,"weather":"rain",
 "difficulty":"normal","spawn":[0,64,0],"tick":{"rate":20.0,"frozen":true,"stepping":false,"sprinting":false},
 "gamerules":{"advance_time":true,"advance_weather":true,"keep_inventory":false,"max_entity_cramming":24,"mob_griefing":true,"random_tick_speed":3}}
```

- `world` (optional in the query): default the session's region world; unknown → `world_not_found`.
- `gamerules`: every game rule of that world by the name the `gamerule` command takes (26.x: snake_case registry keys such as `advance_time`; a datapack's rules as `namespace:path`), booleans as JSON booleans and numbers as numbers. Clients build `gamerule <name> <value>` from these names and never hard-code them.

**`players_query` (C→S) → `players_info` (S→C)**: every online player, any world, sorted by name:

```json
{"v":1,"type":"players_info","seq":100,"t":1760370008210,"ref_seq":85,"players":[
 {"name":"Alex","uuid":"0f1e…","id":7,"world":"world","pos":[112.5,64.0,-30.25],"gamemode":"survival","health":18.5,"max_health":20.0,"food":17,"ping":23,"op":false}]}
```

Client guidance (non-normative; the Quest's operator panel): the World and Players tabs ask once a second while they are open, and their buttons are command templates on top of §4.27 (`time set day`, `weather thunder`, `summon minecraft:lightning_bolt ~ ~1 ~` at the pointer, `tick freeze`, `difficulty hard`, `gamerule <name> <value>`, `tp <name> ~ ~1 ~` at the pointer, `gamemode creative <name>`, `effect give <name> minecraft:instant_health 1 10`, `effect give <name> minecraft:saturation 1 10`, `kick <name> <reason>`). Without `command_v1` they are greyed out.

### 4.26–4.28 Fixtures and tests

- Fixtures: `hello_control.json` (the caps and `operator_name`), `welcome_control.json` (`chat`, `commands`), `chat.json`, `chat_death.json`, `chat_operator.json`, `chat_send.json`, `command.json`, `command_result.json`, `command_output.json`, `command_complete.json`, `command_suggestions.json`, `error_command_denied.json`, `server_status.json`, `world_query.json`, `world_info.json`, `players_query.json`, `players_info.json`. Java `ControlFixtureTest` builds them with the plugin's builders (`CommandPolicyTest`, `ChatTextTest` cover the policy and the text rules), Python `test_tabletop_protocol.py` validates them, C# `Tests~/ControlTests.cs` parses them.
- Live: `tools/control_check.py` (a headset plus a mineflayer Java player: caps, results, syntax errors, denied roots, the `at` pointer, completion, status, the chat round trip, the bubble, rate limits) and `Tests~ --live-control URL TOKEN` (the app's own classes).
