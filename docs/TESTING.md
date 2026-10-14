# TESTING.md: what was verified, and how

> **Server version.** Sections 1-4, "Region resize e2e", "Real terrain" and the `--live` and `e2e.sh` runs under "C# client tests" were measured on Paper 1.21.11 build 132 with Temurin JDK 21, before the plugin moved to Paper 26.3 / Java 25. Their numbers are kept as recorded. Paper 26.3 results: section 6.
>
> Two `server.properties` defaults differ on a 26.x test server: `white-list` is true on a new server (set it to false or `whitelist add` the test players), and `level-type=minecraft:flat` needs explicit `generator-settings` layers (an empty value fails with "No key layers").

Run on a Linux box on 2026-10-13 (no GPU, no headset): Temurin JDK 21.0.12, Paper **1.21.11 build 132** (latest stable 1.21.11 build from the PaperMC Fill v3 API), offline mode, flat world, mineflayer 4.39.0 (`version: "1.21.11"`), Python 3 + websockets 17.2, .NET 8 SDK. The EULA was accepted locally for this test only; `eula.txt` and the server jar are git-ignored (`tools/run/`).

Reproduce: `tools/e2e.sh` (needs JDK 25 since the move to 26.3, Node, `pip install -r tools/requirements.txt`).

## 1. Unit tests

| Suite | Command | Result |
|---|---|---|
| Plugin (JUnit 5) | `cd server-plugin && ./gradlew build` | 15 tests, 0 failures (BlockCodecTest 5, PoseTest 7, FixtureTest 3) |
| Fake client (pytest + ruff) | `cd tools && ruff check . && pytest -q` | 17 passed, ruff clean (includes all 13 PROTOCOL.md example messages in `tools/fixtures/`) |
| Quest scripts, engine-free (.NET 8) | `cd quest-app/Tests~ && dotnet run` | 12/12 PASS: JSON parse/write, RLE decode + bad total rejected, greedy mesher (solid 4^3 cube = 6 quads, checkerboard = 48 quads, touching cubes = 6, glass pair = 6 transparent, poppy = cross only), Unity->tabletop frame mapping, block_update palette_add, stale snapshot drop |

## 2. End-to-end on a real Paper server (`tools/e2e.sh`)

