# PROTOCOL.md — Tabletop link protocol, v1.1.0

The project version (repo `VERSION` file) versions the plugin, the Quest app and this protocol together. From 1.0.0 the version follows semantic versioning: a plugin and an app with the same major version work together, new optional fields and messages come in minor versions, and a breaking change needs a new major version. (Before 1.0 there was no backwards compatibility: plugin, app and tools of the same 0.X version went together.) The wire field `v` stays `1`.

Status: **v1, normative.** The server plugin (`server-plugin/`), the Quest app (`quest-app/`) and the fake client (`tools/`) must all implement exactly this. Any breaking change bumps `PROTOCOL_VERSION` and is recorded in the changelog at the bottom.

## 1. Transport

- WebSocket (RFC 6455), plaintext `ws://` on LAN. Default endpoint: `ws://<server-host>:8765/tabletop` (port and path configurable in the plugin's `config.yml`).
- Every message is **one UTF-8 JSON text frame** holding one JSON object, with one exception: the server answers `asset_get` with a **binary frame** (an asset chunk, "Asset delivery"; v0.6.0). Clients send no binary frames; a server that gets one replies with `error` code `unsupported` and ignores it, and a client ignores binary frames it didn't ask for.
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
| `block_entities` | S→C | sign text, banner patterns, head skins, pot sherds, spawner mobs, beacon tiers, campfire/shelf/vault items, active conduits, as deltas (§4.29, needs `block_entities_v1`) |
| `interact` | C→S | use a block with an empty hand, or an item on a block's face ("Using blocks and items", needs `interact_v1`) |
| `item_registry` | S→C | the items the headset may use and the spawn eggs, once after `block_registry` ("Using blocks and items") |
| `entity_op` | C→S | hit, kill, remove, use an item on, name, move, carry, freeze or light up a mob or player, or ask about it ("Acting on mobs") |
| `entity_info` | S→C | what the server knows about one entity, the reply to `entity_op` `info` ("Acting on mobs") |
| `undo` / `redo` | C→S | take back the newest edit group, or put back the newest undone one ("Undo and redo", needs `power_v1`) |
| `history` | S→C | how many edit groups (and blocks) can be undone and redone ("Undo and redo") |
| `box_edit` | C→S | fill, replace, clear, walls, hollow or clone a box of blocks ("Box edits", needs `power_v1`) |
| `box_progress` | S→C | a box edit, undo or redo running, then its result ("Box edits") |
| `brush` | C→S | one stroke of a sphere or cylinder brush: paint, replace, erase, smooth ("Brushes", needs `power_v1`) |
| `world_state` | S→C | the region's world's time of day, rain and thunder, dimension: on change and about once a second ("World state: time and weather", needs `world_v1`) |
| `bookmark` / `bookmarks` | C→S / S→C | add or remove a shared bookmark; every bookmark, after `welcome` and on each change ("Marks: bookmarks, spawn and worlds", needs `marks_v1`) |
| `container_query` / `container` | C→S / S→C | open a container's view; its slots, re-sent while they change, or the view's end ("Containers", needs `interact_v1`) |
| `container_edit` / `container_close` | C→S | set one slot or sort the container; stop watching it ("Containers") |
| `sign_edit` | C→S | one side of a sign: four lines, dye colour, glowing ink ("Signs", needs `interact_v1`) |
| `sound_groups` | S→C | every block's break, step, place, hit and fall sounds, once after the registries ("Sound", needs `sound_v1`) |
| `assets` / `asset_get` | S→C / C→S | the operator's own pack.zip, entity_models.json and sounds.zip on offer; a chunk of one, answered by a binary frame ("Asset delivery", needs `assets_v1`) |
| `pair_request` / `pair_code` / `paired` | C→S / S→C / S→C | ask for a pairing code; the code to show; the device token once an operator typed the code ("Pairing", needs `pair_v1`; 1.1.0) |
| `report` / `report_ack` | C→S / S→C | report a player to the server's operators; the report's id once it is saved ("Chat safety: mute, block, report", needs `report_v1`; next version, 1.2.0) |

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
- `caps`: capability strings. v1 defines `rle_palette_v1` (required) and `avatar` (client will send poses). Optional extras: `light_v1` and `biomes_v1` (§4.5.1), `edit_v1` (§4.20), `entities_v1` (§4.21), `actions_v1` (§4.22), `chat_v1`, `command_v1` and `status_v1` (§4.26–4.28), `block_entities_v1` (§4.29), `interact_v1` ("Using blocks and items"), `world_v1` ("World state: time and weather"), `marks_v1` ("Marks: bookmarks, spawn and worlds"), `power_v1` ("Undo, box edits and brushes"), `report_v1` ("Chat safety: mute, block, report"; 1.2.0); unknown caps are ignored.
- `operator_name` (optional string): the name the headset wants in game chat (§4.26). The server keeps `[A-Za-z0-9_]`, at most 16 characters.
- Since 1.1.0, with the cap `pair_v1`: `device_token` (optional string), the token this server gave the headset when it was paired, and `device_name` (optional string, e.g. "Quest 3"). A valid `device_token` counts like the shared `token`; on a server that needs a credential, a `pair_v1` client without one is shown a code instead of being refused ("Pairing").
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
- Since 1.1.0: `server_id` (string, 16 hex digits), a random id the server keeps; and for `pair_v1` sessions `pairing` (`paired`, `device`, `name`, `required`, `token_rejected`; "Pairing").

### 4.3 `subscribe` (C→S)

```json
{"v":1,"type":"subscribe","seq":1,"t":1760370000100,
 "world":"world","origin":[100,60,-40],"dims":[32,64,32]}
```

- `origin`: int[3], world block coords of the region's min corner.
- `dims`: int[3] `[dx,dy,dz]`, each ≥ 1 and ≤ `limits.max_dims`. Y is clamped to the world's min/max build height; a clamp sends back the actual values in the next `chunk_snapshot`.
- Optional `follow`: player name string. The server answers `error` `unsupported`: headsets follow a player themselves with `region_query` and `region_request` ("Following a player"; v0.5.0, the field was never implemented).
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

Sent at `limits.player_hz` (default 10 Hz) whenever there's at least one player in the region bounds expanded by `stream.players-margin` (16) blocks on every axis, plus once with an empty list when the last one leaves. Since v0.3.1 the margin applies to Y too (a player deep in a cave under the region isn't listed), and spectators, invisible players (the flag or the potion) and vanished players (a true `vanished` metadata value, as vanish plugins set, or hidden from any other online player) are never listed.

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
- Since v0.3.1 the client SHOULD treat 6 s without any inbound bytes as a dead link (Wi-Fi gone without a TCP FIN, a frozen server), drop the socket and reconnect. The Quest app does, and clears its RTT while the link is down. A big snapshot in transit counts as traffic.

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
| `edit_denied` | no | a `block_edit` was refused: no `edit_v1`, occupied cell, denied state (§4.20); an `undo`, `redo`, `box_edit` or `brush` refused (no `power_v1`, nothing to undo, a job still running, over a limit; "Undo, box edits and brushes") |
| `out_of_region` | no | a `block_edit`, `interact`, `entity_op`, `container_query`, `container_edit`, `sign_edit`, `box_edit` or `brush` outside the session's region (§4.20, "Using blocks and items", "Containers", "Signs", "Box edits") |
| `bad_state` | no | a `block_edit` state that isn't a placeable block state (§4.20) |
| `chat_denied` | no | a `chat_send` without `chat_v1`, or `chat.send` is off (§4.26) |
| `command_denied` | no | a `command` or `command_complete` without `command_v1`, or a command the server's policy refuses (§4.27) |
| `interact_denied` | no | an `interact` or `entity_op` without `interact_v1`, with a family switched off, or that does nothing there ("Using blocks and items"); a `container_query`, `container_edit` or `sign_edit` that isn't allowed or valid there ("Containers", "Signs") |
| `not_found` | no | an `entity_op` whose entity is gone (died, despawned, unloaded) ("Acting on mobs") |
| `mark_denied` | no | a `bookmark` without `marks_v1`, on a read-only server (`marks.edit`), over `marks.max`, or removing a name that doesn't exist ("Marks: bookmarks, spawn and worlds") |
| `asset_denied` | no | an `asset_get` without `assets_v1` (nothing to serve, an untrusted session, not from the LAN, or not asked for) ("Asset delivery"); `not_found` also answers an `asset_get` for a name that isn't offered |
| `pair_denied` | no (yes while waiting for pairing) | a `pair_request` refused: pairing off, already paired, too many requests from this address or too many headsets waiting ("Pairing"; 1.1.0) |
| `report_denied` | no | a `report` without `report_v1`: the client didn't ask for it, or `reports.enabled` is off ("Chat safety: mute, block, report"; 1.2.0) |

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

A client SHOULD NOT reconnect by itself after 4001 or 4003: nothing changes until the app, the plugin, the token or the address does. The Quest app stops retrying and shows its setup card (QUEST_SETUP.md Part D); it reconnects with backoff after every other close. Since 1.1.0: after a 4003 to a `hello` that carried a `device_token` (the headset was unpaired), the client drops that token and reconnects without it, which shows a new code ("Pairing").

## 6. Implementation notes (non-normative)