Setup through the server console: `tabletop setorigin 0 -64 0` (region 32x64x32 from the flat world's bedrock), `fill 0 -60 0 31 -60 31 stone`, `setblock 5 -59 5 gold_block`, `setblock 10 -59 20 oak_stairs[facing=east]`, `setblock 20 -59 20 torch`, `gamerule random_tick_speed 0`. Then the fake Quest connects for 25 s (`--avatar`, 30 Hz scripted head circle 0.45 m above the table centre, hands ±5 cm), a mineflayer bot `TestWalker` joins and walks into the region and places cobblestone, and the console does `setblock 7 -59 7 diamond_block` and `setblock 5 -59 5 air` mid-run.

Fake Quest report (`tools/run/fake_report.json`):

```
counts {'welcome': 1, 'pong': 13, 'chunk_snapshot': 1, 'avatar_state': 51, 'player_positions': 197, 'block_update': 2}
snapshot {'id': 1, 'origin': [0, -64, 0], 'dims': [32, 64, 32], 'bytes': 510, 'palette': 8, 'runs': 11, 'at_ms': 199}
players {'TestWalker': {'first': [-0.5, -60.0, -5.5], 'updates': 196, 'last': [4.5, -59.0, 19.82]}}
head_range {'x': [6.16, 25.85], 'y': [-19.0, -19.0], 'z': [6.15, 25.85], 'samples': 50}
rtt {'n': 13, 'min': 0, 'max': 8} poses 722 ok True
  OK  (7,-59,7)   want diamond_block  got minecraft:diamond_block      (console setblock during the run)
  OK  (5,-59,5)   want air            got minecraft:air                (gold block removed during the run)
  OK  (10,-59,20) want oak_stairs     got minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]
  OK  (20,-59,20) want torch          got minecraft:torch
  OK  count stone = 1024, bedrock = 1024, cobblestone = 1 (placed by the mineflayer player)
updates [[5, 5, 5, 'minecraft:air'], [7, 5, 7, 'minecraft:diamond_block'], [5, 5, 19, 'minecraft:cobblestone']]
```

- **Chunk snapshot correct**: every expected block and count matched the console-placed world (world coords via `origin + local`, yzx order, `[paletteIndex, runLength]` pairs).
- **Block updates**: console `/setblock` (caught by the 20-tick safety diff) and a player placement (caught by `BlockPlaceEvent`, applied next tick) both arrived as `block_update` with region-local coordinates and `palette_add`.
- **Live player positions**: 196 `player_positions` entries for the mineflayer player at 10 Hz, from outside the region (-0.5, -5.5) to inside it (4.5, 19.82).
- **Avatar follows poses**: the head moved over x 6.16..25.85 and z 6.15..25.85 at y -19.0, exactly `origin + p / 0.01` for the scripted 0.10 m circle around (0.16, 0.45, 0.16). Server log:

```
[TabletopCraftAR] Avatar spawned: scale 16.00 (scale clamped from 97.2 to 16.0)
AVATAR_STATE {"enabled":true,"scale":16.0,"world_height_blocks":28.8,"note":"scale clamped from 97.2 to 16.0",
  "debug":{"feet":[16.68,-44.92,6.02],"yaw":3.9,"pitch":0.0,"head":[16.68,-19.0,6.02],"left":[11.68,-31.408,6.02],"right":[21.68,-30.592,6.02],"poses_applied":290,"pose_seq":447}}
AVATAR_ENTITY MANNEQUIN id=1 16.68 -44.92 6.02 yaw=3.9
AVATAR_ENTITY ITEM_DISPLAY id=2 11.68 -31.41 6.02 yaw=0.0
AVATAR_ENTITY ITEM_DISPLAY id=3 21.68 -30.59 6.02 yaw=0.0
```

  Feet = head - 1.62 x 16 = -44.92. Hands sit ±5 blocks (±5 cm / 0.01) from the head. Yaw checks out against the look-at-centre direction (e.g. head at (24.18, 21.76) gives Minecraft yaw 125.1°, matching atan2 of the direction to (16,16)). About 20 poses/s are applied out of 30 sent: the latest-pose-per-tick slot drops the rest, as designed.
- **Scale attribute works on Mannequin**: `attribute <uuid> minecraft:scale get` -> `The value of attribute Scale for entity Tabletop Giant is 16.0`, and the `entity_update_attributes` packet a client receives carries the value 16. (mineflayer's minecraft-data labels 1.21.11 attribute ids wrongly, so its decoded names are off by one; the raw value is what was checked.) No fallback to the ItemDisplay head was needed.
- **Second player sees the giant** (mineflayer `TestWalker`, offline auth):

```
ENTITY_SPAWN mannequin id=1 (21.69, -44.92, 24.22)
ENTITY_SPAWN item_display id=2 (16.69, -28.19, 24.22)
ENTITY_SPAWN item_display id=3 (26.69, -33.81, 24.22)
ENTITY_MOVED mannequin (20.27, -44.92, 25.04) yaw=0.44 ... (26 sampled moves over 18 s)
PLACED cobblestone at (5, -59, 19)
SUMMARY seen={"1":"mannequin","2":"item_display","3":"item_display"}
```

## 3. Consistency and error paths

`tools/consistency_check.py` keeps a live mirror from snapshot + updates while the console runs `fill` (glass 4x3x4), `setblock`, and `fill` (planks 3x3), then compares it with a fresh snapshot:

```
block_update msgs=3 changes=59; live-vs-fresh differing cells: 0/65536; glass=47 planks=9 at(0,-58,0)=minecraft:air
```

(With random ticks on, grass under the stone layer decays to dirt; those changes also streamed correctly as `block_update`s in an earlier run.)

`tools/error_paths.py`:

```
wrong path       [('closed', 1008, 'wrong path, use /tabletop')]
version mismatch [('error', 'version_mismatch', None), ('closed', 4001, 'version_mismatch')]
no hello first   [('error', 'bad_message', None)]
subscribe 128    [('welcome',..), ('chunk_snapshot', dims [32, 64, 32]), ('error', 'region_too_large', None)]
subscribe ok     [('welcome',..), ('chunk_snapshot', dims [32, 64, 32]), ('chunk_snapshot', dims [16, 8, 16])]
unknown type     [('error', 'unknown_type', None), ('welcome', ..), ('chunk_snapshot', ..)]
hello timeout    [('closed', 4002, 'hello timeout')]   (after 5.3 s)
```

## 4. Quest C# against the live plugin

`quest-app/Tests~` compiles the real `LinkClient.cs` (System.Net.WebSockets), `Protocol.cs`, `WorldModel.cs` and `GreedyMesher.cs` and runs a session against the plugin:

```
PASS live: snapshot received dims=(32, 64, 32) palette=11
PASS live: meshed 16 sections into 144 quads
PASS live: ping/pong rtt 36 ms
PASS live: avatar_state enabled=True scale=16
live: poses sent 239, player msgs 0, block updates 0
ALL PASS
```

## 5. Unity build (Linux, Unity 6000.0.84f1, 2026-10-13)

Headless, with `tools/patch-meta-sdk-linux.sh` and `TabletopBatch` (see QUEST_SETUP.md Part B, "Linux and command line"):

- **Compile**: Assembly-CSharp and Assembly-CSharp-Editor compile with 0 errors on the first open, once the missing engine modules are in `manifest.json`. The only warning in our code is CS0618 for `OVRPassthroughLayer.overlayType`.
- **Setup**: the scene builder threw a NullReferenceException (`MRUK.SceneSettings` is null on a component added from code); fixed. Fix All applies the usual Quest settings: API 32/34, Vulkan, linear colour, ASTC, IL2CPP ARM64, OpenXR Touch profile, and OVRManager requesting the Scene permission on startup. The scene is now saved after Fix All; before that, the OVRManager fixes were lost. A second Setup pass changes nothing else.
- **APK**: builds in about 2 minutes clean (45 MB, `com.tabletopcraft.ar`, arm64-v8a). Its manifest has INTERNET, USE_SCENE, USE_ANCHOR_API, PASSTHROUGH required, `quest3|quest3s` and `usesCleartextTraffic=true`. Meta's `enableNSCConfig` (on by default) had added a `network_sec_config.xml` with `cleartextTrafficPermitted="false"`, which would override that, so the builder now turns it off.
- **Build contents** (from the build log): TabletopCraft/Blocks, BlocksTransparent, Unlit/Color (Marker.mat) and Sprites/Default are compiled in. Standard is not, so `CreatePrimitive` objects now get the unlit marker material. The TextMesh font (`LegacyRuntime.ttf`) and its shader ship in the built-in "unity default resources".

## First headset session (Quest 3, 2026-10-13)

Quest 3 on Android 14-based Horizon OS, USB for install and logcat, Wi-Fi for the link. The PC ran the plugin on Paper 26.3 (seed 12345, a 64x96x64 area at y 32..128 around 0,0) and the APK from section 5. `pack.zip` was the user's 26.3 client jar.

- **Works on the headset**:
  - Passthrough, and the link to the server (`welcome ... mc 26.3`, RTT 13-14 ms in the log, up to 42 ms on the status panel).
  - The 64x96x64 snapshot (palette 43), textured from the jar. `pack loaded`, so `System.IO.Compression` works on IL2CPP.
  - `USE_SCENE` granted on first launch.
  - VrApi `FPS=72/72` with ~2.4-2.6 ms app GPU time. Passthrough rendered at 72 fps.
  - A Java player shown on the table as a marker.
  - The giant followed the headset in a Java client, wearing the user's skin once the server was in online mode (`skin_player` set in tabletop.json; exact placement).
  - The demo media in `docs/media/` comes from this session's headset recording. It's cropped and blurred so no screens or usernames show.
- **Bugs found on the headset and fixed the same day**:
  - **Meshing race:** a few sections failed with NullReferenceException in the first meshing batch, so terrain was missing. Fixed by making the shape and face caches concurrent; Tests~ has a regression check.
  - **avatar_config spam:** the app sent `avatar_config` every frame while scaling, and the server respawned the giant each time (1,193 respawns in a few minutes). The app now debounces it, and the server rescales in place.
  - **Giant deleted outside the area:** stepping back from the table or shrinking the miniature moved the giant out of the area into unloaded chunks, which deleted it (26.x has no spawn chunks). It now only moves into loaded, ticketed chunks.
- **Setup notes**:
  - The USB debugging prompt didn't appear at first. A headset restart with the cable in, then "Always allow", fixed it.
  - Unity's batch builds kill the adb server, so expect to re-approve until "Always allow" sticks.
- **Not yet checked on the headset**:
  - MRUK table detection. The log has "MRUK Shared: Unable to bind OpenXR function" warnings, and the table was placed with A.
  - Area setup.
  - Live block edits.
  - Everything added after this session: water, animation, lighting, block models, navigation, player figures.

## 6. Paper 26.3 (Java 25, 2026-10-13)

Paper 26.3 build 181 (stable), Temurin JDK 25.0.4.1, plugin built against `paper-api 26.3.build.181-stable` with Gradle 9.8.0 / Shadow 9.6.1. No plugin source changes were needed.

- **Unit tests**: 21 JUnit tests, 0 failures. pytest 23 passed, ruff clean.
- **`tools/e2e.sh`** (fresh flat world, exit 0). mineflayer 4.39.0 can't speak 26.3, so the test server gets ViaVersion + ViaBackwards 5.12.0 and the bot joins as 26.1. 26.x also rejects a second movement packet in a tick until the client sends `tick_end`, which mineflayer never does (kicked with `invalid_player_movement`); the bot now sends it.
  ```
  counts {'welcome': 1, 'pong': 13, 'avatar_state': 51, 'chunk_snapshot': 1, 'player_positions': 199, 'block_update': 2}
  snapshot dims [32,64,32] bytes 577 palette 8 runs 11 at_ms 251; welcome mc_version '26.3'
  rtt_ms min 0 max 1, poses_sent 732, ok True
  OK (7,-59,7)=diamond_block  OK (5,-59,5)=air  OK oak_stairs  OK torch  OK stone=1024  OK bedrock=1024  OK cobblestone=1
  consistency: block_update msgs=3 changes=59; live-vs-fresh differing cells: 0/65536
  error paths: wrong path 1008, version_mismatch 4001, bad_message, unknown_type, hello timeout 4002 (subscribe 128x64x128 is within the default 128 cap)
  bot: ENTITY_SPAWN mannequin + 2 item_display, ENTITY_MOVED, PLACED cobblestone at (5,-59,20)
  AVATAR_STATE scale 16.0 (clamped from 97.2), world_height_blocks 28.8, poses_applied 287
  ```
- **`tools/region_e2e.py`**: ok: true. Over-cap requests (256², 129x64x128, 128x384x128) are rejected with `region_too_large`.

  | dims | bytes | runs | async walk ms | main-thread snapshot ms | request→snapshot ms | client decode ms |
  |---|---|---|---|---|---|---|
  | 16x64x16 | 548 | 23 | 1.8 | 0.04 | 191 | 0.1 |
  | 32x64x32 | 555 | 23 | 4.2 | 0.05 | 149 | 0.6 |
  | 64x64x64 | 579 | 27 | 16.9 | 0.10 | 148 | 2.3 |
  | 128x64x128 | 677 | 33 | 57.3 | 0.42 | 243 | 7.3 |

  Runs are higher than in the 1.21.11 table because the `consistency_check` glass at y=-58/-57 survives the documented world prep (it only clears y=-59).
- **26.x differences** that needed tooling changes: Java 25, `white-list=false`, explicit flat `generator-settings` layers, and worlds now keep every dimension under `world/dimensions/`.
- **Resource pack (26.3 client jar), offline**: a Python mirror of `ResourcePackAtlas.Resolve` over all 1,288 block states found two gaps, both fixed. 26.x models may give a texture as `{"sprite": ..., "force_translucent": true}` (107 models: glass, stained glass and panes, redstone), and fluids only name a `particle` texture. Now 940 states resolve all six faces, including grass, stone, logs, leaves, ores, sand, glass and water. The ~95 that resolve none are mostly entity-rendered (chests, banners, heads, shulker boxes, decorated pots) or invisible, and keep flat colours. Cross plants use the north face, which resolves.

## 7. Giant placement, giant skin, player figures (2026-10-13)

Paper 26.3 build 181 test server (offline mode, seed 12345, region 64x96x64 from (0, 32, 0)), JDK 25, .NET 8, Unity 6000.0.84f1 batch mode. No headset, no Java client.

- **Unit tests**: plugin 33 JUnit tests, 0 failures (new: `GiantPlacementTest` 8, `SkinLookupTest` 3, `FixtureTest` +1 for `skin_model`). pytest 24 passed, ruff clean. `Tests~` 68/68 PASS (35 new: model geometry and UVs, winding, slim arms, animation, sneak pose, legacy skin conversion, default skin per UUID checked against the JDK's `UUID.hashCode`, skin URL filter, `PlayerInfo.From` on the fixture). `Tests~ --live` against the test server: all PASS.
- **Ground placement, live** (a scripted client in place of the headset, block size 0.01, `debug.avatar-state: true`). The user stands 0.4 m west of the table, head 0.6 m up, looking east, hands 0.3 m forward, 0.45 m down and 0.2 m to each side:
  ```
  placement ground: feet [0.5, 63.0, 32.0] (clamped to the region's west edge, on the ground; exact would be x -40, feet y 66)
    eye y 88.92 = 63 + 1.62 x 16; yaw -90 (east); body_yaw -40 (lags the head by 50)
    hands [5.44, 81.51, 28.71] / [5.44, 81.51, 35.29] = eye + (0.3, -0.45, -/+0.2) m x 28.8/1.75 blocks per metre
  walking along the south edge, looking north: feet x 0.5 -> 63.5 over 6 s at z 63.5, y 63.0; right hand east of the body
  ```
  `exact` (via `/tabletop avatar placement exact`) maps the head to x -40 / y 92, outside the region. Before this change the giant never moved there on a server with no player nearby. The chunk loaded by `getChunkAtAsync` unloaded again before the next tick, and the giant waited for it forever. Async-loaded chunks are now ticketed when the load completes, and the same run shows the giant at (-40, 66.08, 32) and following the walk.
- **Giant skin, offline-mode server**: `skin_player` set to a real Java account name gives `SKIN resolved <name> -> <uuid> (wide)` 0.25 s after spawn, then `avatar_state` with `skin_resolved: true`. The Mannequin switched to the signed static profile. A name with no account gives `skin_failed` ("no Java account named ...") and `skin_resolved: false` with a note.
- **Unity**: 0 `error CS`, no new warnings in `Assets/` (the only one is the existing CS0618 in the scene builder), no shader errors. A temporary editor script (not committed) checked these:
  - `Resources/TabletopPlayer.mat` loads with `TabletopCraft/PlayerSkin` (supported, no errors).
  - The default skin for the offline UUID of "Steve" comes from the 26.3 jar as `wide/efe.png`, matching `DefaultPlayerSkin`. Without a pack it falls back to the plain-colour skin.
  - A figure builds with 6 parts and 288 vertices, and the sneak pose puts the head pivot at 17.8 px.
  - A real profile skin URL (`http://textures.minecraft.net/...`) is fetched over https through `UnityWebRequestTexture`, converted from legacy 64x32 to 64x64, point-filtered, and not readable on the CPU after upload. A non-Mojang URL is refused.
- **Offline render of the generated meshes** (a Python rasteriser of the `PlayerModel` output with the 26.3 jar's Steve and Alex skins; front, side, back and top views): faces, hair, sleeves and trousers land on the right boxes. The side view shows the head pitched down, the legs and arms counter-swinging, and Alex's sneak pose (lean, lowered head, legs back).

Still needs the headset and a Java client: the figures' look on the table (size at 1 cm/block, shading, label), skin downloads on the Quest (https from the headset), the giant's movement as seen by Java players (Mannequin interpolation plus server smoothing), and `skin_model`/`skin` from an online-mode server.

## Not verified (and why)

- **On the Quest** (see "First headset session"): MRUK table detection, area setup and live block edits haven't been checked yet.
- **Block models on the Quest**: they are checked offline only, with tests on the 26.3 jar and the offline renderer ("Block models from the resource pack").
- **The giant in a Java client**: seen once, with exact placement and the user's skin. Ground placement, hand orientation and smoothness haven't been looked at by eye.
- **Player skins on the table**: `player_positions` skins only exist on an online-mode server. The figures that use them haven't been on a headset yet.
- **Windows**: the server steps in QUEST_SETUP.md are written for Windows but were run on Linux.

## Spec deviations (PROTOCOL.md v1, known and intentional for v0.1)

- The region walk into palette ids runs on the main thread (fine at 32x64x32: about 1-2 ms); RLE encoding and JSON serialisation are off-thread on a single ordered outbound executor.
- The server never initiates `ping` (it answers client pings). It doesn't enforce `rate_limited`; the latest-pose slot simply drops excess poses.
- `follow` (subscribe) and `skin_url` (avatar_config) reply `error unsupported`. `model` (wide/slim) is stored but not applied.
- The 64 KiB inbound limit is checked on the decoded text length.
- `avatar_state` carries an extra `debug` object (entity positions) when `debug.avatar-state: true`. v1 peers ignore unknown fields.
- `avatar_state` also goes out when the avatar auto-spawns on the first pose (`avatar.spawn-on-first-pose`, a convenience for clients that never send `avatar_config`).

## Region resize e2e (tools/region_e2e.py, flat world, async reader)

The script expects more world than `e2e.sh` prepares. With the e2e server running, type these in its console first (no player is online, so force-load the area):

```
forceload add -64 -64 63 63
fill -64 -59 -64 63 -59 63 minecraft:air
fill -64 -60 -64 63 -60 63 minecraft:stone
setblock 3 -59 5 minecraft:gold_block
setblock -60 -59 60 minecraft:diamond_block
setblock 60 -59 -60 minecraft:emerald_block
forceload remove all
```

Re-run on 2026-10-13 after the e2e world prep: `ok: true`. All six resizes (16→32→64→128→64→16) matched both clients, the three over-cap requests were rejected, and the 128x64x128 request→snapshot took 238 ms with a 67 ms async walk.

| dims | bytes | runs | server read ms (async walk) | main-thread snapshot ms | request→snapshot ms | client decode ms |
|---|---|---|---|---|---|---|
| 16x64x16 | 456 | 7 | 3.2 | 0.17 | 191 | 0.2 |
| 32x64x32 | 463 | 7 | 7.6 | – | 194 | 1.1 |
| 64x64x64 | 466 | 7 | 25.9 | 0.53 | 197 | 4.6 |
| 128x64x128 | 545 | 11 | 157.2 | 1.70 | 292 | 18.1 |

- Requests over the cap (256², 129, 128x384x128) are rejected with `region_too_large`.
- The two clients' grids mirror each other exactly.
- The per-session diff walk now runs off the main thread too (before, a sync read of 128x64x128 blocked the main thread for 164 ms).
- Snapshot bytes on a flat world are tiny. Real terrain will be much larger, and that hasn't been measured yet.

## Real terrain (normal world, seed 12345, centred on 0,0; `tools/measure_regions.py` + `Tests~ --measure`)

These are the second runs, with chunks already generated. On the first run, chunk generation added up to 750 ms of `chunk_load` for the 128² request; that work happens off the main thread. In every run, main-thread time per resize (`snapshot_main_ms` in REGION_SET) stayed under 3 ms.

| region (x×y×z, y range) | snapshot bytes | palette | RLE runs | server walk (async) | request→snapshot | client decode (Python) |
|---|---|---|---|---|---|---|
| 32×96×32 (y 32..128) | 16 KB | 29 | 3,277 | 25 ms | 188 ms | 4 ms |
| 64×96×64 (y 32..128) | 64 KB | 41 | 13,838 | 69 ms | 263 ms | 11 ms |
| 128×96×128 (y 32..128) | 234 KB | 53 | 51,606 | 289 ms | 465 ms | 29 ms |
| 128×128×128 (y 0..128) | 728 KB | 139 | 153,960 | 416 ms | 657 ms | 41 ms |
| 32×384×32 (full height) | 100 KB | 102 | 20,497 | 68 ms | 197 ms | 9 ms |
| 64×384×64 (full height) | 407 KB | 148 | 81,752 | 291 ms | 449 ms | 26 ms |

Greedy-mesher output for the same snapshots: the real `GreedyMesher.cs`, flat-colour face keys, run on the x86 box.

| region | greedy quads | special quads (cross/slab/etc.) | total vertices | triangles | worst section | full mesh time (box) | `AreaMath.Estimate` |
|---|---|---|---|---|---|---|---|
| 32×96×32 | 2,343 | 526 | 11.5k | 5.7k | 480 quads | 16 ms | 3.6k |
| 64×96×64 | 9,320 | 3,476 | 51k | 26k | 959 | 64 ms | 14.3k |
| 128×96×128 | 32,993 | 13,318 | 185k | 93k | 1,268 | 254 ms | 57k |
| 128×128×128 | 68,093 | 14,816 | 332k | 166k | 1,268 | 510 ms | 76k |
| 32×384×32 | 10,909 | 1,408 | 49k | 25k | 954 | 66 ms | 14k |
| 64×384×64 | 39,394 | 7,242 | 187k | 93k | 998 | 262 ms | 56k |

With a resource pack, faces only merge when they share a texture, and since the block-model work every non-cube block is drawn from its model. The measured counts are in "Block models from the resource pack" below; the capacity guidance uses them.

### Quest 3 capacity guidance (ESTIMATES, not measured on a headset)

- **Estimator** (`AreaMath.Estimate`, used for the soft cap): recalibrated for block models to about 5.5 faces per column for a 96-block surface band, plus 0.04 faces per block below that band (caves). It overestimates every measured pack case except 128×128×128, which it underestimates by about 5%. (Before block models: 3.5 and 0.035.)
- **Default soft cap:** 250k faces = 1M vertices / 500k triangles. This assumes the scene is only the unlit block shader at 72 Hz. The headset shows a warning above the cap; holding B overrides it.
- **Recommended sizes** (pack counts):
  - **64×96×64**: comfortable everywhere (about 21k faces).
  - **128×96×128**: the recommended maximum for normal play (74–79k faces measured at spawn, in a village and in snowy plains).
  - **128×128×128 / 64×384×64**: fine for rendering (about 63–117k faces). Snapshots are 0.4–0.7 MB.
  - **128×384×128** (full height): estimated at about 280k faces / 1.1M vertices (4× the measured 64×384×64 is about 250k). That is over the soft cap, so it needs the B override, and it is over the server's default `max_blocks` (2,097,152), so the server operator would have to raise it.
- **Meshing on the headset:** with block models the box meshes about 0.4–0.5 ms per 16³ section (under 1 ms worst case). Assuming the Quest's mobile CPU is 3–5× slower (estimate), a 128×96×128 re-mesh is about 0.6–1 s spread across 3 worker threads. The main thread only uploads meshes, within a 3 ms per-frame budget.

## Block models from the resource pack (26.3 client jar, 2026-10-13)

Engine-free, run on the x86 Linux box: .NET 8, the user's 26.3 client jar passed with `--jar` (or `MC_CLIENT_JAR`), never copied into the repo. The live data came from a local Paper 26.3 build 181 test server: normal world, seed 12345, offline mode.

- **Tests:** `cd quest-app/Tests~ && dotnet run -- --jar CLIENT.jar`: **68/68 PASS**. Without a jar, 34/34 PASS and the model tests are skipped. The model tests cover:
  - Full cubes: stone, grass (tinted top, dirt bottom), and `oak_log[axis=x]`, which has its ends west/east and rotated bark (`UvMap`).
  - Stairs: 11 faces, with the step moving with `facing` and flipping for `half=top`. Cullfaces rotate with the model. Inner stairs have 15 faces.
  - Multipart: fence post + north arm (16 faces), the east arm, a wall (post + low + tall), and redstone (dot / line / corner / climbing via `OR` and `side|up`, colour follows power). Glass panes are translucent through 26.x `force_translucent`.
  - Rotated elements: the wall torch leans off its wall, the poppy is a 4-face cross, and a hanging sign uses 26.x `x`/`y`/`z` rotation.
  - Heights: a snow layer is 6/16 high with its inner helper face skipped; snow at 8 layers is a full cube.
  - Block-entity boxes: single and double chests, banner (wool flag), skull, bell body, shulker box. 26.x bed and sign models bake. Powder snow stays a cube, and a spawner doesn't.
  - Classification after prewarm.
  - Meshing: stairs on stone = 6 + 10 faces (bottom culled), two snow layers = 8 faces, joined glass panes drop the faces between them, and the greedy east face of grass is upright.
- **Every blockstate** (`dotnet run -- --jar CLIENT.jar --list-shapes`, first variant of each of the 1,288 states): 404 stay greedy cubes (5 of them were cross placeholders before, such as mushroom stems), 33 stay see-through cubes (glass, leaves, ice), and 9 opaque-by-name cubes become see-through cubes (copper grates, frosted ice). 833 become block models: 310 that were drawn as full cubes before (stairs, beds, chests, cauldrons, anvils, paths, farmland...), 403 cross placeholders (plants, torches, fences, walls, rails, doors...), 101 slabs, and 19 glass panes / iron bars / honey / slime. Air and fluids are unchanged.
- **Face counts** (`tools/measure_regions.py` + `dotnet run -- --jar CLIENT.jar --measure DIR`). "flat" is the mesher with flat-colour keys and the old placeholders (no pack). "pack" is greedy cubes with one texture layer per texture, plus block models.

| region (y range) | flat faces | pack faces | of which model faces | `AreaMath.Estimate` (new) | pack mesh time, all sections |
|---|---|---|---|---|---|
| 32×96×32 spawn (32..128) | 2,676 | 4,451 | 1,926 | 5.6k | 9 ms |
| 64×96×64 spawn (32..128) | 12,811 | 20,657 | 10,510 | 22.5k | 38 ms |
| 128×96×128 spawn, mostly ocean (32..128) | 45,470 | 73,692 | 38,253 | 90.1k | 148 ms |
| 128×96×128 village at -144,-832 (32..128) | 59,638 | 78,603 | 22,391 | 90.1k | 183 ms |
| 128×96×128 snowy plains at 672,-608 (32..128) | 67,072 | 76,009 | 29,583 | 90.1k | 170 ms |
| 128×128×128 spawn (0..128) | 82,224 | 116,715 | 42,835 | 111.1k | 226 ms |
| 32×384×32 spawn (full height) | 12,138 | 15,556 | 4,202 | 17.4k | 82 ms |
| 64×384×64 spawn (full height) | 47,004 | 62,710 | 22,461 | 69.7k | 178 ms |

  Every face is 4 vertices and 2 triangles. Model faces are single-sided, while the old cross placeholders were double-sided, so triangles grew less than faces: 128×96×128 spawn went from 115k to 147k triangles. Meshing is 0.4–0.5 ms per 16³ section, with the worst section under 1 ms. Outliers of 7–9 ms on the first sections are .NET JIT warm-up. Baking all palette states (main thread, prewarm) took 45–53 ms for 110–171 states. That includes the test reader's PNG decoding. On the headset, the atlas's texture loading for new states already ran in the same step.
- **Where the model faces come from:** underwater plants dominate the ocean (seagrass and tall seagrass 8 faces per block, kelp 4). In the village, the main sources are short grass (4), leaf litter (about 2.6), dirt paths (about 3.7, 15/16 high), pink petals (about 27) and stairs (about 5). In snowy plains, 10,136 snow layers cost 2.2 faces each after side culling against each other.
- **Offline visual check:** `dotnet run -- --jar CLIENT.jar --scene OUT.json` dumps a demo scene of about 90 odd blocks (or `--snapshot FILE x0,y0,z0,dx,dy,dz` crops a saved region). Then `uv run --no-project --with pillow --with numpy python tools/render_mesh.py OUT.json CLIENT.jar OUT.png --view se|nw [--box ...]` renders it. The renderer follows the shader rules: layer texture with repeat, tint, face shading, alpha test for the opaque submesh and blending for the transparent one. Checked from both diagonal views:
  - stairs (all facings, upside down, corners) with world-aligned plank tops (uvlock), slabs, fences, walls, panes, iron bars and fence gates;
  - logs on all three axes, furnace fronts upright, hay bales;
  - standing, wall and hanging torches and lanterns, flowers, tall grass, crops, sugar cane, berry bushes, potted plants, carpets, rails, redstone (red);
  - doors and trapdoors, ladders, vines, snow layers 1–8, dirt path, farmland, cauldron, hopper;
  - chests (double chests join), banner, skull, bed, sign, bell, anvil, enchanting table, lectern, campfire, decorated pot, brewing stand and shulker box;
  - a crop of the real village (stair roofs, farm, paths, lamp posts).

  Grass sides keep the fringe up on all four sides. Before, east/west greedy faces had it sideways.
- **Needs the headset:** frame rate at 75–80k faces for 128×96×128, the prewarm hitch on the Quest CPU when a new region's palette is baked, and the look of face shading on rotated faces. Plants come out at 0.8 brightness from their diagonal normals, where vanilla draws them at full brightness.

## C# client tests (`cd quest-app/Tests~ && dotnet run`)

- **33/33 PASS.** This adds tests for `AreaMath` (block-size fit, clamping, the estimate, soft cap and override, clamping to server caps), `AreaSetupState` (centre on origin/player, resize clamps, request matches the fixture, warn/override), and `region_info`/`region_too_large` fixture parsing.
- **`--live`** against the real-terrain server: all checks PASS, including the area-setup resize. The live test uses the same `AreaSetupState`: it requests +16 x/z, which the server's `max_blocks` limit clamps to 80×327×80, and gets back `region_changed` from its own session plus a snapshot with the new dims.
- **`tools/e2e.sh`:** re-run after the async reader change and PASS. The block_update check found 0/65536 cells differing between the live and fresh snapshots, error paths returned the expected codes, and the avatar entities spawned. The earlier hang was caused by a stray test server still holding ports 25565/8765. The script now checks those ports before it starts and exits with an error if either is in use.

## Water, waterlogging and animated textures (Quest mesher and atlas, 2026-10-13)

What changed: `World/FluidMesher.cs` draws water and lava, including the water inside waterlogged blocks and in kelp, kelp_plant, seagrass, tall_seagrass and bubble_column cells (the 26.3 blocks that always hold water, found by listing the client's `getFluidState` overrides). It culls faces between cells that hold the same fluid and uses vanilla surface heights (a source is 8/9, flowing water is amount/9, corners are averaged with the neighbours). `World/TextureAnimation.cs` parses `<texture>.png.mcmeta`, and `ResourcePackAtlas.Animation.cs` advances animated textures at 20 ticks/s by GPU-copying single frames into their atlas layers.

**Engine-free tests** (`Tests~/WaterTests.cs`, part of `dotnet run`; all PASS, 95 checks with master's player tests merged):
- Water next to kelp makes 6 merged water quads and none between the cells. The kelp cross is still drawn, and it comes before the water in the transparent index list, so the water blends over the plant.
- A kelp_plant inside 3×3×3 water: the cross plus 10 merged outer water quads, no faces around the kelp cell. All water quads wind counter-clockwise seen from outside.
- A two-high water column: the top is lowered to 1 + 8/9, with no face between the cells. A 16×4×16 ocean merges into 10 quads.
- `water[level=1]` next to air slopes from about 0.88 to about 0.39 (vanilla corner averaging).
- Waterlogged bottom slab: slab box plus 5 water quads; the slab's own bottom face hides the water's. Waterlogged top slab: its top hides the water surface.
- Waterlogged stairs hold water and get no face from the water beside them. While `BlockRules` still meshes stairs as an opaque cube, the cube hides that water. With a real stair shape the cell fills, and the back and bottom faces are culled. `FluidRules.FullFaces` is the hook for that.
- Water next to stone: 5 water quads, and stone keeps all 6 faces. Lava: 6 quads, all opaque.
- mcmeta: a `frames` list with `{index,time}` entries; a bad index is dropped; the schedule wraps; `texture`-only files and single frames are static; explicit `width`/`height` grids.
- Against the user's 26.3 client jar (`--jar PATH` or `TABLETOP_CLIENT_JAR`, skipped when missing):
  - water_still: 32 frames × 2 ticks (3.2 s cycle).
  - kelp: 20 × 2.
  - prismarine: a 22-step sequence over 4 frames, 300 ticks each, interpolated; tick 150 is a 0.5 blend toward the next frame.
  - lava_still: a 38-step ping-pong over 20 frames.
  - All 54 animated block textures parse. 105 block mcmeta files exist; the other 51 only carry `texture` hints such as mipmap strategy.

**Real terrain** (seed 12345, centred on 0,0, y 32..128, `tools/measure_regions.py` against a local 26.3 server). The 128² region holds 258k water cells, 5.9k kelp/seagrass and 14 waterlogged blocks; 121 of its 384 sections have visible water. Counts use flat-colour keys. Times are on the x86 box, with `Tests~` built in Release and tiered JIT off (`dotnet run -c Release -- --measure DIR --measure-fluids DIR`).

| region | total quads master → now | water quads master → now | whole mesh master → now | fluid pass alone (worst section) |
|---|---|---|---|---|
| 32×96×32 | 2,676 → 2,465 | 465 → 254 | 13 → 13 ms | 1 ms (0.12 ms) |
| 64×96×64 | 13,019 → 11,094 | 2,949 → 1,029 | 51 → 49 ms | 4 ms (0.13 ms) |
| 128×96×128 | 45,704 → 38,239 | 11,546 → 4,097 | 180 → 194 ms | 16 ms (0.14 ms) |

Before this change, water drew faces against every plant and every change of water level. Removing those faces more than pays for the new water cells. The fluid pass costs about 0.04 ms per section. A Debug build of `Tests~` is much slower (8× for the whole mesher here, far more for the fluid pass), so compare timings in Release only. `--live` against the same server also passed.

**Animation cost (estimate; the CPU side was measured):**
- Scheduling all 54 animations takes 0.7 µs per tick on the box.
- A layer copy is one 16×16 RGBA32 frame (1 KB), GPU to GPU, and happens only when a frame changes. A typical ocean view (water, kelp ×2, seagrass ×3, each changing every 2 ticks) needs about 60 copies/s. The worst case, with all 54 animated block textures on screen at once, is 814 copies/s.
- Interpolated textures (prismarine, magma, command blocks, sculk, ...) also blend 256 pixels on the CPU and upload 1 KB to a one-layer staging array, at most 20 times/s each.
- The frame store holds every frame of the textures that are loaded, at 1 KB per frame (about 150 KB for an ocean scene). The atlas is only rebuilt when the palette brings new textures, as before.

**Needs the headset:**
- `Graphics.CopyTexture` between `Texture2DArray`s on Quest Vulkan. Watch logcat for `[Tabletop] ANIM N animated textures`. If the copy is unsupported, it logs `ANIM off` and the first frames stay.
- How water transparency and sorting look with kelp/seagrass and at region edges.
- 72 FPS with animation running.

## Lighting and biome colours (`light_v1` / `biomes_v1`, PROTOCOL §4.5.1; 2026-10-13, no headset)

Paper 26.3 build 181 (Java 25), normal world seed 12345, regions centred on 0,0 with y 32..128 (the same regions as "Real terrain" above; mostly ocean with lush caves and dark forest). Every number is the median of 3 runs with chunks already generated (`tools/measure_regions.py [--extras]` and the plugin's `REGION_SET`/`SNAPSHOT` log lines). "Before" is the plugin from master on the same world.

| region | plugin / client | snapshot bytes | light part | biome part | main-thread snapshot | async walk | extras encode (outbound thread) | client decode (Python) |
|---|---|---|---|---|---|---|---|---|
| 64×96×64 | before | 65.5 KB | – | – | 0.23 ms | 24.0 ms | – | 4 ms |
| 64×96×64 | new, client without caps | 65.5 KB | – | – | 0.08 ms | 24.1 ms | – | 4 ms |
| 64×96×64 | new, `light_v1`+`biomes_v1` | 75.1 KB (+15%) | 9.4 KB, 2,010 runs | 0.2 KB, 2 biomes | 0.17 ms | 25.5 ms | 0.17 ms | 10 ms |
| 128×96×128 | before | 239.8 KB | – | – | 0.94 ms | 98.3 ms | – | 16 ms |
| 128×96×128 | new, client without caps | 239.7 KB | – | – | 0.24 ms | 96.4 ms | – | 16 ms |
| 128×96×128 | new, `light_v1`+`biomes_v1` | 301.1 KB (+26%) | 60.0 KB, 10,913 runs | 1.3 KB, 3 biomes | 0.76 ms | 98.9 ms | 0.75 ms | 34 ms |
| 128×128×128 (y 0..128) | new, extras | 915 KB (728 KB without) | 175 KB, 36,892 runs | | | | | |
| 64×384×64 (full height) | new, extras | 562 KB (407 KB without) | 151 KB, 34,490 runs | | | | | |

- The main-thread step stays far under 3 ms. Old clients now cost *less* main-thread time than before, because the reader asks Paper for light only when a session wants it (`getChunkSnapshot(false, biomes, false, light)`; the old 3-argument call always copied light).
- The largest snapshots stay well under the 4 MiB frame cap, and `max_bytes` now includes the light/biome estimate (computed off-thread; 294,006 estimated vs 294,969 sent for 128×96×128).

**Live light updates** (`tools/light_check.py`, private 16³ region in the sky at 200,100,200): the console builds a stone room with a torch, then opens the roof above the torch. The client mirrors blocks + light from `block_update`, and afterwards a fresh snapshot is compared cell by cell:

```
updates 4, block changes 239, light changes 2,344, largest block_update 10.6 KB
torch cell (sky, block) = (15, 14) after the roof opened; corner under the roof (13, 8); outside (15, 0)
live vs fresh: 0/4096 blocks differ, 0/4096 light values differ  -> OK
```

A client without the caps (`fake_quest.py` without `--extras`) gets `light: null`, `biomes: null` and the same bytes as before; with `--extras` it gets both and `welcome.caps = ["light_v1","biomes_v1"]`.

**Quad counts** (`Tests~ dotnet run -- --measure DIR [--no-light | --flat-light]`, the real `GreedyMesher` + `BlockShading` on the snapshots above, flat colours with the biome tint kinds a pack gives grass/leaves/water; x86 box):

| region | no light (old look) greedy / total | flat light (`smooth_lighting:false`) | smooth light + AO (default) | mesh time no light → smooth |
|---|---|---|---|---|
| 64×96×64 | 9,383 / 12,805 | 12,113 / 15,535 (×1.21) | 16,378 / 19,800 (×1.55) | 139 → 155 ms |
| 128×96×128 | 33,322 / 45,886 | 42,951 / 55,515 (×1.21) | 58,081 / 70,645 (×1.54) | 580 → 625 ms |
| 128×128×128 | 68,737 / 85,159 | 89,292 / 105,714 (×1.24) | 122,021 / 138,443 (×1.63) | 869 → 960 ms |
| 64×384×64 | 39,024 / 48,010 | 52,628 / 61,614 (×1.28) | 67,626 / 76,612 (×1.60) | 545 → 597 ms |

- Smooth lighting costs about +55–63% quads (faces with an AO gradient or a different light level can't merge), flat lighting +21–28%. The recommended 128×96×128 area is about 71k faces, 128×128×128 about 138k: still under the 250k soft cap. `AreaMath.LightFactor` (×1.6 smooth, ×1.25 flat, set from `welcome.caps`) scales the area-setup estimate to match; it still underestimates 128×128×128 by about 12%, as the base estimator already did.
- Meshing time grows about 8–12%: the light lookups are cheap next to the face walk.

**Unit tests** after this change: plugin `./gradlew build` 25 tests, 0 failures (FixtureTest 7: light/biome fixture round trip through `BlockCodec`/`BiomeGrid`, negative-origin quart grid, `block_update.light` builder = fixture, extras stay null without caps); Python `pytest` 29 passed, ruff clean; `Tests~ dotnet run` 75/75 PASS, adding: lightmap (full daylight = white, level 0 = 0.094, monotonic, warm block light, interpolated fractional levels), brightness curve, AO (open floor, wall side 0.8, diagonal 0.8, 1-high inner corner 0.6, 2-high inner corner 0.4), smooth light averaging and vanilla `smoothBlend` zero handling, partial-face bilinear weights, greedy merging with a dimmer cell (merged quads reproduce each face's own colour), no-light fallback = old colours, swamp noise = vanilla (reference values from the 26.3 client classes), colour maps from the real 26.3 jar (plains 91BD59/77AB2F/A37546, forest, jungle, taiga, snowy, desert, birch, meadow, dark forest 507A32; skipped if the jar is missing), biome blend 5×5, block tint rules, `chunk_snapshot`/`block_update` light + biome parsing, section dirtying for AO. `--live` against the test server: snapshot carries light + 2 biomes, all checks PASS. Unity 6000.0.84f1 batch compile: no errors, no new warnings.

**Not verified yet (needs the headset):** how it looks (brightness of caves at Moody brightness 0.5, the warm torch tint, AO strength on the table under passthrough), the cost of the extra ~55% vertices on the Quest GPU, and colour-map loading through `Texture2D.LoadImage` on Android. `tools/e2e.sh` (ports 25565/8765) was not run because those ports belong to the live session.