1. **Encoder reference (Java):** walk `i = 0 … N−1` in yzx order, look up `ChunkSnapshot.getBlockData(x&15, y, z&15).getAsString()`, map it to a palette index with a `HashMap<String,Integer>` (cache by `BlockData` identity), and emit a pair whenever the index changes.
2. **Decoder reference (C#/Python):** expand the pairs into a `ushort[]` of length N and check the total.
3. **Size check:** a typical 32x64x32 band (65,536 blocks), mostly air above and stone below, has about 500–3,000 runs, so 5–30 KB of JSON. A worst-case noisy 32x384x32 region (393,216 blocks, all runs of 1) is about 2.5 MB of JSON, which is still under the 4 MiB cap. See RESEARCH.md §2.
4. **Conformance:** `tools/` ships golden JSON fixtures for every message type. Both the Java unit tests and the fake client round-trip them.

## 7. LAN discovery (UDP, outside the WebSocket; v0.3.1)

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
- Since 1.1.0: the reply also carries `id` (the server's `server_id`, so a headset recognises a paired server whose address changed) and `pair` (true when it pairs headsets: no token to type even if `token` is true). Fixture `discovery_reply_pair.json`.
- Fixtures: `tools/fixtures/discovery_probe.json`, `discovery_reply.json` (Java `LanBeaconTest`, Python `test_discovery_*`, C# `Tests~/ComfortTests.cs`).

## Changelog

- **v1 (2026-10-13):** initial.
- **v1, light and biomes (2026-10-13, additive):** optional per-block light and 4x4x4 biomes in `chunk_snapshot`, light changes in `block_update`, opted into with the `light_v1` / `biomes_v1` caps and echoed in `welcome.caps` (§4.5.1). Peers without the caps are unaffected.
- **v1, block editing (2026-10-13, additive):** `block_edit` (C→S) breaks or places one block inside the region, opted into with the `edit_v1` cap; error codes `edit_denied`, `out_of_region`, `bad_state`; an error about an edit carries its `op` and `pos` (§4.20). Peers without the cap are unaffected.
- **v0.2.0: block menu (2026-10-14, required, not backwards compatible):** the server sends `block_registry` (§4.24) to every client right after `welcome`, and a `block_edit` `place` must name a registry block or variant (else `bad_state`, §4.20). No cap: plugin, Quest app and tools must match.
- **v0.3.0: visible width presets (§4.25).** `limits.max_view_width` (required in `welcome.limits`, `region_info.limits` and `region_too_large`); a `region_request` whose visible width is over it is narrowed around its centre instead of refused; `sections_v1` deltas may change `dims` (a section-aligned resize, §4.18). Fixtures `chunk_snapshot_grow.json`, `chunk_snapshot_shrink.json`, `region_request_resize.json`.
- **v0.3.1: sessions and discovery.** `region_changed` carries `limits` like `region_info` (with the new world's `build_height`, §4.13); LAN discovery over UDP (§7); `player_positions` filters on Y and leaves out spectators, invisible and vanished players (§4.6); clients drop a link after 6 s of silence (§4.10) and don't retry after 4001/4003 (§5). Fixtures `region_changed.json` (now with `limits`), `discovery_probe.json`, `discovery_reply.json`.
- **v0.3.1: building like vanilla (§4.20).** No new fields. `block_edit` `pos` may be the clicked block itself when it is replaceable; the server places doors, beds and tall plants with both halves (and removes both on a break), checks vanilla replaceability and survival, refuses blocks with a collision box in a player or mob and edits outside the world border, makes placed leaves persistent, sets `waterlogged` from the cell, computes connection properties from the neighbours, and names blocks readably in refusals ("stone is in the way"; fixture `error_edit_denied.json`).
- **v0.3.1: armour stands (§4.21).** `entities` entries carry `arms`, `base_plate` and `stand_pose` for armour stands; fixture `entities_armor_stand.json` (Java `EntityDeltaTest`, Python, C# `EntityTests`).
- **v0.4.0: faster gliding (§4.17, §4.25), no field changes.** A glide `region_request` may move the origin by several whole sections at once; the default `max_region` is 352×384×352 and 25,165,824 blocks (a config still holding the 0.3.0 defaults is raised on start); a server reads only the sections of a moved or resized region that weren't in the old one (the delta lists exactly those). Fixture `region_changed.json` carries the new default limits.
- **v0.4.0: chat, commands and server status (§4.26–4.28).** New caps `chat_v1`, `command_v1` (needs `auth.token` by default) and `status_v1`; messages `chat`, `chat_send`, `command`, `command_result`, `command_output`, `command_complete`, `command_suggestions`, `server_status`, `world_query`, `world_info`, `players_query`, `players_info`; `hello.operator_name`; `welcome.chat` and `welcome.commands`; errors `chat_denied`, `command_denied`. Fixtures `hello_control.json`, `welcome_control.json`, `chat*.json`, `command*.json`, `error_command_denied.json`, `server_status.json`, `world_query.json`, `world_info.json`, `players_query.json`, `players_info.json`.
- **v0.4.0: block entities (§4.29).** New cap `block_entities_v1` and message `block_entities`: sign text, banner patterns, player head profiles, pot sherds, spawner mobs, beacon tiers, campfire, shelf and vault items and active conduits, as per-chunk deltas with `removed`, and `reset` on a session's first region and after a world change; config.yml `block-entities`. Fixtures `block_entities.json`, `block_entities_reset.json`.
- **v0.5.0: operator tools ("Operator tools": "Worlds and spawn", "Following a player", "Marks: bookmarks, spawn and worlds", "Containers", "Signs").** `welcome.worlds` and `region_info.worlds`/`spawn`; new cap `marks_v1` (config.yml `marks`) with `bookmark` and `bookmarks` (after `welcome` and on each change) and `welcome.marks`; error `mark_denied`; with `interact_v1`: `container_query`, `container`, `container_edit`, `container_close`, `sign_edit`, and `welcome.interact.containers`/`signs` (config.yml `interact.containers`, `interact.signs`, `interact.sign-event`). An `interact` on a container or sign is still refused (`interact_denied`). `subscribe.follow` is answered `unsupported`: headsets follow players themselves. Fixtures listed under "Operator tools: Fixtures and tests".
- **v0.5.0: using blocks, items and mobs ("Using blocks and items", "Acting on mobs").** New cap `interact_v1` (config.yml `interact`); messages `interact`, `item_registry` (after `block_registry`, `interact_v1` sessions only), `entity_op`, `entity_info`; `welcome.interact` (the switches); errors `interact_denied`, `not_found`; `out_of_region` also for `interact` and `entity_op`. Errors about an `interact` carry `op: "interact"` and `pos`, errors about an `entity_op` carry `op: "entity_op"`, `entity_op` and `uuid`. Fixtures `hello_interact.json`, `welcome_interact.json`, `interact.json`, `interact_item.json`, `item_registry.json`, `entity_op*.json`, `entity_info.json`, `error_interact_denied.json`, `error_not_found.json`.
- **v0.5.0: undo, box edits and brushes ("Undo, box edits and brushes").** New cap `power_v1` (with `edit_v1`; config.yml `edit.power`, `edit.undo-depth`, `edit.box-max-blocks`, `edit.box-budget-ms`, `edit.brush-...`); `block_edit`, `interact`, `container_edit` and `sign_edit` gain an optional `group` (container and sign edits are journalled too); messages `undo`, `redo`, `history`, `box_edit`, `box_progress`, `brush`; `welcome.limits` gains `undo_depth`, `box_max_blocks`, `brush_max_radius`, `brush_blocks_per_second` for `power_v1` sessions. Errors about them carry `op` (`undo`, `redo`, `box_edit` with `box_op`, `brush` with `brush_op`) and `group`. Fixtures `hello_power.json`, `welcome_power.json`, `block_edit_group.json`, `interact_group.json`, `undo.json`, `redo.json`, `history.json`, `box_edit_fill.json`, `box_edit_replace.json`, `box_edit_clone.json`, `box_progress.json`, `box_progress_done.json`, `brush.json`, `brush_replace.json`, `error_box_denied.json`.
- **v0.5.0: a living world ("World state: time and weather", "Living entities").** New cap `world_v1` and message `world_state` (time, day time, rain, thunder, dimension, sky light; on change and about once a second; config.yml `world-state`). `entities` entries gain `leash` (the holder's id), `eye`, `chested`, `name_visible`, `xp`, `owner`, `carried`, `peek`, `attach` and `display` (display entity transformation, billboard, brightness, item context, text); text displays are listed; a snow golem without its pumpkin is `sheared`; a cushion has its `color`. Fixtures `world_state.json`, `entities_living.json`.
- **v0.5.1: no protocol change.** Faster section meshing (same output), adaptive mesh jobs and the optional surface skirt are client-side (QUEST_SETUP.md Part H: `surface_skirt`, `skirt_speed`, `mesh_workers`).
- **v0.6.0: sound and asset delivery ("Sound", "Asset delivery").** New cap `sound_v1` (config.yml `sound.enabled`): message `sound_groups` (every block's sound group, once after the registries) and the `actions` kinds `sound` and `sound_stop` (only to `sound_v1` sessions). New cap `assets_v1` (config.yml `assets`, off by default, needs `auth.token`, LAN addresses only): messages `assets` and `asset_get`, the first binary frame of the protocol (an asset chunk, §1), error `asset_denied` (`not_found` and errors about an `asset_get` carry `op: "asset_get"` and `name`). Fixtures `hello_sound.json`, `welcome_sound.json`, `sound_groups.json`, `actions_sound.json`, `assets.json`, `asset_get.json`, `error_asset_denied.json`, `asset_chunk.bin`.
- **v0.7.0: block animations ("Block animations").** The `actions` kinds `lid` (a container lid opens or closes), `bell` (a bell rung, with the side) and `explode` (an explosion's point and power) for `actions_v1` sessions; `entities` entries gain `fuse` (primed TNT, ticks left) and `swell` (a creeper, 0..1), and the poses `dying`, `sitting` and `sleeping` are now drawn. No new cap. Fixtures `actions_motion.json`, `entities_motion.json` (Java `MotionTest`, Python `test_motion_messages`, C# `MotionTests`). Client-only: flowing fluids and ambient particles (QUEST_SETUP.md Part E, tabletop.json `particles`).
- **v0.8.0: no message changes.** Bedrock players joining through Geyser/Floodgate are ordinary players here (names may start with `.`); the plugin now re-sends a player's `skin` when it changes, as §4.6 always said (Floodgate applies Bedrock skins after the join).
- **v0.8.1: no message changes.** When one headset has several `region_request`s in flight, the server applies only the newest one's read (an older one finishing later is dropped and logged `REGION_SUPERSEDED`), so a slow link can't flip the area back to an older size or place.
- **v1.0.0: first stable release, no message changes since v0.8.1.** From here the version is semantic: plugins and apps with the same major version work together, additions come in minor versions, breaking changes need 2.0.0.
- **v1.1.0: pairing ("Pairing").** Additive: cap `pair_v1`; `hello.device_token` and `device_name`; `welcome.server_id` (every client) and `welcome.pairing`; messages `pair_request`, `pair_code`, `paired`; error `pair_denied`; a session state "waiting for pairing" on servers that need a credential (`pair_v1` clients only; others still get 4003); the discovery reply's `id` and `pair`; the require-token switches mean "trusted session" (shared token or paired headset); a 4003 to a hello with a `device_token` makes the client drop the token and reconnect. Fixtures `hello_pair.json`, `welcome_pair.json`, `pair_request.json`, `pair_code.json`, `paired.json`, `error_pair_denied.json`, `discovery_reply_pair.json`.
- **Next version (1.2.0): chat safety ("Chat safety: mute, block, report").** Additive: cap `report_v1` (config.yml `reports`); messages `report` (C→S) and `report_ack` (S→C); error `report_denied`; errors about a report carry `op: "report"`. Mute and block are client-side (no messages). Old peers: a 1.1.x plugin doesn't grant `report_v1` (the app greys Report, mute and block still work); a 1.1.x app never asks for it. Fixtures `hello_report.json`, `welcome_report.json`, `report.json`, `report_ack.json`, `error_report_denied.json`.
- **v1.1.0: automatic game files ("Asset delivery").** Additive: asset delivery is on by default for trusted sessions (paired headsets or the `auth.token`) on the LAN, with files the plugin builds from the server PC's own Minecraft install (config.yml `assets.auto`, `assets.minecraft-dir`); the `assets` offer gains optional `state` and `note`. Fixture `assets_building.json` (Java `GameAssetsTest`, Python `test_assets_offer_state_and_note`, C# `SetupTests`).

## 4.13–4.16 Runtime region control (v1, additive)

- `region_request` (client→server): `{"origin":[x,y,z],"dims":[dx,dy,dz]}` or `{"center":[x,y,z],"size":[dx,dy,dz],"y_origin"?:y}` (origin = center − size/2). The server checks it against `max_region` in config.yml (x, y, z, blocks, bytes). Over the cap it returns `error{code:"region_too_large", max_dims, max_blocks, max_bytes}`.
- `region_changed` (server→all shared sessions): `{world, origin, dims, by_session, ref_seq}`, followed by a full `chunk_snapshot`. When several clients write, the last request wins. Since v0.3.1 it also carries `view_margin` (§4.17) and `limits` as in `region_info` (`max_dims`, `max_blocks`, `max_bytes`, `max_view_width` and the region's world's `build_height`), so a client that moved to another world (area setup centred on a player in the Nether, say) glides within the right height at once.
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

Navigation (Quest app, docs/QUEST_SETUP.md "Moving around"): when the window has moved more than a few blocks past the region centre in the direction of motion, the client sends `region_request` with the same `dims` and `view_margin` and the `origin` moved by 16 blocks (one section) on that axis; while it moves steadily one way it may move the origin by several sections at once, so that the margin ahead of the window is runway (v0.4.0; QUEST_SETUP.md Part H). Origins always move by whole 16-block sections, so the client can keep its section meshes and the server can send a delta (4.18). One request is in flight at a time. The region is shared (4.13), so other headsets follow; the last writer wins.

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

- **Which entities:** every entity whose bounding box touches the region box grown by `entities.margin` (default 2) blocks on each side, except players (they're in `player_positions`), the giant's own entities (scoreboard tag `tabletop_avatar`), markers, interactions and area effect clouds (text displays are sent since v0.5.0, "Living entities"). At most `entities.max` (default 256); past that, the ones nearest the region centre win.
- **Deltas:** the server remembers what it sent each session. An entry for an `id` the client doesn't hold yet is a **full entry** (every field that applies; false/null/empty ones are left out). An entry for a known `id` lists only what changed: `pos` when it moved by at least 0.01 block on an axis, angles when they turned by at least 1°, any other field when it differs. A field that went away is sent as `false`, `null` or `[]`. `removed` lists ids that left the region or despawned. With `full: true` the list replaces the client's whole set: entities it doesn't name are gone, and its entries are full entries. The server sends one after every region change. A client that gets a delta for an `id` it doesn't hold ignores it.
- **Fields** (all optional except `id`; a full entry always has `type` and `pos`):
  - `id` (int): the server's entity id, unique while the entity exists. `uuid` (string, full entries).
  - `type`: entity type key: `minecraft:cow`, `minecraft:oak_boat` (26.x has one type per boat wood), `minecraft:item`...
  - `pos`: world coordinates (2 decimals) of the entity's position: the feet or bottom centre, or for a hanging entity the centre of it on its wall.
  - `yaw`, `pitch`: degrees, §3 convention. For living entities they are the **head**'s, and `body_yaw` is the body's. Other entities have no `body_yaw` (their yaw is the body's).
  - `on_ground` (bool), `vehicle` (id of what it rides), `passengers` (ids in seat order, players included; see `player_positions[].id`).
  - `size`: `[width, height]` of the bounding box in blocks. Clients draw a box of this size for types they don't know, and slimes scale with it.
  - Looks: `baby`, `small` (armour stands), `sheared`, `tamed`, `angry`, `invisible` (bools). `variant`: the registry key path for vanilla ids (`temperate`, `pale`, `calico`, `desert`, `kebab`), `namespace:path` otherwise; enum values in lower case (horse colour `dark_brown`, rabbit `salt_and_pepper`, panda gene, axolotl, parrot, fox, llama colour, salmon size, pufferfish `small`/`medium`/`big`, tropical fish pattern). `color`: a dye colour (sheep wool, shulker, tropical fish body, a tamed wolf's or cat's collar, a cushion). `markings`: horse markings (`white`, `whitefield`, `white_dots`, `black_dots`) or the tropical fish pattern colour. `profession`: villagers and zombie villagers; missing for none. `pose`: as for players, plus `sitting`; missing = standing. `name`: the custom name as plain text.
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
- `lid`, `bell`, `explode` (v0.7.0): container lids, bells and explosions, "Block animations"; `sound`, `sound_stop`: "Sound".
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

**`limits.max_view_width`** (int, required in `welcome.limits`, `region_info.limits` and the `region_too_large` error, from config.yml `max_region.view_width`, default 256): the widest visible window a client may ask for, i.e. `dims − 2·view_margin` on x and on z. The region with its margins must still fit `max_dims` and `max_blocks`; the defaults (v0.4.0: `max_dims` 352×384×352, 25,165,824 blocks; were 288×384×288, 16,777,216) fit a 256-wide window with the headset's 48-block glide margin on x and z, 16 on y, and up to 171 blocks of window height. A headset grows its margin with the width (16 up to 96, 32 at 128–192, 48 at 256) within these limits; on a server with smaller limits it uses a smaller margin, never a smaller width.

**Clamping.** A `region_request` whose visible width on x or z is over `max_view_width` is not refused: on that axis the server narrows `dims` to `max_view_width + 2·view_margin` (the margin is first clamped as in §4.17), and moves `origin` by half the cut **rounded to whole sections**, so the window stays centred to within 8 blocks and the reply can still be a delta. The checks of §4.13 (`max_dims`, `max_blocks`, `max_bytes`) then apply to the narrowed region as before. The reply (`region_changed`, then the snapshot) carries the accepted `origin`, `dims` and `view_margin`; clients take the accepted width from it.

```json
{"v":1,"type":"region_request","seq":52,"t":1760370002600,"world":"world",
 "origin":[-176,32,-176],"dims":[352,128,352],"view_margin":[16,16,16]}
```

With `max_view_width` 256 this window of 320 is narrowed to 256: the server sets the region `origin` [-144, 32, -144], `dims` [288, 128, 288] (cut 64, origin +32 on x and z).

**Resize deltas** (§4.18): `base.shift` is new `origin` − old `origin` (multiples of 16), `dims` are the new ones. Example (`tools/fixtures/chunk_snapshot_grow.json`): against snapshot 8 of `chunk_snapshot_sections.json` (origin [0,60,0], dims [32,16,16]), snapshot 9 has origin [-16,60,0], dims [64,16,16], `shift` [-16,0,0] and lists sections [0,0,0] and [3,0,0]; sections 1 and 2 are copied from old sections 0 and 1. `chunk_snapshot_shrink.json` (origin [16,60,0], dims [16,16,16]) lists no section at all: everything is copied. A delta that leaves out a section reaching past the old region is a `bad_snapshot`.

Client guidance (non-normative; Quest `World/AreaSizing.cs`, `World/SectionPlan.cs`): keep the view where it is in the world when the resized region arrives; re-key the section meshes by `shift / 16` exactly as for a navigation shift, mesh only new or listed sections, and refresh kept sections that touch them or lost neighbours. Scale the block size by old width / new width so the miniature keeps its size on the table.

Fixtures and tests: `chunk_snapshot_grow.json`, `chunk_snapshot_shrink.json`, `region_request_resize.json`, and `max_view_width` in `welcome.json`, `region_info.json`, `error_region_too_large.json`. Java `SectionCodecTest` (resize deltas, fixtures rebuilt from snapshot 8) and `RegionRequestTest.viewWidthIsClampedAroundTheCentre`, Python `test_resize_deltas_grow_and_shrink` / `test_limits_carry_max_view_width`, C# `Tests~/AreaTests.cs`. Live: `tools/area_e2e.py` (each resize compared block by block, light included, with a fresh client's full snapshot) and `Tests~ --live-resize ws://…`.

## 4.26–4.28 Chat, commands and server status (v0.4.0; caps `chat_v1`, `command_v1`, `status_v1`)

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

## 4.29 Block entities (v0.4.0; cap `block_entities_v1`)

What a block state doesn't say about a block entity: sign text, banner patterns, player head profiles, pot sherds, the mob in a spawner, a beacon's tier, the items on a campfire, shelf or vault, and whether a conduit is active. Everything else is drawn from the block state alone: chests (ender, trapped and copper ones too), shulker boxes, bells, mob heads, plain banners, end portals and gateways, copper golem statues, the books of lecterns (`has_book`) and enchanting tables. Granted when config.yml `block-entities.enabled` (default true) and the client lists the cap; echoed in `welcome.caps`. Clients without it never get `block_entities`. Docs: ARCHITECTURE.md §3.10 and §4 (Block entities).

**`block_entities` (S→C)**:

```json
{"v":1,"type":"block_entities","seq":14,"t":1760370001200,
 "entities":[
  {"pos":[106,64,-35],"kind":"sign","front":{"lines":["","Shop","",""],"color":"light_blue","glowing":true},"back":{"lines":["Closed","","",""],"color":"red","glowing":false}},
  {"pos":[107,65,-35],"kind":"banner","patterns":[{"pattern":"minecraft:stripe_bottom","color":"red"},{"pattern":"minecraft:creeper","color":"black"}]}],
 "removed":[[100,64,-35]]}
```

- `entities`: new or changed entries, each complete: it replaces whatever the client holds at its `pos` (integer world block coordinates).
- `removed`: world positions whose entry is gone (the block was broken, or has nothing to send any more, such as a sign whose text was erased).
- `reset` (optional, `true`): forget every entry held, then apply the lists. The server sends it, even with both lists empty, in the first `block_entities` of a session and in the first after the session's region moves to another world. Fixture `block_entities_reset.json`.

Entries by `kind`. An entry exists only when it has something to say:

| `kind` | blocks | fields |
|---|---|---|
| `sign` | standing, wall and hanging signs | `front`, `back`: `lines` (4 strings of plain text; styles, click events and per-line colours are dropped), `color` (the dye, default `black`), `glowing`. Not sent when both sides are empty. |
| `banner` | standing and wall banners | `patterns`: the layers in order (at most 16), each `{pattern, color}` with `pattern` a banner pattern registry key. Not sent without patterns (the base colour is in the block id). |
| `skull` | player heads (standing and wall) | the profile: `skin` (a textures.minecraft.net URL), `name`, `uuid`, each optional. For a head placed with only a name or UUID the server looks the skin up off the main thread (at most 4 at a time) and adds `skin` once it has it. Other heads send nothing. |
| `decorated_pot` | decorated pots | `sherds`: 4 item keys for the back, left, right and front sides, `minecraft:brick` for a plain side. Not sent when all four are brick. |
| `spawner`, `trial_spawner` | spawners, trial spawners | `entity` (entity type key), `size` (optional [width, height] of that mob in blocks, for scaling big mobs down as vanilla does), `ominous` (trial spawners only). Not sent when nothing is set to spawn. |
| `beacon` | beacons | `tier` 0..4, the pyramid levels; 0 draws no beam. Always sent. |
| `campfire` | campfires, soul campfires | `items`: 4 slots, each an item key or null. Not sent when empty. |
| `shelf` | shelves | `items`: 3 slots, each an item key or null. Not sent when empty. |
| `vault` | vaults | `item`: the item the vault shows. Not sent when it shows none. |
| `conduit` | conduits | `active: true`. An inactive conduit sends nothing: it is the closed shell. |

**Sending.** The server reads block entities on the main thread, chunk by chunk, within `block-entities.budget-ms` (default 0.3 ms) per tick: first the chunks new to a region and the chunks with block changes, sign edits or right clicks, then a round robin. Chunks with campfires, shelves, vaults, beacons, spawners or conduits are read every `block-entities.rescan-ticks` (40); the others every 8× as long, which still catches `/data` edits. Each session gets the difference between a chunk's entries and what it already holds, at most one message per tick. When the region moves within the same world, the entries outside the new region are dropped by both sides without `removed`: the client drops them when it applies the new region's snapshot. A message may arrive before or after the snapshot of a new region, so clients keep entries by world position, outside the current region too until that snapshot.

Client guidance (non-normative; Quest `World/BlockEntityRenderer.cs`): the terrain mesher leaves out the blocks vanilla draws entirely in code (chests, shulker boxes, heads, banners, bell bodies, pots, conduits, statues, end portals and gateways), and the block entity renderer draws them from the block state plus these entries, with the entity model layers in the user's `entity_models.json` (QUEST_SETUP.md Part E) and the pack's textures. Without that file it draws plain boxes.

Fixtures and tests: `block_entities.json` (one entry of each kind and a removal), `block_entities_reset.json`. Java `BlockEntityJsonTest` and `FixtureTest` build them with the plugin's builders (plus per-chunk deltas and pruning), Python `test_tabletop_protocol.py` validates and mirrors them (`BlockEntityMirror`), C# `Tests~/BlockEntityTests.cs` applies them. Live: `tools/block_entities_check.py`.

## Using blocks and items (v0.5.0; cap `interact_v1`)

The headset uses blocks and items like a creative player's right click: it opens doors, flips levers, presses buttons, tunes note blocks, uses bone meal, buckets, flint and steel, axes, hoes, shovels, shears, spawn eggs and a lightning wand. Granted when config.yml `interact.enabled` (default true) and, with `interact.require-token`, `auth.token` is set; echoed in `welcome.caps`. `welcome` then carries the switches the client needs to colour its outline and fill its menus:

```json
"interact":{"iron_doors":true,"fire":true,"tnt":true,"fluids":true,"lightning":"real","entities":true,"harm_players":false,"attack_damage":5.0,"max_spawns_per_minute":60}
```

`lightning`: `real`, `effect` (flash and sound only) or `off`. The others are the config switches of the same names. Docs: QUEST_SETUP.md Part M (the controls), ARCHITECTURE.md §3.11.

### `interact` (C→S)

```json
{"v":1,"type":"interact","seq":90,"t":1760370004000,"pos":[105,64,-35],"face":"north","hit":[0.5,0.7,0.0],"item":null,"sneak":false,"dir":[0.1,-0.5,0.86]}
{"v":1,"type":"interact","seq":91,"t":1760370004100,"pos":[105,63,-35],"face":"up","item":"minecraft:bone_meal","sneak":false,"dir":[0.0,-0.9,0.44]}
```

| field | type | req | meaning |
|---|---|---|---|
| `pos` | int[3] | yes | the clicked block, world coordinates; must be inside the session's region (`out_of_region`) |
| `face` | string | yes | the clicked face (`west`, `east`, `down`, `up`, `north`, `south`) |
| `item` | string or null | no | null or missing: an empty hand uses the block. An item id: that item is used on the face; it must be in `item_registry` (any axe, hoe or shovel stands for its tool class) |
| `hit` | number[3] | no | where the ray met the block, 0..1 inside it |
| `sneak` | bool | no | informational: the client chose sneak (it then sends `item` or a `block_edit` instead of an empty-hand use) |
| `dir` | number[3] | no | the ray direction, world axes (a fence gate swings away from it; a pumpkin carved from above faces back along it) |

There is no acknowledgement and nothing is guessed on the client: results arrive as `block_update`, `entities` and `actions` (typically 100–200 ms). At most `interact.max-per-second` (10) `interact` and `entity_op` together per session; more get `rate_limited`. Protection plugins are not consulted, as for `block_edit`; changes outside the region are refused, but fire and lava can spread beyond it.

**Empty hand** (the server's `InteractRules`, families by block id; the client mirrors them to colour its outline):

| block | what happens |
|---|---|
| doors, trapdoors, fence gates | open or close, both halves of a door; iron doors and trapdoors only with `interact.iron-doors` (default true); a gate opened from the side it faces turns to swing away; the door's own sound (wooden, cherry, bamboo, nether wood, copper, iron) |
| lever | flips; the block it hangs on also updates its neighbours, so redstone behind a wall sees the power (vanilla does this; see ARCHITECTURE.md §3.11) |
| buttons | pressed, and released by the block's own tick after 20 ticks (stone, polished blackstone) or 30 (wood) |
| note block, repeater, comparator, daylight detector | next note (and the note plays when the block above is air), next delay, compare/subtract (then the comparator refreshes its output), inverted |
| bell, cake, candle cake, composter, jukebox | rings; one bite (the last one removes it); a lit candle cake goes out, an unlit one is eaten (cake with one bite, the candle drops); a full composter (level 8) empties and drops bone meal; a jukebox with a disc ejects it |
| lit candles and campfires | go out |
| chests, trapped chests, barrels, shulker boxes, hoppers, dispensers, droppers, furnaces, smokers, blast furnaces, brewing stands, crafters, copper chests; signs | not an `interact`: the headset opens them with `container_query` ("Containers") and edits signs with `sign_edit` ("Signs"); an `interact` on them gets `interact_denied` |
| anything else | `interact_denied` ("nothing to use on stone"); the client places a block there instead |

**Items** (`item`):

| item | what happens |
|---|---|
| `minecraft:bone_meal` | vanilla bone meal (`Block#applyBoneMeal`): crops, saplings, grass, flowers, seagrass...; `interact_denied` when it does nothing |
| `minecraft:flint_and_steel`, `minecraft:fire_charge` | TNT becomes a primed TNT (fuse 80 ticks; `interact.tnt`); an unlit candle, candle cake or campfire that isn't under water lights; otherwise fire (soul fire on soul sand or soil) in the face cell if it is air and fire can stay there (`interact.fire`) |
| `minecraft:bucket` | picks up a water or lava source, powder snow, or a waterlogged block's water (`interact.fluids`). Clients aim it at fluid sources (vanilla's source-only ray) |
| `minecraft:water_bucket`, `minecraft:lava_bucket`, `minecraft:powder_snow_bucket` | water waterlogs the clicked block when it can, else the fluid (or powder snow) goes into the face cell if that is air, a fluid or replaceable; water evaporates in an ultra-warm dimension (`interact.fluids`) |
| any axe | strips a log, wood, stem, hyphae or bamboo block; scrapes copper one weathering stage back; unwaxes waxed copper (properties kept) |
| `minecraft:honeycomb` | waxes copper |
| any hoe | grass, dirt and paths to farmland, coarse dirt to dirt (air above, not from below); rooted dirt to dirt (drops hanging roots) |
| any shovel | puts out a lit campfire; grass, dirt, podzol, coarse dirt, mycelium, rooted dirt to a path (air above) |
| `minecraft:shears` | a pumpkin is carved (facing the clicked side, from above facing the headset; 4 seeds drop); kelp, weeping, twisting and cave vines stop growing |
| a spawn egg | a spawner takes that mob; anywhere else the mob spawns in the clicked cell when it has no collision box (grass), else in the face cell; at most `interact.max-spawns-per-minute` (60) per session |
| `tabletop:lightning` | a lightning bolt (`real`) or only its flash (`effect`) at the face cell (`interact.lightning`) |
| name tag, lead, wheat, carrots, seeds | only on mobs (`entity_op`); on a block `interact_denied` |

Refusals are `error` with `ref_seq`, `op: "interact"` and `pos`, the message readable as it is ("the axe does nothing to stone", "fire is off on this server (interact.fire)"). Fixture `error_interact_denied.json`.

### `item_registry` (S→C)

Sent once to `interact_v1` sessions, after `block_registry` and before the first `chunk_snapshot`:

```json
{"v":1,"type":"item_registry","seq":2,"t":1760370000014,"count":21,
 "categories":[
  {"id":"use","label":"Use","items":["minecraft:flint_and_steel","minecraft:fire_charge","minecraft:bone_meal","minecraft:bucket","minecraft:water_bucket","minecraft:lava_bucket","minecraft:powder_snow_bucket","minecraft:iron_axe","minecraft:honeycomb","minecraft:iron_hoe","minecraft:iron_shovel","minecraft:shears","minecraft:name_tag","minecraft:lead","minecraft:wheat","minecraft:carrot","minecraft:wheat_seeds","tabletop:lightning"]},
  {"id":"spawn_eggs","label":"Spawn Eggs","items":["minecraft:cow_spawn_egg","minecraft:pig_spawn_egg","minecraft:sheep_spawn_egg"]}]}
```

- `use`: one item per tool class the server implements, in menu order, without the families that are switched off (fire and TNT: flint and steel and fire charges; fluids: the buckets; entities: name tag, lead, foods; lightning: the wand). `tabletop:lightning` is a virtual item (clients draw it with the lightning rod's icon).
- `spawn_eggs`: the eggs of every spawnable entity type the region's world enables (feature flags), minus `interact.deny-spawn` (default the wither and the ender dragon), sorted by id. 86 on Paper 26.3.
- `count`: items in both lists.

### Acting on mobs: `entity_op` (C→S) and `entity_info` (S→C)

With `interact.entities` (default true) the headset acts on the entities it sees in `entities` and `player_positions`, named by their `uuid`:

```json
{"v":1,"type":"entity_op","seq":95,"t":1760370005000,"uuid":"3c1e5f7a-2b4d-4e6f-8a9b-0c1d2e3f4a5b","op":"attack","dir":[0.1,-0.4,0.9]}
{"v":1,"type":"entity_op","seq":96,"t":1760370005100,"uuid":"3c1e…","op":"move","to":[110.5,64.0,-30.5],"yaw":90.0}
{"v":1,"type":"entity_op","seq":97,"t":1760370005200,"uuid":"3c1e…","op":"use","item":"minecraft:shears"}
{"v":1,"type":"entity_op","seq":98,"t":1760370005300,"uuid":"3c1e…","op":"name","text":"Rex"}
```

The entity must exist (else `not_found`) and stand inside the region grown by `entities.margin` (else `out_of_region`); a `to` must be inside the region. The giant's own entities are never found.

| `op` | fields | what happens |
|---|---|---|
| `attack` | `dir` (optional, the ray) | `interact.attack-damage` (5) generic damage and a knockback along `dir`; the red flash comes back as an `actions` `hurt`. Players only with `interact.harm-players` (default false) |
| `kill` | | a living entity dies (`/kill`'s damage type); boats, minecarts, items and frames are removed. Players only with `harm-players` |
| `remove` | | the entity is removed without dying (not players) |
| `use` | `item` | shears shear a sheep, mooshroom, snow golem...; a food the animal breeds with puts it in love (a baby grows up faster); a spawn egg of the same mob spawns a baby, of another mob that mob; a lead ties the mob to the nearest fence within 7 blocks (a knot), or unties it; `tabletop:lightning` strikes it |
| `name` | `text` | the mob's name (formatting codes and control characters removed, at most 64 characters; empty removes it); a named mob no longer despawns. Not players |
| `move` | `to`, `yaw` (optional) | teleport there (players by `PLUGIN` cause); it leaves its vehicle, passengers come along |
| `hold` | `to` | carried: gravity and AI off, teleported to `to` (about 10 a second while the headset carries it). Restored on `drop`, when the session closes, after 3 s without a `hold` or `drop`, and when the plugin stops. Not players |
| `drop` | `to` (optional) | put down at `to` (or where it hangs) with gravity and AI back |
| `freeze`, `unfreeze` | | `Mob#setAware`: it stands still and doesn't react. It is saved with the mob, so the plugin lists frozen mobs in `plugins/TabletopCraftAR/frozen.txt` and `/tabletop unfreeze-all` wakes them (unloaded ones when their chunk loads) |
| `glow` | | toggles the glowing outline |
| `info` | | answered with `entity_info` |

Refusals are `error` with `ref_seq`, `op: "entity_op"`, `entity_op` (the op) and `uuid`. `hold` and `drop` have a rate limit of their own (20 a second); the other ops share the `interact` limit.

**`entity_info` (S→C)**:

```json
{"v":1,"type":"entity_info","seq":120,"t":1760370005310,"ref_seq":99,"uuid":"3c1e…","id":104,"entity_type":"minecraft:wolf","name":"Rex",
 "pos":[108.7,64.0,-28.0],"health":[14.0,20.0],"owner":"Alex","age":-2400,"baby":true,"ai":true,"frozen":false,"glowing":false}
```

`entity_type` (not `type`: that is the envelope's), `id` (as in `entities`), `pos` (feet, world), `name` (a player's name or the custom name), `health` ([current, max], living entities), `owner` (tamed animals), `age` (ageable mobs: negative = ticks until grown up), `baby`, `ai` (false while held or with NoAI), `frozen` (mobs: not aware), `glowing`, and when true `held`, `leashed`, `player`; `vehicle` (the type it rides) and `passengers` (count) when there are any.

**Client guidance** (non-normative; the Quest's `World/UseRules.cs`, `World/EntityRaycast.cs`, `XR/BlockEditMode.cs`, `XR/EntityCard.cs`): the left trigger follows vanilla's right-click order: a mob the ray hits first (an item is used on it, else `info` opens a card), a block the empty hand can use, the selected item on the face, else a `block_edit` place; holding the left grip (sneak) skips the first two. The ray hits mobs and players as their eased boxes (at least half a block, players 0.6 × 1.8 or 1.5 sneaking), and the nearer of a mob and a block wins. Containers and signs call the client's container-view and sign-editor hooks ("Containers", "Signs"). Nothing is guessed locally; refusals become toasts.

### Fixtures and tests

Fixtures `hello_interact.json`, `welcome_interact.json`, `interact.json`, `interact_item.json`, `item_registry.json`, `entity_op.json`, `entity_op_move.json`, `entity_op_use.json`, `entity_op_name.json`, `entity_info.json`, `error_interact_denied.json`, `error_not_found.json`. Java `InteractRulesTest` (the whole table on state strings) and `InteractFixtureTest` (parsers and builders against the fixtures), Python `test_tabletop_protocol.py` (`validate_interact`), C# `Tests~/InteractTests.cs` (parsing, the right-click order, the entity ray, the menu tabs, the info card). Live: `tools/interact_check.py` (41 checks on a flat world) and `Tests~ --live-interact URL` (the app's own classes).

## Undo, box edits and brushes (v0.5.0; cap `power_v1`)

Power tools for the headset operator: undo and redo of its own edits, box edits like vanilla's `/fill` and `/clone`, and sphere or cylinder brushes. Granted to sessions that ask for `power_v1` in `hello.caps` and got `edit_v1`, when config.yml `edit.power` is on (default) and, with `edit.power-require-token`, `auth.token` is set; echoed in `welcome.caps`. `welcome.limits` then also carries:

```json
"undo_depth":10000,"box_max_blocks":32768,"brush_max_radius":8,"brush_blocks_per_second":20000
```

A 0 means that tool is off on this server (`edit.undo-depth`, `edit.box-max-blocks`, `edit.brush-blocks-per-second`). Everything here stays inside the session's region and the world border, uses the same block rules as `block_edit` (only `block_registry` blocks, never `edit.deny`), and is not seen by protection plugins. Docs: QUEST_SETUP.md Part O (the controls), ARCHITECTURE.md §3.13.

### Edit groups: `group` on `block_edit` and `interact`

```json
{"v":1,"type":"block_edit","seq":44,"t":1760370004200,"op":"place","pos":[106,64,-35],"state":"minecraft:stone_bricks","face":"east","group":7}
```

`group` (optional, an integer 0..2^32-1, chosen by the client) ties the edits and uses of one trigger hold together: one `undo` takes back the whole group. The client starts a new id with every trigger press (the Quest counts up from 1 per connection). Without `group` each message is its own group. Servers without `power_v1` ignore the field. `box_edit` and `brush` carry a `group` too: one box is one group, and the strokes of one brush hold share one.

### Undo and redo: `undo`, `redo` (C→S), `history` (S→C)

```json
{"v":1,"type":"undo","seq":120,"t":1760370006000}
{"v":1,"type":"redo","seq":121,"t":1760370006500}
{"v":1,"type":"history","seq":140,"t":1760370006120,"undo":3,"redo":1,"undo_blocks":45,"redo_blocks":2,"depth":10000}
```

The server keeps a journal per session (it goes with the session: a reconnect starts empty). Every change a `block_edit`, `interact`, `box_edit` or `brush` of the session makes is recorded as (position, the state and block entity before, the state after), grouped by `group`:
- **What is recorded.** For `block_edit` and `interact` the server compares the cells around the target before and after it runs (a 3×3×3 window; 7 × 14 × 7 for bone meal, which grows plants and trees), so both halves of a door or bed, a waterlogged cell and a neighbour whose connection changed are all in the group. Block entities in the window (chests, barrels, shulker boxes, signs, banners, spawners...) are snapshotted first, so a broken chest comes back with its items. Box edits and brush strokes record each cell they change, and the neighbours whose connections they recomputed. A `container_edit` or `sign_edit` ("Containers", "Signs") records the block entity it changed (both halves of a double chest) when its contents or text differ afterwards; its undo puts the whole block entity back as it was before the edit, so a change someone else made to the same chest in between is overwritten (the block state is checked, the contents aren't).
- **Bounds.** At most `edit.undo-depth` blocks (default 10000) per session in undo and redo together: the oldest groups are dropped first, but the newest group is always kept whole, so a box bigger than the depth can be undone until the next edit. At most `edit.undo-tile-entities` (256) block entity snapshots: past that the oldest lose theirs and come back as an empty block.
- **`undo`** takes the newest group and puts each cell's "before" back, newest change first. A cell whose block isn't the one the group left there is **skipped** (someone or something changed it since: a player, physics, a `/setblock`); water or lava that only flowed in counts as air. Cells outside the session's current region (the area moved) are skipped too (`outside`). The cells put back form a group on the redo list.
- **`redo`** takes the newest undone group and puts each cell's "after" back, oldest first, with the same skip rules. It goes back on the undo list. Any new group clears the redo list.
- Both run as a job like a box edit (below): one at a time per session, within the per-tick budget, then a `box_progress` with `op` `undo` or `redo`, `group` (the undone group's id, when the client gave it one), `changed`, `skipped` (with up to 16 `skipped_pos`, world positions) and `outside`.
- **`history`** is sent whenever the counts change (at most once a tick): `undo` and `redo` are groups, `undo_blocks` and `redo_blocks` cells, `depth` the server's `edit.undo-depth`.
- Refusals (`error` with `op` `undo` or `redo`): `edit_denied` "nothing to undo" / "nothing to redo", "wait: a fill (42 %) is still running", "undo is off on this server (edit.undo-depth: 0)", no `power_v1`; `rate_limited` past `edit.power-actions-per-second` (5).

**What undo doesn't restore:** entities (a spawned mob, a primed TNT; the items a chest spills when a `block_edit` breaks it, as in vanilla, stay on the ground, so undoing that break duplicates them; box edits, brushes and undo itself empty a container before replacing it, so nothing spills there), changes after the moment of the edit (sand that fell, water that spread, fire, grown crops: those cells are skipped), anything beyond the window of a `block_edit` or `interact` (a lever's redstone line further away updates by itself when the lever comes back; a very large tree grown with bone meal), and block entities past `edit.undo-tile-entities`.

### Box edits: `box_edit` (C→S), `box_progress` (S→C)

```json
{"v":1,"type":"box_edit","seq":130,"t":1760370007000,"op":"fill","from":[110,64,-30],"to":[101,60,-21],"state":"minecraft:stone_bricks","group":9}
{"v":1,"type":"box_edit","seq":131,"t":1760370007200,"op":"replace","from":[101,60,-30],"to":[110,64,-21],"state":"minecraft:glass","filter":"minecraft:stone_bricks","group":10}
{"v":1,"type":"box_edit","seq":132,"t":1760370007400,"op":"clone","from":[101,60,-30],"to":[104,62,-29],"dest":[112,60,-30],"rotate":1,"group":11}
```

| field | type | req | meaning |
|---|---|---|---|
| `op` | string | yes | `fill`: every cell gets `state`. `replace`: the cells matching `filter` get `state`. `clear`: every cell becomes air (with a `filter`, only the matching ones). `walls`: the four vertical sides get `state` (no floor or ceiling). `hollow`: the six faces get `state` and the inside becomes air (vanilla's `/fill ... hollow`). `clone`: the box is copied to `dest`. |
| `from`, `to` | int[3] | yes | two opposite corners, world block coordinates, inclusive, in any order |
| `state` | string | all but `clear`, `clone` | a block state, as for `block_edit` `place`: a `block_registry` block or variant, not air, not `edit.deny`, not one half of a door, bed or tall plant. Leaves are made persistent; a waterloggable state is waterlogged exactly where the cell held water |
| `filter` | string | `replace` (and optionally `fill`, `clear`) | a block id (`minecraft:stone`: any of its states) or a state with the properties that must match (`minecraft:oak_stairs[facing=east]`) |
| `dest` | int[3] | `clone` | the copy's minimum corner |
| `rotate` | int | no | `clone`: quarter turns clockwise seen from above, 0..3. A cell at local (x, z) of a box sx by sz goes to (sz−1−z, x) of the turned box, and its state turns with it (a stair facing east faces south after one turn) |
| `group` | int | no | the edit group (above) |

- Limits: at most `edit.box-max-blocks` (default 32768, vanilla's `/fill` limit) cells, the source's for a clone; the box (and a clone's copy) inside the session's region (else `out_of_region`), the build height and the world border. One job per session: a second `box_edit`, an `undo` or a `redo` while one runs gets `edit_denied` "wait: ...". At most `edit.power-actions-per-second` (5) `box_edit`, `undo` and `redo` together.
- The server applies a job a slice at a time, `edit.box-budget-ms` (default 2 ms) of main-thread time per tick for all sessions' jobs together, bottom layer first: `setBlockData(state, false)` (no neighbour reactions, like vanilla's `/fill`), recording each change for undo, then a fix-up pass over the cells it changed: connections from the neighbours (fences, panes, walls, stairs, rails, redstone, snowy grass, gates, leaves; and the neighbours just outside the box, inside the region), then, with `edit.physics`, the neighbours are told (vanilla's `updateNeighborsAt`, as `/fill` does: sand falls, water flows in, redstone reacts). A container in the way is emptied before it is replaced (its items are in the journal; nothing spills, as with `/fill`). A clone reads its source first (so source and copy may overlap), copies block entities (a chest's items too, like `/clone`) and leaves out `edit.deny` blocks (counted as skipped). Changes reach every headset through the normal diff: a `block_update`, or a sectioned snapshot of the changed sections when more than 4096 cells changed in a tick (§4.5, §4.18).
- **`box_progress`** (S→C) every 10 ticks while a job runs and once when it ends (`finished: true`):

```json
{"v":1,"type":"box_progress","seq":150,"t":1760370007500,"op":"fill","group":9,"ref_seq":130,"phase":"write","processed":12000,"total":32768,"changed":11800,"skipped":0,"outside":0,"finished":false}
{"v":1,"type":"box_progress","seq":151,"t":1760370006110,"op":"undo","group":7,"ref_seq":120,"phase":"done","processed":14,"total":14,"changed":12,"skipped":2,"outside":0,"finished":true,"skipped_pos":[[106,64,-35],[107,64,-35]],"ms":3.2,"ticks":1}
```

`op` is the box op, `undo` or `redo`; `ref_seq` the request's `seq`; `phase` `read` (a clone's source), `write`, `fixup`, `done` or `failed` (a server error stopped it; what changed so far stays and is undoable); `processed` of `total` cells looked at (walls only count the sides), `changed`, `skipped` (undo/redo conflicts, a clone's `edit.deny` blocks), `outside`; when finished `ms` (wall time) and `ticks`. Brush strokes send none.
- Refusals: `error` with `op: "box_edit"`, `box_op` and `group`; codes `edit_denied` ("the box is 40000 blocks, more than 32768 (edit.box-max-blocks)", "command block can't be placed from the headset (edit.deny)", "wait: ..."), `out_of_region`, `bad_state`, `rate_limited`, `bad_message` (malformed). Fixture `error_box_denied.json`.

### Brushes: `brush` (C→S)

```json
{"v":1,"type":"brush","seq":160,"t":1760370008000,"op":"paint","shape":"sphere","center":[105,63,-35],"radius":3,"state":"minecraft:moss_block","group":12}
{"v":1,"type":"brush","seq":161,"t":1760370008150,"op":"replace","shape":"cylinder","center":[105,63,-35],"radius":4,"height":1,"state":"minecraft:dirt_path","filter":"minecraft:grass_block","group":13}
```

One stroke; the client sends them while its trigger is held (the Quest at most every 0.15 s, at the aimed block), all with the hold's `group`.
- `shape`: `sphere` (default), the cells with dx² + dy² + dz² ≤ r² + r around `center`; or `cylinder`, upright, dx² + dz² ≤ r² + r, `height` layers (default 2r + 1, 1 = a flat disc) from −(height−1)/2 to height/2. `radius` 0..`brush_max_radius`.
- `op`: `paint` (every cell gets `state`), `replace` (the cells matching `filter` get `state`), `erase` (every block becomes air), `smooth` (one 3×3×3 majority vote: a cell with at least 14 solid cells around it, itself included, is solid afterwards, else air; a cell that becomes solid takes the most common solid neighbour, so a lone bump goes, a lone hole fills and flat ground stays).
- Cells outside the region, the build height or the border are left out. A stroke runs as a job like a box edit (within the same budget, with the fix-up pass) and is recorded in the hold's group; strokes queue behind each other (at most 3; more get `rate_limited` "brush strokes are queueing up"), and wait for a running box edit, undo or redo (`edit_denied` "wait: ...").
- Each stroke costs its cell count (a radius 8 sphere is 2553) from a per-session bucket of `brush_blocks_per_second` (20000); a stroke that doesn't fit gets `rate_limited`. Refusals carry `op: "brush"`, `brush_op` and `group`.

### Fixtures and tests (power)

Fixtures `hello_power.json`, `welcome_power.json`, `block_edit_group.json`, `interact_group.json`, `undo.json`, `redo.json`, `history.json`, `box_edit_fill.json`, `box_edit_replace.json`, `box_edit_clone.json`, `box_progress.json`, `box_progress_done.json`, `brush.json`, `brush_replace.json`, `error_box_denied.json`. Java `PowerFixtureTest` (parsers and builders against the fixtures), `UndoJournalTest` (groups, merging, depth, redo, block entity caps), `BoxPlanTest` (the cells of each op, the clone's turn, brush shapes and smoothing); Python `test_tabletop_protocol.py` (`validate_power`); C# `Tests~/PowerTests.cs` (parsing, B's tap and hold, groups on `block_edit` and `interact`, the wand, paste, brushes, the Tools tab). Live: `tools/power_check.py` (39 checks on a flat world) and `Tests~ --live-power URL [TOKEN]` (the app's own classes).

## World state: time and weather (v0.5.0; cap `world_v1`)

So the headset can light the miniature like the game does at night, in rain and in thunderstorms, and draw rain, snow and lightning. A client lists `world_v1` in `hello.caps`; the server grants it unless config.yml `world-state.enabled` is false and lists it in `welcome.caps`.

```json
{"v":1,"type":"world_state","seq":7,"t":1760370001000,"world":"world","dimension":"overworld","skylight":true,
 "time":114000,"day_time":18000,"rain":1,"thunder":1}
```

| field | type | meaning |
|---|---|---|
| `world` | string | the world of the session's region |
| `dimension` | string | `overworld`, `the_nether`, `the_end` or `custom` (`World#getEnvironment`) |
| `skylight` | bool | the dimension has sky light |
| `time` | int | total ticks of the world's clock (`World#getFullTime`): the day count and the moon phase |
| `day_time` | int | 0–23999 (`World#getTime`): what the game's day timeline is keyed on (6000 noon, 18000 midnight) |
| `rain`, `thunder` | 0 or 1 | it rains (or snows), a thunderstorm; `thunder` only while `rain` |
| `fixed_time` | bool, optional | the clock doesn't run (the Nether and the End); missing = false |
| `ticking` | bool, optional | false while the clock is stopped (the daylight cycle game rule is off, or the server's tick is frozen); missing = true |

**When.** Right after the session's region is known, whenever the world, the dimension, the weather or the clock mode changes or the clock jumps (`/time set`: more than 40 ticks off what the client predicts), and otherwise about once a second (every 20 ticks). Between messages the client runs the clock at 20 ticks a second unless `fixed_time` or `ticking: false`. About 150 bytes a second.

Client guidance (non-normative; Quest `World/DayLight.cs`, `World/WorldLight.cs`, `World/WeatherRenderer.cs`): the light follows 26.3's lightmap inputs: the overworld's `sky_light_factor` and `sky_light_color` timeline (data/minecraft/timeline/day.json: factor 1 from tick 730 to 11270, 0.24 from 13140 to 22860, linear between; colour white to #7a7aff), the weather layers (rain moves the factor 31.25% and thunder 52.7% of the way to 0.24 and the colour toward #7a7aff), a lightning flash (factor 1 for 2 ticks), and the dimension's ambient colour and sky factor (Nether #302821 and 0, End #3f473f and 0); rain and thunder fade by 0.01 a tick like the game's rain levels. Rain or snow falls per column by the biome at the surface (none in biomes without precipitation; snow below 0.15 of the height-adjusted temperature), down to the highest block that blocks motion. The `lightning_bolt` entity (§4.21) is drawn with the game's bolt geometry and flashes the sky.

Fixture `world_state.json`. Java `LivingTest` (message and send rule), Python `test_world_state_fixture_and_rules`, C# `Tests~/LivingTests.cs` (world clock, lightmap). Live: `tools/living_check.py`.

## Living entities: leads, gear, displays (v0.5.0; `entities_v1`)

More fields of `entities` entries (§4.21), with the same delta rules (left out when not set in full entries; a field that went away is sent as `null`, `false` or 0):

| field | type | meaning |
|---|---|---|
| `leash` | int | the entity id of whoever holds this entity's lead: a player (`player_positions[].id`), a leash knot, a mob, a happy ghast holding a boat |
| `eye` | number | eye height in blocks, living entities (the game's lead offsets and rope hold points use it); 2 decimals |
| `chested` | bool | a donkey, mule or llama carries a chest |
| `name_visible` | bool | the custom `name` shows over the entity all the time (CustomNameVisible) |
| `xp` | int | an experience orb's value (its icon) |
| `owner` | int | a fishing bobber's player (the line goes to their rod hand) |
| `carried` | string | the block state an enderman carries |
| `peek` | number | how far a shulker's lid is open, 0..1; resent when it moved by 0.05 or opened or closed fully |
| `attach` | string | the face a shulker sticks to, when not `down` (`up`, `north`...) |
| `display` | object | display entities, below; the whole object is resent when anything in it changes |

- Gear stays in `equipment`: `body` (horse, wolf and nautilus armour, a llama's carpet, a happy ghast's harness) and `saddle`. A tamed wolf's or cat's `color` is its collar.
- A snow golem without its pumpkin is `sheared`. A cushion has its `color`.
- Text displays are listed now (they weren't before); `marker`, `interaction` and `area_effect_cloud` still aren't.

**`display`** (block, item and text displays), from `Display#getTransformation` and the display's state:

```json
"display":{"translation":[0.0,0.5,0.0],"left_rotation":[0.0,0.0,0.3827,0.9239],"scale":[2.0,2.0,2.0],
           "right_rotation":[0.0,0.0,0.0,1.0],"billboard":"fixed","brightness":[15,15],"item_transform":"fixed"}
```

| field | meaning |
|---|---|
| `translation`, `scale` | 3 numbers (3 decimals), game frame |
| `left_rotation`, `right_rotation` | quaternions `[x, y, z, w]` (4 decimals), game frame; the transformation is translation × left × scale × right |
| `billboard` | `fixed`, `vertical`, `horizontal` or `center` |
| `brightness` | `[block, sky]` light override, missing = the world's light at the entity |
| `item_transform` | item displays: `none`, `thirdperson_lefthand`, `thirdperson_righthand`, `firstperson_lefthand`, `firstperson_righthand`, `head`, `gui`, `ground`, `fixed` |
| `text`, `line_width`, `background`, `text_opacity`, `align` | text displays: plain text (lines split by `\n`), the line width in font pixels, the ARGB background colour, opacity 0–255, `center`/`left`/`right`; `shadow`, `see_through`, `default_background` when set |

The block display's block is its `block`, the item display's item its `item`, as before.

Client guidance (non-normative; Quest `World/LivingLooks.cs`, `EntityFigure.Living.cs`, `LeashRenderer.cs`): draw a lead as the game's LeashFeatureRenderer does (24 steps, two crossed ribbons, alternating browns, sagging) from the entity's leash offset to the holder's rope hold point every frame; gear as the game's equipment layers with the 26.x `assets/minecraft/equipment/*.json` textures; displays with orientation (billboard) × translation × left rotation × scale × right rotation, no bob or spin, a block display's block with its corner at the entity, an item display's item turned 180° about Y.

Fixture `entities_living.json` (a leashed wolf and its knot, a horse in armour with a saddle, a chested llama with a carpet, an orb, a bobber, an enderman with a block, a shulker on a wall, text, item and block displays, a lightning bolt, a snow golem without its pumpkin, a villager with a visible name). Java `LivingTest`, Python `test_entities_living_fields`, C# `Tests~/LivingEntityTests.cs`. Live: `tools/living_check.py`.

## Operator tools: following, marks, containers, signs (v0.5.0; caps `marks_v1`, `interact_v1`)

Navigation powers and block-entity editing for the headset operator. Jumps and following need no message of their own: they are `region_request`s (§4.13, origin form with `view_margin`, §4.17) on the shared region (last writer wins: every headset on the server moves along). Docs: QUEST_SETUP.md Part N, ARCHITECTURE.md §3.12.

### Worlds and spawn

`welcome` (every client) and `region_info` list the server's worlds; `region_info` also carries the region world's spawn:

```json
"worlds":[{"name":"world","env":"normal","min_y":-64,"max_y":320,"spawn":[0,64,0]},
          {"name":"world_nether","env":"nether","min_y":0,"max_y":256,"spawn":[0,70,0]},
          {"name":"world_the_end","env":"the_end","min_y":0,"max_y":256,"spawn":[100,49,0]}]
"spawn":[0,64,0]
```

- `env`: `normal`, `nether`, `the_end` or `custom` (as in `server_status`, §4.28). `min_y`/`max_y`: the build height `[min_y, max_y)`, the same as `limits.build_height` for that world. `spawn`: `World#getSpawnLocation` as a block.
- A world switch is a `region_request` with that world's `world`, the same x/z size and margins, the height (and y margin) shrunk to fit its build height, centred on its spawn or a bookmark. Another world is always a full snapshot; `region_changed.limits.build_height` (§4.13) confirms the height. Fixture `region_request_jump.json` (into the nether).

### Following a player

Client-side (the server's `subscribe.follow` is not supported, §4.3). The headset:
- while the player stands on the table (they are in `player_positions`, §4.6), glides the view after their eased position: the offset closes exponentially (about 90% a second), with a dead zone of 0.75 block across and a quarter of the window height up and down, so walking around doesn't bob the table. The glide moves the region by section shifts like the left stick (§4.17);
- asks where they are with `region_query{player}` (§4.13) once a second while they aren't on the table, and every 5 s while they are (a world change); `region_info.players[0]` gives `world` and `pos`, an `error` (`bad_message`, "no online player named ...") means they left;
- jumps (a `region_request` centred on them, as a world switch above) when they are more than 16 blocks beyond the loaded region or in another world, and puts the view on them when the region arrives;
- stops following on any manual glide, area setup, Reset view, a jump of its own, or the player leaving.

The giant itself can't be followed: it stands where the headset looks at the table, so following it would chase itself.

### Marks: bookmarks, spawn and worlds (`marks_v1`)

Shared bookmarks: named world positions every headset sees, kept in `plugins/TabletopCraftAR/bookmarks.yml`. Granted when config.yml `marks.enabled` (default true) and, with `marks.require-token`, `auth.token` is set; echoed in `welcome.caps`. `welcome` then carries `"marks":{"max":200,"edit":true}` (`marks.max`, and `marks.edit`: false = the headsets only read and jump).

**`bookmark` (C→S)**:

```json
{"v":1,"type":"bookmark","seq":70,"t":1760370007000,"op":"add","name":"Village","world":"world","pos":[120,64,-300]}
{"v":1,"type":"bookmark","seq":71,"t":1760370007100,"op":"remove","name":"Village"}
```

- `name`: at most 32 characters after control characters and `§` are removed and whitespace is collapsed; names are unique ignoring case (adding an existing name moves it). `pos`: integer world coordinates (`y` clamped to the world's build height); `world` must exist (else `world_not_found`).
- Refusals: `mark_denied` with `op: "bookmark"` and `name` (no `marks_v1`, `marks.edit` off, more than `marks.max`, removing an unknown name); `bad_message` when malformed. Shares the query rate limit (`commands.completions-per-second`, 10).

**`bookmarks` (S→C)**, to every `marks_v1` session after `welcome` (after `block_registry` and `item_registry`, before the first snapshot) and whenever one changes:

```json
{"v":1,"type":"bookmarks","seq":3,"t":1760370007010,"max":200,"marks":[
 {"name":"Nether hub","world":"world_nether","pos":[8,70,-4],"by":"Operator"},
 {"name":"Village","world":"world","pos":[120,64,-300],"by":"Operator"}]}
```

Sorted by name (ignoring case); `by`: the operator name of the headset that added it.

Jumping (non-normative; the Quest's Marks tab): to typed coordinates (`x z` keeps the view's height; `x y z`; `~` relative to the view), the world's spawn, a bookmark, or another world's spawn. Each is a `region_request` keeping the x/z size and margins, centred on the target; in the same world the origin moves by whole 16-block sections from the current one (centred to within 8 blocks) so the server reads only the new sections and may send a delta (§4.18), and the client then puts the view on the target itself.

### Containers (`interact_v1`)

The headset's container view: chests (a double chest as one view), trapped and copper chests, barrels, shulker boxes, hoppers, dispensers, droppers, furnaces, smokers, blast furnaces, brewing stands and crafters inside the region. Needs `interact_v1` and config.yml `interact.containers`: `edit` (default), `view` (look only) or `false`; `welcome.interact.containers` says which.

**`container_query` (C→S)** opens (or refreshes) the session's view; one view per session, a query for another container ends the previous one. **`container_close` (C→S)** ends it.

```json
{"v":1,"type":"container_query","seq":92,"t":1760370008000,"pos":[1,64,2]}
{"v":1,"type":"container_close","seq":96,"t":1760370008400,"pos":[1,64,2]}
```

**`container` (S→C)**: the answer to a query (with its `ref_seq`), then again (without `ref_seq`) whenever the contents change while the view is open (checked every 4 ticks and right after an edit):

```json
{"v":1,"type":"container","seq":140,"t":1760370008010,"ref_seq":92,"pos":[1,64,2],"block":"minecraft:chest","title":"Large Chest",
 "size":54,"other":[2,64,2],"edit":"edit","items":[[0,"minecraft:diamond",3],[5,"minecraft:torch",64],[30,"minecraft:oak_planks",16]]}
{"v":1,"type":"container","seq":142,"t":1760370008030,"pos":[1,64,2],"closed":true,"reason":"the container is gone"}
```

- `items`: the filled slots as `[slot, item id, count]` (item components such as names and enchantments are kept on the server, not sent). `size`: the slot count (54 for a double chest: the left half's 27, then the right half's). `other`: the other half of a double chest. `title`: the custom name, else vanilla's (`Chest`, `Large Chest`, `Barrel`, `Shulker Box`, `Item Hopper`, `Furnace`...). `edit`: `edit` or `view`.
- `closed: true` with a `reason` ends the view by itself: the block is gone or no longer a container, the region moved away from it or to another world, or containers were switched off.
- While any headset views a chest, barrel or shulker box, its lid stays open for Java players (`Lidded#open`; a barrel's `open` property shows in `block_update`); it closes when the last view ends (close, another query, disconnect, plugin stop).

**`container_edit` (C→S)**:

```json
{"v":1,"type":"container_edit","seq":93,"t":1760370008100,"pos":[1,64,2],"slot":5,"item":"minecraft:torch","count":32,"was":["minecraft:torch",64]}
{"v":1,"type":"container_edit","seq":95,"t":1760370008300,"pos":[1,64,2],"op":"sort"}
```

| field | meaning |
|---|---|
| `op` | `set` (default) or `sort` |
| `slot` | `set`: the slot (0 .. size − 1) |
| `item`, `count` | `set`: the slot becomes `count` × `item` (an item id); `item` null or `count` 0 empties it ("take out" and "clear": the items go nowhere, as a creative player's would). The count is clamped to the item's stack size; the same item keeps its components (a named sword stays named) |
| `was` | optional `[item or null, count]`: what the client saw in the slot; if the slot holds anything else now (a Java player or a hopper changed it), the edit is refused |

- `sort` (chests, barrels, shulker boxes, hoppers, dispensers, droppers only): stacks of the same item and components merged up to their stack size, ordered by item id, the biggest first.
- Slot rules as vanilla's `canPlaceItem`: a furnace's output slot takes nothing and its fuel slot only fuel; a brewing stand's bottle slots take potions and glass bottles, its fuel slot only blaze powder; a shulker box takes no shulker box.
- No acknowledgement: the next `container` shows the result. Refusals are `error` with `ref_seq`, `op` (`container_query` or `container_edit`) and `pos`: `out_of_region`, `interact_denied` ("no container there (stone)", "containers are view-only on this server (interact.containers: view)", "the slot changed meanwhile (it holds 16 ender pearl)", "stone isn't fuel", "a shulker box can't go inside a shulker box"). Container messages share the `interact` rate limit.
- With `power_v1`, each edit is journalled for undo ("Undo, box edits and brushes"): an optional `group`, as on `block_edit`; both halves of a double chest.

### Signs (`interact_v1`)

**`sign_edit` (C→S)**, with `interact_v1` and config.yml `interact.signs` (default true; `welcome.interact.signs`):

```json
{"v":1,"type":"sign_edit","seq":97,"t":1760370008500,"pos":[106,64,-35],"side":"front","lines":["","Shop","open 9-5",""],"color":"light_blue","glowing":true}
```

- `side`: `front` or `back`. `lines`: exactly 4 strings; control characters and `§` are removed, each is cut to 90 characters. `color` (optional): a dye name. `glowing` (optional): glowing ink. Standing, wall and hanging signs.
- With `interact.sign-event` (default true) the server first fires a `SignChangeEvent` (no player: the headset isn't one), so protection and logging plugins can see the change, alter its lines or cancel it ("another plugin refused the sign change (protection)"). A listener that needs a player fails inside its own handler (the server logs it) and doesn't block the edit.
- A waxed sign is refused, as in vanilla. No acknowledgement: the new text reaches every headset through `block_entities` (§4.29). Refusals are `error` with `op: "sign_edit"` and `pos` (`out_of_region`, `interact_denied`).
- With `power_v1`, each edit is journalled for undo ("Undo, box edits and brushes"; an optional `group`, as on `block_edit`).

### Fixtures and tests

Fixtures `hello_operator.json`, `welcome_operator.json` (`worlds`, `marks`, `interact.containers`/`signs`), `region_info_worlds.json`, `region_request_jump.json`, `bookmark.json`, `bookmark_remove.json`, `bookmarks.json`, `error_mark_denied.json`, `container_query.json`, `container.json` (a double chest), `container_furnace.json` (view only), `container_closed.json`, `container_edit.json`, `container_edit_put.json`, `container_edit_sort.json`, `container_close.json`, `error_container_denied.json`, `sign_edit.json`. Java `OperatorFixtureTest` (parsers and builders) and `ContainerRulesTest` (slot rules, sorting, titles, the bookmark store), Python `test_tabletop_protocol.py` (`validate_operator`, `validate_worlds`), C# `Tests~/OperatorTests.cs` (parsing, jump plans, following against `NavState`, the Marks tab, the container view, the sign editor). Live: `tools/operator_check.py` (35 checks with a mineflayer player) and `Tests~ --live-operator URL TOKEN PLAYER` (following with the app's own classes).

## Sound (v0.6.0; cap `sound_v1`)

The headset plays the game's sounds on the table from the user's own `sounds.zip` (made by `tools/sounds/PackSounds.java` from their own launcher install, QUEST_SETUP.md Part E; nothing of the game ships with the project). It works out most sounds itself from what it already gets (below); the server adds what only it knows. A client lists `sound_v1` in `hello.caps`; the server grants it unless config.yml `sound.enabled` is false and lists it in `welcome.caps`.

### `sound_groups` (S→C)

Once per connection to `sound_v1` sessions, after `block_registry` (and `item_registry`), before the first `chunk_snapshot`. Each block's SoundType, which the game keeps in code (`BlockData#getSoundGroup`); blocks with the same five sounds, volume and pitch share one group:

```json
{"v":1,"type":"sound_groups","seq":3,"t":1760370000015,"count":7,
 "groups":[{"break":"block.stone.break","step":"block.stone.step","place":"block.stone.place","hit":"block.stone.hit","fall":"block.stone.fall",
            "volume":1.0,"pitch":1.0,"blocks":["minecraft:stone","minecraft:cobblestone","minecraft:stone_bricks"]},
           {"break":"block.anvil.break","step":"block.anvil.step","place":"block.anvil.place","hit":"block.anvil.hit","fall":"block.anvil.fall",
            "volume":0.3,"pitch":1.0,"blocks":["minecraft:anvil"]}]}
```

- Sound ids are sound event ids as `sounds.json` keys them: the `minecraft:` namespace is left out, others are kept (`mymod:block.thing.break`). `count`: the blocks listed in all groups. Paper 26.3: 1283 blocks in 129 groups, about 64 KB.
- Clients play them like the game: a break or place at volume (v + 1) / 2 and pitch × 0.8, a digging hit at (v + 1) / 8 and pitch × 0.5, a step at v × 0.15. A block that isn't listed falls back to the client's own guess.

### `sound` and `sound_stop` (in `actions`)

Two more `actions` kinds (§4.22), sent only to sessions with both `actions_v1` and `sound_v1`, for sounds a client can't infer:

```json
{"kind":"sound","sound":"block.note_block.harp","pos":[105.5,64.5,-35.5],"volume":3.0,"pitch":1.189}
{"kind":"sound","sound":"music_disc.cat","pos":[103.5,64.5,-38.5],"volume":4.0,"pitch":1.0,"stream":true}
{"kind":"sound_stop","pos":[103,64,-38]}
```

| field | meaning |
|---|---|
| `sound` | the sound event id (as in `sound_groups`) |
| `pos` | world position (3 decimals); block sounds at the block centre |
| `volume` | the game's volume: loudness min(volume, 1), heard up to attenuation_distance (16 blocks) × max(1, volume) |
| `pitch` | 0.5–2 |
| `stream` | true for long sounds the client should stream (jukebox songs) |
| `sound_stop` `pos` | stop what plays at that block (a jukebox stopped or emptied) |

The server sends: note blocks (every note played, by players, redstone or the headset: `block.note_block.<instrument>`, volume 3, pitch 2^((note − 12) / 12)), bells (`block.bell.use`, volume 2), containers opening and closing (chests, trapped chests, barrels, shulker boxes, ender chests, copper chests; volume 0.5, pitch 0.9–1.0), jukebox songs (the disc's sound, volume 4, `stream`) and their stop, explosions (`entity.generic.explode` volume 4, pitch (1 + (r1 − r2) × 0.2) × 0.7; wind charges `entity.wind_charge.wind_burst`, breeze charges `entity.breeze.wind_burst`), fire lit by a player (flint and steel, fire charge), a player's bucket emptied or filled, bone meal, and the sound the plugin plays for the headset's own `interact` (doors, levers, buttons, buckets, tools...). The same sound at the same block collapses within a tick; sounds up to 8 blocks outside the region (horizontally) are included.

Client guidance (non-normative; Quest `World/SoundDirector.cs`, `SoundRules.cs`, `SoundMixer.cs`): from `actions` `break` (with an `id`; explosion breaks are covered by the explosion) and `place` the block's group sound; from `dig` a hit every 4 ticks while the cracks keep coming; from `block_update` the sounds of state changes the game makes when a player or redstone uses a block (doors, trapdoors and fence gates by their family, levers, buttons, pressure plates, pistons, comparators, candles, campfires, composters) or a tool (stripping, scraping, waxing, tilling, paths, carving), but no break or place sound for a plain change (`/fill`, physics); from `entities` and `player_positions` footsteps (a step per 1/0.6 blocks walked on the ground; mobs with their own `entity.<type>.step`, else the block underfoot), mob ambient sounds on the game's clock (each tick a chance of counter/1000, then 80 ticks of quiet), hurt (`actions` `hurt`) and death (pose `dying`) sounds, item and experience pickups (one vanishing next to a player or mob), a lightning bolt's thunder and impact, primed TNT; from `world_state` rain where it lands (`weather.rain`, volume 0.2, a few times a second); the game's random ambience of flowing water, open lava and fire near the view. A sound at a block within a second of the same sound at that block (inferred and sent) plays once. On the table the fall-off distance is scaled with the block size (QUEST_SETUP.md Part E).

Fixtures `hello_sound.json`, `welcome_sound.json`, `sound_groups.json`, `actions_sound.json`. Java `SoundTest`, Python `test_sound_fixtures_and_rules`, C# `Tests~/SoundTests.cs`. Live: `tools/sound_check.py`.

## Asset delivery (v0.6.0; cap `assets_v1`)

A server hands its operator's own headsets the files the app reads from its files folder: `pack.zip`, `entity_models.json`, `sounds.zip`, so nothing has to be pushed with `adb`. **The project ships no Mojang assets**: these are the operator's own copies, made from their own Minecraft install, and only for their own devices. Since 1.1.0: on by default, with the files built by the plugin itself from the server PC's Minecraft install (config.yml `assets.auto`, "Where the files come from" below), for paired headsets ("Pairing").

- **Granted** (`assets_v1` in `welcome.caps`) when the server has files to serve (config.yml `assets.auto`, or `assets.serve` with a folder `assets.dir`), the session is **trusted** (it presented `auth.token`, or the device token of a paired headset, "Pairing"), the client asked for `assets_v1` and its address is private (10/8, 172.16/12, 192.168/16, fc00::/7), link-local or loopback. Only the three names above are ever read. (Up to 1.0.x: only `assets.serve` with `auth.token` set.)
- **Where the files come from** (1.1.0): for each name, `assets.dir` first (with `assets.serve`), then the files the plugin builds into `plugins/TabletopCraftAR/assets/` from the server PC's own launcher folder (`versions/<mc>/<mc>.jar` and `.json` of the server's game version, in the usual places or `assets.minecraft-dir`): `pack.zip` is the client jar's `assets/` entries; `entity_models.json` and `sounds.zip` come from `tools/entity_models/DumpEntityModels.java` and `tools/sounds/PackSounds.java`, run in a separate JVM. They are rebuilt when the jar, its JSON or the plugin version change. Nothing is downloaded from anywhere.
- **Offer (S→C)**, after `welcome` and the registries, once the server has hashed the files (at start and `/tabletop reload`; again when a file changes while being served):

```json
{"v":1,"type":"assets","seq":4,"t":1760370000016,"chunk":262144,
 "files":[{"name":"entity_models.json","size":91234,"sha256":"5f70…c6ef"},{"name":"sounds.zip","size":67012345,"sha256":"0263…813f"}]}
```

  `chunk`: the length an `asset_get` should ask for (at most 1 MiB). Files not in the folder are left out; an empty list means nothing is offered.
  Since 1.1.0, optional: `state` says what the automatic files are doing (`building`, `ready`, `missing`: no Minecraft of the server's version on its PC, `failed`), and `note` (not when `ready`) says it in one line for the headset ("No Minecraft 26.3 on the server's PC: start Minecraft: Java Edition 26.3 once in the launcher there, then /tabletop assets rebuild"). A new offer follows when they change. Without `state` (a 1.0.x plugin, or `assets.auto` off) the client just takes the files. Fixture `assets_building.json`:

```json
{"v":1,"type":"assets","seq":4,"t":1760370000016,"chunk":262144,"files":[],"state":"building","note":"building pack.zip, entity_models.json, sounds.zip from the server PC's Minecraft 26.3"}
```
- **`asset_get` (C→S)**: `{"type":"asset_get","name":"sounds.zip","offset":262144,"length":262144}`. `offset` 0..size−1, `length` 1..1 MiB (the last chunk is shorter). At most 8 in flight per session (more get `rate_limited`). Errors carry `op: "asset_get"` and `name`: `asset_denied` (no grant), `not_found` (not offered, or changed on the server: a new offer follows), `bad_message` (offset or length out of range).
- **The answer is one binary frame** (the only binary frame in the protocol):

| bytes | field |
|---|---|
| 0–3 | `TTA1` (ASCII) |
| 4 | n: the name's length, 1–64 |
| 5 … 5+n−1 | the name (ASCII) |
| 5+n … 12+n | the offset, unsigned 64-bit big-endian |
| 13+n … | the file's bytes from the offset |

- Frames for one session go out in request order, paced to `assets.max-mb-per-second` (default 8 MB/s) on a worker thread, so the world stream keeps flowing. The server logs every finished file (`ASSETS session=… sent sounds.zip 67.0 MB in 8.4 s`) and every refusal.
- **Client guidance** (non-normative; Quest `World/AssetSync.cs`): compare each offered file with the local copy by size and SHA-256 (cache the hash by size and date), download the missing or different ones one at a time with a few requests in flight into `<name>.part` (which survives a dropped link and resumes), check the SHA-256 of the whole file before replacing the old one, then reload what changed.
- **Why binary frames over the same WebSocket** and not an HTTP GET with the token: the session is already authenticated and LAN-checked, no second port or firewall rule is needed, Java-WebSocket can't serve plain HTTP on its port, and pulling chunk by chunk gives flow control and resuming for free.

Fixtures `assets.json`, `asset_get.json`, `error_asset_denied.json`, `asset_chunk.bin` (one frame). Java `SoundTest` (frame byte for byte, LAN check), Python `test_asset_messages_and_frame`, C# `Tests~/SoundTests.cs` (offer, frame, a download against an in-memory server with resume and a checksum mismatch). Live: `tools/sound_check.py --assets-dir DIR`, `Tests~ --live-assets URL TOKEN` (the app's own classes; dummy files).

## Pairing (1.1.0; cap `pair_v1`)

So a headset needs nothing typed or pushed: instead of a shared token in tabletop.json, an operator who can see the headset approves it once with a 6-digit code, and the server gives that headset its own device token. QUEST_SETUP.md "Store install" shows the steps; the plugin side is `PairingService.java`, the app side `Net/Pairing.cs`.

**Flow.**

1. The client asks for `pair_v1` in `hello.caps` and, if it was paired with this server before, sends `device_token`. It also sends `device_name` (what the server lists it as, e.g. "Quest 3"; at most 32 printable characters are kept).
2. The server decides at `hello`:
   - a valid `auth.token` or a known `device_token`: the session is **trusted** and goes on as usual (`welcome`);
   - neither, on an **open** server (no `auth.token`, config.yml `auth.require-pairing` false): `welcome` as usual, untrusted. The client then sends `pair_request` to get a code (the Quest app does so by itself when it has no token for this server);
   - neither, on a **closed** server (`auth.token` set, or `auth.require-pairing`): a `pair_v1` client gets `pair_code` at once and the session waits (state "pairing", no `welcome` yet). A client without `pair_v1` gets `auth_failed` and close 4003, as before.
3. The client shows the code. The server never prints it: its console and online operators see `Headset "Quest 3" (192.168.1.23) wants to pair: type the 6-digit code it shows, /tabletop pair <code>`. Typing the code proves the operator can see that headset.
4. An operator runs `/tabletop pair <code> [player]` (console, or `tabletop.admin`). The server sends `paired` with a new token. `player` (optional) is the Java account whose skin the giant wears and whose name is used in chat when the headset names none (`avatar_config.skin_player`, `hello.operator_name`).
5. A waiting session (closed server) then continues on the same connection: `welcome` and everything after it, as a trusted session. A session that already had `welcome` (open server) keeps going untrusted; the client reconnects with its new `device_token` to get the trusted features.

```json
{"v":1,"type":"pair_code","seq":1,"t":1760370000014,"code":"482913","expires_s":300,"required":true}
{"v":1,"type":"pair_request","seq":3,"t":1760370000200,"device_name":"Quest 3"}
{"v":1,"type":"paired","seq":2,"t":1760370031000,"token":"q1mTq3z9yZQ0nI5c1o0m8m3rT9JdY0cB7v1hJ3zK2xA","device":"a1b2c3d4","name":"Quest 3","server_id":"3f9c2a7e1b4d5c6a"}
```

- **`pair_code` (S→C)**: `code` (6 digits, a string), `expires_s` (300), `required` (true: the world waits for it), optional `token_rejected` (the `device_token` in `hello` is unknown: unpaired, or the server lost devices.yml; the client drops it). A new `pair_request` replaces the session's code. A code works once.
- **`pair_request` (C→S)**, optional `device_name`: allowed while waiting (a fresh code when the old one expired) and on an open server's untrusted session. Refused with `error` `pair_denied` (`op: "pair_request"`) when pairing is off (config.yml `auth.pairing`), the headset is already paired, more than 6 requests came from its address in a minute, or 16 headsets are waiting; while waiting, that error is fatal and the server closes with 4003. Without `pair_v1` the answer is `unsupported`.
- **`paired` (S→C)**: `token` (256 random bits, base64url, 43 characters), `device` (8 hex digits, the server's id for this headset), `name`, `server_id`. The token is sent this once; the server stores only its SHA-256 (plugins/TabletopCraftAR/devices.yml) and the client keeps it per server (the Quest app in `pairings.json` in its files folder, keyed by `server_id`).
- **While waiting** the server accepts only `ping`, `pong`, `bye` and `pair_request` (anything else: `bad_message`) and closes after 15 s without a message (4004), so the client pings as usual (the Quest app does from the `pair_code` on). The 5 s hello timeout doesn't apply.
- **`welcome`** (to every client) gains `server_id`: a random id the server keeps (16 hex digits), also in the discovery reply (§7). To `pair_v1` sessions it adds `pairing`: `paired` (bool), `device` and `name` when paired, `required` (whether this server needs a credential), optional `token_rejected`. `welcome.caps` lists `pair_v1` when the server pairs.

```json
"server_id":"3f9c2a7e1b4d5c6a",
"pairing":{"paired":true,"device":"a1b2c3d4","name":"Quest 3","required":false}
```

- **Trusted sessions** may have what the config.yml switches `edit.require-token`, `edit.power-require-token`, `interact.require-token`, `marks.require-token` and `commands.require-token` keep for them (each "requires a token" means "requires a trusted session": the shared token or a paired headset), and the game files ("Asset delivery"). Up to 1.0.x those switches meant "the server has an `auth.token`"; on a server with a token every session is trusted, so that case is unchanged.
- **Revoking.** `/tabletop devices` lists paired headsets (id, name, player, when paired, last seen) and the ones waiting with a code (never the code). `/tabletop unpair <id|name|all>` removes them; a connected one gets `error` `auth_failed` "this headset was unpaired on the server" and close 4003. A client that presented a `device_token` and gets 4003 forgets that token and connects again without it (so it shows a new code) instead of stopping (§5).
- **Security.** The link is plain `ws://`: device tokens travel in clear like the shared token, so this suits a trusted LAN (ARCHITECTURE.md 6). The code itself never leaves the headset and the operator's keyboard; a code is only useful to the headset that asked for it.

**Older peers.** A 1.0.x plugin ignores `pair_v1`, `device_token` and `device_name`: the client sees no `welcome.pairing` and keeps working without pairing (the Quest app says once that a plugin update to 1.1.0 adds pairing and automatic game files, with the releases link). A 1.0.x app never asks for `pair_v1`: on an open server it works as before; on a server with `auth.require-pairing` it gets 4003.

Fixtures `hello_pair.json`, `welcome_pair.json`, `pair_request.json`, `pair_code.json`, `paired.json`, `error_pair_denied.json`, `discovery_reply_pair.json`. Java `PairingTest` (codes, tokens, devices.yml, revoking, limits, the messages against the fixtures), Python `test_pairing_fixtures`, `test_pairing_rejects_bad_fields`, C# `Tests~/SetupTests.cs` (the token store, codes, welcome, old plugins, and LinkClient against a fake server: pings while waiting, an unpaired token). Live: `tools/pair_check.py` (pairs through the server console, then checks the game-file offer and an unpair).

## Chat safety: mute, block, report (next version, 1.2.0; cap `report_v1`)

The headset user can stop seeing a player's chat, and tell the server's operators about a player. The people who can act on a report are the server's operators (kick, ban, whitelist), so reports go to them, not to the app's developer. Docs: QUEST_SETUP.md Part L ("Mute, block and report").

**Mute and block (client-side, no messages).** The app keeps, per server (`welcome.server_id`, else the server's address), the players this headset mutes or blocks, by uuid (`chat.from.uuid`, §4.26). Neither the server nor the player is told. Client guidance (non-normative; what the Quest app does):
- muted: the player's `chat`, `say` and `me` lines and their bubbles are not shown (and make no sound);
- blocked: as muted, and their `whisper` lines (`/tabletop tell`) too;
- `join`, `quit`, `death` and `advancement` lines stay (they say who is on the server), and so do lines without a uuid (the console, plugin broadcasts, other headsets' `operator` lines);
- muting removes the player's earlier lines from the log at once; unmuting doesn't bring them back.

**`report` (C→S)**, needs `report_v1` (granted when config.yml `reports.enabled`, default true; echoed in `welcome.caps`):

```json
{"v":1,"type":"report","seq":70,"t":1760370009000,"player":{"name":"Alex","uuid":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"},"reason":"harassment","details":"kept insulting me in chat","blocked":true}
```

- `player.uuid` (required): a UUID, as in `chat.from.uuid` or `players_info`. `player.name` (optional): the name the headset saw, for the log when the server doesn't know the player.
- `reason` (required): one of `harassment`, `hate`, `sexual`, `threat`, `spam`, `cheating`, `other`. A server takes any other lower-case word (`[a-z_]{1,24}`) as given, so a newer app's reason reaches an older 1.x plugin.
- `details` (optional): the headset user's note, at most 200 characters; the server removes control characters and `§` and collapses whitespace.
- `blocked` (optional, default false): the headset also blocked the player (the Quest app always does).
- Refused with `error` and `op: "report"`: `report_denied` without `report_v1`; `bad_message` for a missing or malformed field; `rate_limited` beyond `reports.max-per-minute` (3) per session; `internal` when the server couldn't save it.

**What the server does.** It appends the report to `plugins/TabletopCraftAR/reports.log` (moved to `reports.log.1` past `reports.max-log-kb`, 1024): an id (8 hex digits), the time, the reporting headset (its device name, paired device id, session and chat name; never its address), the player's name and uuid and whether they are online, offline or unknown to the server, the reason, the note, whether the headset blocked them, and the player's recent chat: the newest `reports.context-lines` (20) lines of the last `reports.context-minutes` (30) they said (`chat`, `say`, `me`, whispers to the headsets), which the plugin keeps in memory per player while reports are on and writes nowhere else. It logs a `REPORT <id> ...` warning on the console and shows a line to every online player with the permission `tabletop.reports` (default: ops) except the reported player. The reported player is not told.

**`report_ack` (S→C)**, once the report is saved:

```json
{"v":1,"type":"report_ack","seq":120,"t":1760370009040,"ref_seq":70,"id":"5e1a9c03","operators":1}
```

`id`: the report's id in reports.log. `operators`: how many online players with `tabletop.reports` were shown it (0: it waits in the log).

**Older peers.** A 1.1.x plugin never grants `report_v1` and answers a `report` with `unknown_type`; the Quest app then greys Report and says the server's plugin is older than 1.2.0 (mute and block still work, they need nothing from the server). A 1.1.x app never asks for the cap.

**The platform's own reporting.** On the Meta Horizon Store, the Quest button's universal menu also has "Report Abuse" (Meta's User Reporting Service, set up in the Developer Dashboard; it sends the report to the app's developer, not to the server). That is outside this protocol; QUEST_SETUP.md Part L and `docs/store/LISTING.md` describe both routes.

Fixtures `hello_report.json`, `welcome_report.json`, `report.json`, `report_ack.json`, `error_report_denied.json`. Java `ReportTest` (parsing and its refusals, the ack and error against the fixtures, the chat context window, the log block, rotation, the rate limit), Python `test_report_fixtures`, `test_report_rejects_bad_fields`, C# `Tests~/SafetyTests.cs` (the fixtures, what mute and block hide, lists per server and in prefs.json, the panel's buttons, list and report picker).

## Block animations (v0.7.0; `actions_v1`, `entities_v1`)

What moves on the table besides blocks changing: container lids, bells, explosions, and a few entity states. No new cap: `actions_v1` sessions get the three action kinds (in the per-tick `actions` message, §4.22) and `entities_v1` sessions the two entity fields.

### `actions` kinds `lid`, `bell`, `explode`

```json
{"v":1,"type":"actions","seq":31,"t":1760370004100,"actions":[
  {"kind":"lid","pos":[107,64,-35],"open":true},
  {"kind":"lid","pos":[109,64,-35],"open":false},
  {"kind":"bell","pos":[104,65,-33],"dir":"east"},
  {"kind":"explode","pos":[110.2,63.0,-30.7],"power":4.0}]}
```

- **`lid`**: integer block `pos`, `open` (bool). Sent when a container's first viewer opens it and when its last viewer closes it (the game's `container_open` / `container_close` game events), for chests, trapped chests, ender chests, copper chests and shulker boxes. Barrels need nothing: their `open` is in the block state. The client moves the lid like vanilla: 0.1 a tick toward open or closed (10 ticks), a chest lid turned by `-(1 - (1 - o)^3)·π/2`, a shulker box lid raised 8 px and turned 270°. A double chest gets one action per half. Repeats at one block within a tick collapse to the last.
- **`bell`**: integer block `pos` and `dir` (`north`, `south`, `east` or `west`): the side the bell was hit on (Paper's `BellRingEvent` direction; for a bell rung by redstone, its facing). The client swings the bell body by `sin(t/π)/(4 + t/3)` for 50 ticks about x (north/south) or z (east/west).
- **`explode`**: `pos` (the explosion's point, doubles) and `power` (vanilla's explosion power, which Bukkit's events don't carry: TNT and TNT minecarts 4, creepers 3 (charged 6), end crystals 6, the wither's birth 7, fireballs and wither skulls 1, beds and respawn anchors 5, anything else 3). Not sent for wind charges. Also sent a little outside the region (6 blocks), so a cloud at its edge shows. The client draws the explosion clouds (power 2 and up: 48 clouds within 4 blocks over 8 ticks, scaled with the power; less: one) and a flash; the broken blocks still come as `break` actions (at most 48).

### `entities` fields `fuse` and `swell`, and the poses drawn

```json
{"id":401,"type":"minecraft:tnt","pos":[120.5,64.0,-25.5],"yaw":0.0,"pitch":0.0,"size":[0.98,0.98],"fuse":37}
{"id":402,"type":"minecraft:creeper","pos":[122.5,64.0,-25.5],"yaw":90.0,"pitch":0.0,"size":[0.6,1.7],"eye":1.45,"swell":0.5}
```

- **`fuse`** (int, primed TNT only): ticks left before it explodes. The client counts it down by itself at 20 a second; the server sends it again only when it has moved 10 ticks or more since the last time (so twice a second). The TNT swells by up to 30 % over its last 10 ticks and turns white while `fuse / 5` is even (TntRenderer).
- **`swell`** (0..1, 2 decimals, creepers while swelling): the game's swelling (`swell / (maxSwell - 2)`), sent when it moved by 0.05 or more, and 0 when it calmed down. The creeper grows `(1 + g·0.4)·h` across and `(1 + g·0.1)/h` up (`g = swell⁴`, `h` a small wobble) and blinks white every other tenth (CreeperRenderer).
- **Poses** (the existing `pose` field, §4.21): `dying` (a mob at 0 health; the server removes it 20 ticks later) tips the mob over by `sqrt((t - 1)/20·1.6)` of 90° (spiders 180°); `sleeping` lays a mob in the bed under it (its head at the headboard; no bed, no change); `sitting` folds tamed wolves and cats as vanilla does.

Fixtures `actions_motion.json`, `entities_motion.json`. Java `MotionTest` (the batch and the delta against the fixtures, fuse and swell resends), Python `test_motion_messages`, C# `MotionTests` (parsing, and the curves against the 26.3 numbers).
