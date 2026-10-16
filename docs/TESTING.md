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
- **il2cpp segfault (now and then)**: `il2cpp --convert-to-cpp` died with exit code 139 three times on this box (two branches, three core dumps), with nothing changed between a failing and a passing run. It didn't come back in about 55 direct runs of the same command line (Unity's environment, 8/4/2/1 MB stacks, several at once, pinned to 1–2 CPUs, ASLR off) or in 5 more builds, 4 of them with il2cpp forced to rerun. All three core dumps (`coredumpctl`) have the same stack: `__memcmp_evex_movbe` called from il2cpp's bundled CoreCLR (.NET 7.0.13) while it resolves an interface call (virtual stub dispatch, `ResolveWorkerAsmStub`). The memcmp compares 19 bytes starting 16 bytes before the end of a JIT code page against the runtime's `FixupPrecode` template, and the fault is on the next, unmapped page: the runtime reads past the end of its own code page. The crashing thread had used about 5 KB of stack, so it isn't recursion or a stack overflow, and `ulimit -s unlimited` working once was most likely luck (or a different memory layout). It's a bug in il2cpp's .NET runtime, not in our code, and we can't change that runtime (Unity version pinned), so `TabletopBatch.BuildApk` builds again when every error of a failed build is a segfault, up to 3 builds. Tested with a preloaded test library that makes the first `il2cpp --convert-to-cpp` raise SIGSEGV: build 1 failed with the same `139 (Segmentation fault)`, the retry warning was logged, and build 2 succeeded (46 MB APK, 1.5 min, Bee redid only il2cpp and what follows). The report's error is just `Segmentation fault (core dumped)`: Bee prints the crashed command line to stdout, so the retry can't check that it was il2cpp.
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

### 7.1 Floating head and hands (the new default, 2026-10-13)

Same test server, merged with master (water, block models, light). This replaces ground placement as the default, after the headset session's feedback (ARCHITECTURE 3.5).

- **Unit tests**: plugin 45 JUnit tests, 0 failures. New:
  - `GiantPlacementTest` +5: floating maps head and hands exactly; sizes and the cap; head face direction equals `Pose.forward()` for a yawed, pitched and rolled pose (identity faces north, a 90° yaw faces west); hand box axes; quaternion rotate against `Pose.forward()`.
  - `HandColourTest` 3.

  pytest 30 passed, ruff clean. `Tests~` 146/146 PASS (Quest code unchanged by this step).
- **Live, scripted poses** (`giant_float.py`, not committed: holds each pose 4 s, then compares the debug `avatar_state` with `origin + p / block_size_m` and the sent quaternion). The head is yawed 30°, pitched −20° and rolled 10°; the hands are rotated differently:
  ```
  A: bs 0.01   head [32, 92, 32] (want [32, 92, 32])  left [12, 67, 22]  right [52, 62, 20]  pos_err 0.0000  head_q = pose q (err 0.0005, 3-decimal rounding)
               head 25 blocks, hands 10, hand_block cherry_planks (from the skin), scale 50
  B: bs 0.01   user 1 m back from the table, 1.6 m up: head [-100, 192, 32] exact; its chunk was loaded async and ticketed
  C: bs 0.002  head [50, 282, 50] exact; head capped at 64 blocks (from 125), hands 25.6, note "head size capped from 125.0 to 64.0 blocks"
  ALL OK
  ```
  `tools/fake_quest.py --avatar` (head circling 0.45 m above the table): head y 77 = 32 + 0.45 / 0.01, `head_q` = the scripted yaw quaternions, hands ±5 blocks.
- **Second player**: the mineflayer bot (joining as 26.1 through ViaBackwards 5.12.0) receives the three `item_display` entities at spawn, then at the exact phase-C positions `(50, 282, 50)`, `(0, 182, 0)`, `(100, 182, 0)`. While the head was at x −100 in phase B, 188 blocks from the bot, the server stopped sending the entities (display tracking range 128) and sent them again in phase C.
- **Skin**: `skin_player` resolved on the offline-mode server, the head switched to the signed profile and the hand block changed from the default skin's (`raw_copper_block`) to the closest match for the skin's hands (`cherry_planks`).
- **Orientation conventions** come from the 26.3 client classes, read without a game client: `DisplayRenderer$ItemDisplayRenderer` rotates 180° about Y, `player_head.json` has a special-model transform of x 180° plus (0.5, 0, 0.5), `template_skull`'s `fixed` transform has y 180° plus 4/16 up, `ItemTransform.apply` then subtracts 0.5, and the head cube is (−4, −8, −4, 8, 8, 8). Together these put the FIXED head centred and facing +Z, which the unit test above builds on.

Still needs a Java client by eye: that the head's face points where the headset looks (the convention above), the hand boxes' look, interpolation smoothness, and the view from far away.

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
- Block editing (§4.20) follows vanilla placement (two-block structures get both halves; the old "placed as the one state given" is gone), except: clicking a slab with the same slab doesn't make a double slab; snow layers, candles, sea pickles, petals and turtle eggs don't stack; two chests don't join; survival (`canSurvive`) is checked on the server only (the headset shows a plant on stone until the refusal reverts it); protection plugins aren't consulted (no `BlockPlaceEvent`); region requests aren't clamped to the world border (terrain past it is shown, edits there are refused).

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

### Lighting with models and water (after merging players, water and block models; 2026-10-13, no headset)

The quad counts above were measured before block models and the fluid mesher existed. Now every geometry path takes its colours from `BlockShading`: greedy cube faces (smooth light + AO), model quads (`BlockShading.Model`: axis-aligned faces smooth light + AO, rotated faces the block's own light, `tintindex` faces the biome colour), and fluids (`BlockShading.Fluid`: vanilla's one light per face, max of the cell and the cell above, folded into the face key so flat water only merges with equally lit water; per-biome water through `FluidMesher.WaterColor`). The block shaders now use vanilla's face shading (up 1.0, down 0.5, north/south 0.8, east/west 0.6) and leave faces that aren't axis-aligned unshaded (cross plants used to come out at 80%).

`Tests~ dotnet run -- --jar <26.3 jar> --measure DIR [--flat-light | --no-light]`, the same seed-12345 snapshots as above, "pack" mode (the jar's block models and one texture layer per texture, as on the headset), total faces = greedy + model/fluid quads:

| region | no light (old look) | flat light | smooth light + AO (default) | greedy part no light → smooth | mesh time no light → smooth (x86) |
|---|---|---|---|---|---|
| 64×96×64 | 18,760 | 19,579 (×1.04) | 23,252 (×1.24) | 7,237 → 11,707 | 126 → 144 ms |
| 128×96×128 | 67,005 | 69,368 (×1.04) | 82,873 (×1.24) | 23,937 → 39,714 | 506 → 579 ms |
| 128×128×128 | 120,274 | 130,875 (×1.09) | 159,717 (×1.33) | 61,790 → 100,620 | 775 → 921 ms |
| 64×384×64 | 64,688 | 75,454 (×1.17) | 88,980 (×1.38) | 36,823 → 60,910 | 517 → 584 ms |

- Only greedy faces split (+55–66%); model and fluid faces were never merged, so the total grows 24–38%. The recommended 128×96×128 area is about 83k faces with everything on, 128×128×128 about 160k, both under the 250k soft cap. `AreaMath.LightFactor` is now ×1.35 (smooth) / ×1.15 (flat) on top of the shapes agent's recalibrated estimate (5.5 faces/column): 128×96×128 estimates 122k (actual 83k), 128×128×128 150k (actual 160k, 6% under).
- In "flat" mode (no pack, placeholder shapes) 128×96×128 is 38,486 → 55,140 faces with smooth light.

**Live, merged plugin** (my test server, ports 8772/25572): `tools/light_check.py` now clears the room after subscribing (an unloaded area can't be `/fill`ed) and pings so the 15 s idle timeout doesn't close it: clear + rebuild + open roof = 5 `block_update`s, 476 block changes, 4,066 light changes, largest message 25.8 KB, and the live mirror equals a fresh snapshot (0/4096 blocks, 0/4096 light values differ). `Tests~ --live`: light + 2 biomes, all checks PASS. `fake_quest.py --avatar` without caps gets 101.5 KB and no extras, with `--extras` 115.0 KB with 2,833 light runs, and the giant spawns in both.

**Tests after the merge:** plugin 37 tests, 0 failures; pytest 30 passed, ruff clean; `Tests~ dotnet run -- --jar …/26.3.jar` 180/180 PASS (adds model quads lit like cube faces, rotated quads with own-cell light, tinted model quads, fluid light and merge splitting, WaterColor per biome with the atlas alpha, and with the jar: grass_block top, short_grass cross and oak_stairs through the real models); Unity 6000.0.84f1 batch compile clean (no errors, no shader errors, no new warnings).

**Headset to check:** the new face shading (east/west sides darker and north/south sides brighter than before, crosses at full brightness), cave darkness at brightness 0.5, AO on stairs/slabs/fences, water colour and light at depth (deep water faces darken: water light follows vanilla's sky attenuation), and FPS with about 25% more faces.

### Dark faces in AR: light floor, Bright default, open-sky edges (after the first lit headset session; 2026-10-13)

The headset showed near-black faces in caves and around a mineshaft cut by the region edge, and a very dark seabed. Changes in `BlockShading`: the lightmap uses vanilla's "Bright" setting (was the game's default 0.5) and is rescaled so light level 0 shows at `min_light` = 0.2 and full light stays 1, keeping vanilla's falloff shape in between; and every light/AO sample outside the region is open sky-lit air (before: clamped to the nearest cell inside, which copied the cave's darkness to the cut). `Tests~ dotnet run -- --light-report` (brightness of a white face before the shader's face shading):

| face | before | after |
|---|---|---|
| floor of a light-0 cave | 0.094 | 0.200 |
| the same cave floor at the region edge (corner at the cut .. corner inside) | 0.094 .. 0.094 | 0.686 .. 0.200 |
| cut face (front cell outside the region) | 1.000 | 1.000 |
| seabed 10 blocks under water (sky light 5) | 0.314 | 0.510 |
| full daylight | 1.000 | 1.000 |

Levels 10-15 now differ by under 2% (Bright compresses the top), so slightly more faces merge, but the total barely moves: 64×96×64 23,317 faces, 128×96×128 83,055, 128×128×128 159,947, 64×384×64 89,116 (pack mode, smooth light; +0.1-0.3% against the table above). `tabletop.json` `min_light` and `brightness` tune it on the headset (`min_light: -1` = vanilla's curve).

## Navigation and bigger areas (Paper 26.3, seed 12345, 2026-10-13)

A separate headless test server (Paper 26.3 build 181, JDK 25, offline, normal world seed 12345, its own ports) running this plugin, with `max_region` raised for the big runs. Numbers are from second runs, with chunks already generated. All on the x86 box: no headset.

### Tests (after merging master: players, water, block models, light)

| Suite | Result |
|---|---|
| Plugin (JUnit) | 45 tests, 0 failures. Nav additions: `SectionCodecTest` (7: section round trip with partial edges, delta lists only new/changed sections, part splitting, size estimate, fixtures rebuild snapshot 9 from 8, per-section light and light-aware deltas, diff index limit) and `RegionRequestTest.viewMargin`. |
| Tools (pytest + ruff) | 38 passed, ruff clean. Nav additions: the four fixtures, `sections_v1` full/delta, parts, validation, and sections with light and biomes. |
| Quest scripts (`Tests~`, .NET 8) | 215 checks PASS without a client jar, 249 with `MC_CLIENT_JAR` (the block-model tests). Nav additions in `NavTests.cs`: NavState (window, RegionRoot placement, trigger and shift requests, clamp while pending, seamless re-centre, reverse and diagonal moves, refusal back-off, timeout, far jumps, build height, padding with and without the server cap, speed curve and stream cap, latency until meshed), SectionPlan (re-key with a permuted palette, changed, stale and re-lit sections, delta lists, unaligned shifts, new dims, partial edge sections), `sections_v1` (full, delta, light and biomes, wrong base, missing section, bad totals, delta without its new section, light delta without base light), SnapshotAssembler (parts, delta plan, v1 still works), CapMesher, and area setup with margins and the section budget; caps take the biome tint and the same UVs as the faces next to them. |
| `Tests~ --live` against the server | All PASS with `light_v1` + `biomes_v1` granted: area setup 48×64×48 window + 16 margin → an 80×96×80 region; a one-section shift arrives as a delta (30 of 150 sections); `bad_snapshot` → the server's full resend matches the delta-assembled grid, blocks and light. |
| Unity 6000.0.84f1 batch compile | 0 errors; the only warning in our code is the existing CS0618 (`overlayType`). |
| `Editor/ShaderCheck` (headless, `-executeMethod TabletopCraft.EditorTools.ShaderCheck.Run`) | 60 variants (Blocks, BlocksTransparent and PlayerSkin × Vulkan and GLES3 × no keyword, `STEREO_MULTIVIEW_ON`, `STEREO_INSTANCING_ON`, `INSTANCING_ON`, `UNITY_SINGLE_PASS_STEREO` × vertex and fragment), 0 errors. A deliberately broken shader is reported, so the check is real. |
| Flat-world e2e tools on the nav server (before the merge) | fake Quest expectations OK; `consistency_check.py` 0/65,536 differing cells as a v1 client and as a `sections_v1` client (the 5,120-block fills arrive as same-region deltas, 8 of 16 sections); `error_paths.py` as expected; `region_e2e.py` ok with over-cap requests derived from `welcome.limits`. |

### Gliding across the world (`tools/nav_e2e.py`; blocks only, measured before the merge)

A `sections_v1` "headset" asks for a window plus a 16-block margin, then shifts the region by one section 4 times (east, then south). With `--no-v1` it is alone on the server (latency below); otherwise a v1 client on the same shared region gets every region in full, and each delta-assembled grid is compared with it block for block.

| visible window (y range) | region loaded | first snapshot, `sections_v1` | same region, one v1 message | one-section shift (delta) | sections sent | request→snapshot, headset alone | server walk (async) |
|---|---|---|---|---|---|---|---|
| 128×96×128 (y 32..128) | 160×128×160 | 835 KB, 1 part, 369 ms | 730–845 KB | 86–156 KB | 80 of 800 | 144–183 ms | 42 ms |
| 192×96×192 (y 32..128) | 224×128×224 | 1.84 MB, 2 parts, 643 ms | 1.60–1.84 MB | 122–279 KB | 112 of 1,568 | 227–256 ms | 83 ms |
| 256×96×256 (y 32..128) | 288×128×288 | 3.37 MB, 4 parts, 1.0 s | 2.90–3.25 MB | 169–365 KB | 145 of 2,592 | 350–398 ms | 138 ms |
| 256×128×256 (y 0..128) | 288×160×288 | 6.54 MB, 7 parts, 1.3 s | 5.6–5.95 MB: over the 4 MiB frame, the v1 client gets `region_too_large` | 327–543 KB | 180 of 3,240 | 401–454 ms | 172 ms |

- **Cross-check**: every delta-assembled region matched the v1 client's full snapshot (4 shifts per size, then 8 more at 256×96×256). The only difference ever seen was transient: kelp grew between the two clients' `block_update`s, and it matched after 1.5 s.
- **Server main thread** per region change: about 3 ms at 288×288 (361 chunk snapshots). The walk, the diff compare, the `region_request` size estimate, the palette remap and the encoding all run off the main thread now.
- **Faster walk**: block ids are cached by `BlockData` instead of building a state string per block. The same 128×96×128 box (y 32..128) read in 121 ms before and about 20 ms after; 224×128×224 (6.4M blocks) reads in 83 ms.
- A sectioned snapshot is about 15% bigger than one `rle_palette_v1` message for the same region (per-section headers, runs cut at section borders), but it splits into parts under 1 MiB and, while gliding, only the new slice is sent: 11–20% of a full region.

**With light and biomes (after the merge).** Both clients ask for `light_v1` + `biomes_v1`; light travels inside the sections and the cross-check compares light too (every step matched).

| visible window | region loaded | first snapshot, `sections_v1` | same region, one v1 message | one-section shift (delta) | sections sent | request→snapshot, headset alone | server walk (async, with light) |
|---|---|---|---|---|---|---|---|
| 128×96×128 (y 32..128) | 160×128×160 | 1.13 MB, 2 parts | 1.0–1.2 MB | 145–240 KB | 80 of 800 | 231–305 ms | 50–81 ms |
| 192×96×192 (y 32..128) | 224×128×224 | 2.69 MB, 5 parts, 1.5 s | (not run) | 158–462 KB | 112–114 of 1,568 | 343–439 ms | 101–158 ms |
| 256×96×256 (y 32..128) | 288×128×288 | 4.94 MB, 13 parts, 1.8 s | over the 4 MiB frame | 224–600 KB | 144–147 of 2,592 | 458–515 ms | 170–192 ms |

Light adds about 40–60% to snapshots and deltas, and reading it about 20–30 ms per million blocks.

### Meshing the same regions (`Tests~ dotnet run -c Release -- --measure-nav DIR`, x86, one thread, before the merge)

"Texture-like" keys merge faces only within one block's top, bottom or side texture, which approximates a resource pack; flat colours merge more. "Non-empty" sections are the ones that become meshes. These counts are without the block models and light of the merged branches; with smooth light expect about +24–38% faces ("Lighting with models and water" above). The section counts barely change.

| region (window + margin) | sections | non-empty | quads, flat | quads, texture-like | vertices | mesh, 1 thread | worst section | re-key compare (1-section shift) | 6 clip caps | `AreaMath.Estimate` |
|---|---|---|---|---|---|---|---|---|---|---|
| 160×128×160 | 800 | 328 | 108k | 113k | 452k | 542 ms (incl. JIT warm-up) | 8 ms | 4 ms | 6.0k quads, 2.1 ms | 118k |
| 224×128×224 | 1,568 | 706 | 234k | 243k | 973k | 770 ms | 1.2 ms | 5 ms | 10.9k quads, 1.4 ms | 232k |
| 288×128×288 | 2,592 | 1,223 | 380k | 396k | 1.58M | 1.3 s | 33 ms | 8 ms | 16.5k quads, 1.9 ms | 383k |
| 288×160×288 | 3,240 | 1,808 | 544k | 566k | 2.26M | 1.8 s | 59 ms | 10 ms | 20.8k quads, 2.2 ms | 476k |

### Quest 3 capacity guidance (ESTIMATES; only 64×96×64 has run on the headset)

- **Draw calls are the likely limit.** Every non-empty 16³ section is its own mesh (one draw call, two when it has water, glass or plants). The headset has only been measured at 64×96×64: about 50 meshes, 72 FPS, 2.5 ms app GPU time. A 128-wide window loads about 330 meshes; 192 about 700; 256 about 1,200–1,800. Area setup now warns above about 600 estimated meshes (and still above 250k faces); hold B to try bigger areas anyway. Bigger areas need the section meshes batched (for example 2×2×2 sections per mesh), which is the next step. (Done since: batches of 2×2 section columns, 100 renderers at 256; see "Area size presets and batching" below.)
- **Defaults**: the plugin's `max_region` is now 288×384×288 / 16,777,216 blocks / 32 MiB in total, so a 256-wide window with its margin fits. The recommended maximum window stays 128×96×128, now with a margin to glide; you can move anywhere in the world.
- **Streaming**: a 128-wide window needs one delta of about 90–160 KB per 16 blocks travelled, so gliding at 30 blocks/s is about 0.3 MB/s. The headset assembles it on a worker (copy plus a few dozen sections) and meshes about 80 new sections, plus about 80 neighbours refreshed when idle; the old meshes stay up the whole time.
- **Speed limit**: the view can't leave the loaded margin, so the glide speed is capped at about 12 blocks of slack divided by 1.3 × the measured round trip until the new terrain is meshed. On this box that's about 50 blocks/s for a 128 window; the headset's meshing time adds to it.

### Fixes after the first glide on the headset (2026-10-13)

- **Region ping-pong.** The headset log showed the region origin flipping between two neighbours every 0.3 s (x −48 → −32 → −48 ...). After a 16-block shift the view sits 12 blocks behind the new centre, and the reverse trigger was the same 4 blocks as the forward one, so any small motion back (stick or head jitter) asked for the old region at once. `NavState` now has hysteresis: after a shift, a shift back on that axis needs 16 − 4 + 3 = 15 blocks past the centre (capped at the margin's edge) until the view has come back across the new centre, and a push against the edge without a margin has to add up to 2 blocks. New `NavTests`: hovering with jitter after a shift never asks for the old region back, a real move back still shifts back, z jitter while gliding along x asks for no z shift, gliding on past the new centre makes reversing normal again, and stepped mode (no margin) ignores a small push.
- **Resend after `bad_snapshot`.** The full resend was a fresh read of the world, so on a busy region (freshly generated terrain settling, players editing) it already held changes that hadn't been diffed yet, and `Tests~ --live` "bad_snapshot → full resend matches" failed (seen at 128×128×128 with about 20 `block_update`s during the run; reproduced here on fresh terrain with a fill loop on the console). The plugin now resends the session's last snapshot with every `block_update` sent since (`lastGrid`, `lastLight`, the last biomes), which is exactly what the client should have; newer changes follow as `block_update`s for the new snapshot id. The live test compares with the client's state when the resend arrives and prints the number of differing cells. Results: PASS on 160×144×160 of fresh terrain with 10, 65 and 53 `block_update`s during the check (a fill and a moving `setblock` every 0.3 s). The order of updates and snapshots was already safe: one ordered outbound thread, diffs dropped when their snapshot id is stale, and the headset holds updates while a snapshot decodes.
- **After merging master (block editing, underwater view):** plugin 59 tests, 0 failures; `pytest` 50 passed, ruff clean; `Tests~ dotnet run` 298/298 PASS without a client jar; Unity batch build OK (the il2cpp retry in section 5 included), `ShaderCheck` 60 variants, 0 errors.

### Not verified (needs the headset)

Clipping and the cut-face caps on screen, turning, the glide speed and feel, how long the headset takes to assemble and mesh a delta, the giant staying put while the region moves (`pose.snapshot_id`), and frame rates above 64×96×64.


## Eye in water, lava, powder snow or a block (Quest, 2026-10-13, no headset yet)

What changed:
- `World/EyeMedium.cs` (engine-free) decides the medium at the centre eye, filters it and gives the look per medium.
- `World/EyeEffects.cs` applies the result on the headset: fog globals, the passthrough tint and the water's inside faces.
- `Shaders/TabletopFog.cginc` adds distance fog to the Blocks, BlocksTransparent and PlayerSkin shaders.
- `Shaders/EyeOverlay.shader` with `Resources/TabletopEyeOverlay.mat` draws the tint.
- `FluidMesher` and `MeshData.WaterBack` hold the reversed inside copies of water faces, and `TabletopRenderer.Underwater` turns that third submesh on.
- The design and the edge-case decisions are in ARCHITECTURE.md §4 under "Eye in a fluid".

**Tests** (`Tests~/EyeTests.cs`; `dotnet run -- --jar <26.3 jar>`: 275 checks with master's nav tests merged, all PASS):
- Medium:
  - The lower cell of a two-high water column is water up to y 1. The surface cell ends at 1 + 8/9, exactly at the surface is air, and 1e-4 below it is water.
  - A lone source cell is 8/9 high.
  - Flowing water: level 4 is 4/9 high, level 7 is 1/9. Falling water (level 8) is 8/9.
  - Kelp, seagrass, bubble columns and waterlogged fences count as water.
  - Waterlogged stairs are solid while they mesh as a cube (no pack), and water up to 8/9 once they are a model.
  - Waterlogged slabs: the slab's half is solid, the other half is water.
  - Lava; powder snow (whole cell); stone solid; glass air.
  - Under ice the water ends at 8/9, as in vanilla.
  - Outside the region (sides, below, above) is air. The region's top cutting through water is a surface at 8/9. No snapshot means air.
- Filter, with 1 cm blocks (the 4 mm margin is 0.4 block):
  - Water is taken after the 50 ms dwell, not on the first frame.
  - Hovering ±0.2 block around the surface for 100 frames never switches.
  - The eye leaves water once 0.6 block above the surface.
  - A one-frame dip doesn't switch.
- Fade and looks:
  - The tint fades in 0.07 → 0.38 → 0.45 over 0.2 s with no colour shift, and fades out to nothing.
  - Vanilla water vision: 24 blocks of visibility on entering, 96 after 30 s. Ramp values 0 / 0.6 / 1 at 0 / 100 / 600 ticks.
  - Without biomes the fog uses the default water colour 0x3F76E4.
  - Lava visibility is 1 block with a strong tint. Solid is dim, not black.
- Inside faces:
  - One water cell gives 6 outside faces and 6 reversed copies with their own vertices, wound to face into the water, never in the transparent list. The 8/9 surface is among them.
  - Lava has none.
  - A merged 16×4×16 ocean has a copy for each of its 10 quads.
- Nav's window caps:
  - Kelp, waterlogged models and bubble columns are cut as water; cubes as themselves; plants and air not at all.
  - A water cut over a kelp cell merges into one quad with an inside copy.
- **Real terrain** (the seed-12345 snapshots from the water section, `--measure-fluids`): the 128×96×128 ocean region has 4,097 water quads, so 4,097 inside copies. That is about 16k extra vertices (+11%) in memory, drawn only while the eye is in water. Fluid meshing still takes 17 ms for all 384 sections (Release, x86).

**Unity:**
- The C# compile check is clean.
- The four changed shaders were compiled through `ShaderData.Pass.CompileVariant` for Android Vulkan and GLES3, with and without `STEREO_INSTANCING_ON`/`INSTANCING_ON`: 32 vertex/fragment variants, 0 errors. This was repeated after merging nav's clip volume into the same shaders. A temporary editor script did this and was not committed.

**Needs the headset:**
- Whether the overlay box really tints the passthrough underlay. This depends on how the compositor uses the eye buffer's alpha: premultiplied output is assumed.
- How the four looks feel, in particular water at 45% tint and fog strength 0.95 against the real room. Values are in `EyeMediumRules.Look`.
- The surface seen from below and the aquarium walls from inside, including their sorting with kelp.
- Logcat should show `[Tabletop] EYE Water` / `Air` when the head dips into the miniature ocean.
- No flicker at the surface, and FPS. The fog adds one distance per fragment in the block shaders.


## Block editing from the headset (`block_edit`, PROTOCOL §4.20; 2026-10-13, no headset)

**Unit tests.** Plugin `./gradlew build`: 43 tests, 0 failures. `BlockEditTest` (5) covers place/break parsing, 11 malformed messages rejected (no op, unknown op, no/short pos, fractional or string or out-of-world pos, place without or with a blank state, unknown face, 600-character state), the deny-list id, an error carrying `op`/`pos`/`ref_seq`, and the token bucket (a burst of 40 lets 20 through, refills at 20/s, steady 20/s forever, never holds more than a second's worth). `FixtureTest` gains one test: both fixtures parse and the error builder reproduces `error_edit_denied.json`. Python `pytest`: 42 passed (fixtures validate, builder = fixture, 7 bad-field cases), ruff clean. `Tests~ dotnet run -- --jar …/26.3.jar`: 231/231 PASS, including 44 for editing:
- DDA (`BlockRaycast`): straight down onto a floor (cell, face, adjacent cell, distance, entry point), entering the region through its top and through a side, max distance, water skipped (aim at the seabed and place into the water), a horizontal ray on a west face, misses (pointing away, parallel outside, zero direction), a ray starting inside a block, short grass targeted by its cell, and 3,000 random rays checked against a 1/500-block ray march (1,669 hits, 0 disagreements, 1 ray where the march stepped over a clipped corner).
- Frames: a Unity-space ray along +Z is block-space north and hits the south face; the hit point maps back onto the rendered face; the rendered block centre lands in its cell; block space equals the pose frame (`TabletopPose`) divided by the block size.
- Rules: state property read and replace, log `axis` from the clicked face, furnaces turned toward the user, stairs facing the look direction, vertical facings kept, water left by waterlogged blocks and kelp, waterlogging when placing into a source.
- Editor and reconciliation: no edits without `edit_v1`; a break is shown at once and re-meshes; an error with `pos` reverts it; a `block_update` confirms it and a later error then changes nothing; a stale `block_update` confirms nothing; 3 s of silence reverts; place goes on the hit face; a state missing from the palette is sent but not shown (the palette stays in sync); no placing outside the area; waterlogged slab and seabed placement; pick; a new snapshot clears pending edits; `unknown_type` from an old server reverts everything and turns editing off; the builder reproduces `block_edit.json`, and `error_edit_denied.json` reverts its cell.

Unity 6000.0.84f1 batch compile: no errors, no shader errors, no new warnings (the 2 existing `overlayType` warnings in `TabletopSceneBuilder`).

**Live** (my test server: Paper 26.3 build 181, seed 12345, region origin (-16, 32, -16), 32×96×32, ports 8776/25576). The middle column is ocean, so every ray below went through water onto gravel on the sea floor at (0, 48, 0).
- `Tests~ dotnet run -- --live-edit ws://127.0.0.1:8776/tabletop` runs the headset's own code (DDA down the middle column, `BlockEditor`, `PendingEdits`, `LinkClient`). Times are from the send to the confirming `block_update`: break gravel (shown at once, confirmed 138 ms), put it back (144 ms), gold block (not in the palette, so it arrives with `palette_add`: 151 ms), break the gold block (148 ms), and a deny-listed command block on a guessed cell is reverted from the error's `pos` (55 ms). 7/7 PASS.
- `tools/edit_check.py --url ws://127.0.0.1:8776/tabletop`: 14/14 PASS. Break / restore / gold: 139–198 ms each. A second fresh connection's snapshot holds the gold block (the world really changed) and gets `edit_denied` without `edit_v1`. Occupied cell `edit_denied`, outside the region `out_of_region`, `minecraft:not_a_block` `bad_state`, `minecraft:command_block` `edit_denied`, bad face `bad_message` (with `ref_seq`). A burst of 40 edits: 20 pass, 20 `rate_limited`.
- Server log: one `EDIT session=… break|place world x,y,z face=… old -> new (ms)` line per edit; `setBlockData` with physics costs 0.07–0.32 ms on the main thread. Refusals log `EDIT_DENIED`.
- Config: `edit.require-token: true` with an empty `auth.token` gives `welcome.caps = []` and `edit_denied`. With a token set, a headset presenting it gets `["edit_v1"]`.
- Fixed on the way (`TabletopPlugin.diffAll`): a change made while a diff of the region was still reading lost its dirty flag and waited for the 4-tick light recheck (the first break measured 351 ms). Now the region stays dirty and the next diff starts right after.
- Regression: `fake_quest.py --extras --avatar` against the same server: ok, no errors.

**After merging master (navigation, floating giant).** Editing now only targets the visible window (`NavState`): the ray is clipped to it, blocks cut by its sides are hit on their visible part, the hidden margin can't be hit or built on, the outline is cut like the terrain, and guesses and reverts re-slice the clip caps. The live-edit test now assembles snapshots through `SnapshotAssembler` (the app's `sections_v1` path). Results on the merged tree: plugin 59 tests, 0 failures; `pytest` 50 passed, ruff clean; `Tests~ dotnet run -- --jar …/26.3.jar` 299/299 PASS (adds 6 window cases: a column outside the window misses, inside works, a ray entering through a cut side hits the half-shown cell and can't build outside it, a wall in the margin isn't hit, a half-shown edge block can be built on, a whole-block edge stops at the next cell), and 325/325 after also merging water's underwater view; Unity batch compile clean. Live on the merged plugin: `edit_check.py` 14/14 (`welcome.caps` = `view_margin`, `pose_snapshot_id`, `edit_v1`; breaking tall seagrass left a water source as designed; 134–198 ms per edit), `--live-edit` 7/7 (a block new to the palette arrives in 147 ms and is not guessed).
- Nav's own `Tests~ --live` on this server (after its resizes had grown the region to 128×128×128 of freshly generated ocean and lush caves) fails one check, "bad_snapshot -> full resend matches the delta-assembled region", with 19–22 `block_update`s arriving during the run. It fails the same way with my `diffAll` change taken out, so the world was still changing between the delta and the resend, rather than anything in this branch. (Fixed on the nav branch: the resend is now the last snapshot plus the updates sent since; see "Navigation and bigger areas", "Fixes after the first glide on the headset".)

**Needs the headset:** whether `RightHand.forward` (the OVRCameraRig hand anchor) points out of the front of the controller. If the laser comes out at an angle, the ray needs a pitch offset. Also: outline and laser visibility in passthrough (dark outline, 0.8 mm at 1 cm blocks), the preview cube's textures, size and position, label readability, haptic strength, the trigger hold-repeat rate (0.35 s, then every 0.25 s), and whether B for pick feels right next to A (re-place table). `tools/e2e.sh` was not run (its ports 25565/8765 belong to the live session).


## Entities and actions (`entities_v1` / `actions_v1`, PROTOCOL §4.21–4.22; Paper 26.3, 2026-10-13, no headset)

### Tests

| Suite | Result |
|---|---|
| Plugin (`./gradlew build`) | 69 tests with master's, 0 failures. New: `EntityDeltaTest` (7: the full and delta fixtures rebuilt from server state, thresholds accumulate, cleared fields sent as false/null, full lists after a region change, pose names) and `ActionBatchTest` (3: the fixture tick with collapsed repeats, region filtering with the player/entity margins, a break clears the dig, the 128 cap). |
| Python (`pytest`, `ruff`) | 58 passed with master's, ruff clean. `EntityMirror` applies the full and delta fixtures; validation of entries, equipment slots, player poses and action kinds. |
| C# (`Tests~ dotnet run -- --jar 26.3.jar --entity-models entity_models.json`) | 385 PASS with master's (316 PASS without either; the model tests skip without `--entity-models`/`TABLETOP_ENTITY_MODELS`, the pack tests without `--jar`). New in `EntityTests.cs`: the store against the fixtures (vehicle links, removals, equipment replaced whole, full lists reset fields), `player_positions` extras, actions; without model data `Get()` is null while the catalog still names model and texture, dyes fall back, junk files are refused, and a minimal file loads with aliases and dyes; with the generated file all 185 model layer names parse (134 distinct models, 1,716 cubes, 10,296 faces) with every triangle wound outward; feet at y 0 and plausible heights for 12 mobs (villager's root scale included); mirrored legs share their skin area; animation (head, legs at 1.4 rad, zombie arms, riding legs, the 6-tick swing, −0.76 rad at half-swing as `HumanoidModel`); against the jar: 271 catalog looks (every boat, minecart, mob, variant, baby) resolve to a model and existing textures/overlays/second layers, and all 1,657 item definitions resolve (759 sprites, 898 cubes, 0 unresolved, 0 missing textures), plus their display transforms. |
| C# `--live` | Against the test server with a boat + pig, cow, villager, item and minecart in the default region: `live: 6 entities messages, 6 entities (oak_boat, pig, cow, villager, item, minecart); 5 models, 0 boxes` and the existing checks PASS. |
| Unity 6000.0.84f1 | Compiles with no errors or warnings in Assets; `ShaderCheck` (now with `TabletopCraft/Entity`) 80 variants, 0 errors. |

Model data: `java tools/entity_models/DumpEntityModels.java ~/.minecraft 26.3 entity_models.json` reads the 26.3 jar and its libraries in a few seconds and writes 82 KB: 185 layer names (134 distinct models, 51 aliases such as the other boat woods) and the 16 dye colours. It isn't committed; the tests and the headset read the user's own copy.

### Live server (`tools/entities_check.py`, own Paper 26.3 server, seed 12345)

`SERVER_FIFO=run/entities/stdin.fifo TABLETOP_URL=ws://127.0.0.1:<port>/tabletop python entities_check.py 400 100 400 --tick --load 200`: all checks pass. The client gets `entities_v1` and `actions_v1`; a boat summoned with a baby pig passenger arrives with `passengers: [pig]` and the pig with `vehicle: boat`; the cow at its summon position and yaw 45, the sheep `light_blue`, the zombie's `item replace` iron sword in `main_hand`, the item as 3 diamonds, the frame facing north with a compass; six 0.5-block `tp` steps of the cow all arrive; a killed item is removed; `damage` on the sheep arrives as a `hurt` action and a TNT explosion as 9 `break` actions of `minecraft:stone`.

| Scene | Entities | Main thread per send (avg / max) | Bytes/s (one client) |
|---|---|---|---|
| 7 still entities (after the spawn) | 7 | sample 0.09 ms / 0.13; delta 0.01 ms | ~70 (idle) |
| 191 NoAI mobs (no ticking) | 191 | sample 0.23 ms / 3.4; delta 0.05 ms | spawn burst only (one 31 KB full message) |
| 200 wandering mobs (`--tick`, forceloaded) | 195 | sample 0.11–0.17 ms / 0.4–0.9; delta 0.03–0.05 ms / 0.6 | 11.6–16.4 KB/s |

Actions in that run: 150 `hurt` and 9 `break` actions, 5.8 KB in total. The sample includes `getNearbyEntities`, the state copies, and re-reading equipment/items for a fifth of the entities. With no player online the region's chunks only hold plugin tickets, so entities there don't tick and console selectors (`kill @e`, `tp @e`) don't find them; `--tick` forceloads the region for the test. A player nearby makes them tick normally.

### Offline look check

`Tests~ dotnet run -- --jar 26.3.jar --entity-models entity_models.json --entity-scene scene.json` composes a scene the way `EntityFigure`/`EntityGear` build it (a zombie riding a boat holding a sword, a walking cow with its head turned, a red sheep, chicken, warm pig, villager, horse, wolf, creeper, skeleton with a bow, cat, minecart, spider, warm baby cow, iron golem, and a player-model mannequin in iron/diamond/leather/gold armour with a sword and a torch), and `tools/render_mesh.py scene.json 26.3.jar out.png --view se|nw|s|w` renders it with the jar's textures. Checked by eye from four sides: models face their yaw, skins map like the game (faces, wool, udders, snouts), the rider sits legs-forward in the boat, armour layers sit on the right parts, the sword points forward from the hand and the torch's flame forward. The images contain Mojang textures and aren't committed.

### Not verified (needs the headset)

Frame rate and draw calls with many entities (meshes and materials are shared and instancing is on, untested), the hurt flash (the player skin swaps to the entity shader for 0.5 s), dig cracks and particles on screen, labels, how riders look with the boat's interpolation, and lighting from the world's light at each entity.

### Known gaps

Without `entity_models.json` every entity is a box and nobody wears armour (players still hold their items; those come from the pack). No tropical fish patterns (body colour only), wolf/cat collars, saddles, horse/wolf armour or llama carpets; no shield/banner/head item models (cubes of their particle texture); paintings assume 16 texture px per block; display entities ignore their transformation; minecart contents are a plain cube of the block's item model; the boat's water mask isn't drawn, so water shows inside a boat; arrows ignore their model's texture scale.

## Block menu, hotbar and HUD (`block_registry`, PROTOCOL §4.24; Paper 26.3, 2026-10-14, no headset)

### Tests
- Plugin: `./gradlew build` 75 tests, 0 failures. `BlockRegistryTest` (5): filtering (technical, `edit.deny`, feature-disabled, variants of denied blocks), categories for the fixture blocks and 19 tricky names (light blue concrete powder, red sand, red mushroom, jack o'lantern, sea lantern, trapped chest, copper bulbs, crimson stem...), family order (logs, stripped, wood, planks, shapes; copper by weathering then waxed; colored by kind then dye), the placement check (registry states and variants accepted; fire, water, air, barrier, `edit.deny`, disabled blocks refused; nothing before the registry is read), and the message equals `tools/fixtures/block_registry.json`. `FixtureTest` builds the fixture from the same blocks.
- `pytest` 66 passed, ruff clean: the fixture validates; duplicates, a wrong `count`, blank states, categories without an id, dangling variants and non-string items are rejected.
- `Tests~ dotnet run -- --jar …/26.3.jar` 452/452 PASS (plus 9 with `--live` and `--live-edit` against the plugin). New in `Tests~/MenuTests.cs` (88 checks):
  - registry parsing, variants, labels and pack names (`item.minecraft.redstone` → Redstone Dust);
  - the menu model: before the registry only Search and Info (an open Info tab stays Info when the categories arrive); closed menu ignores the ray; panel 0.312 × 0.250 m with no overlapping buttons; hover names; trigger selects and pushes into the hotbar; ray off the panel gives the bindings back; stick scroll with a 0.35 s delay then 0.12 s repeats, only while the ray is on the panel; arrows clamp; tabs keep their scroll; search ranking (`oak` → oak log, oak stairs, oak planks, then dark oak planks), multi-word search, delete/clear;
  - hotbar MRU (one slot per block, 9 max, cycling skips empty slots); clicking a slot doesn't reorder; B pick refuses fire, takes a wall torch (variant); the right stick click cycles; closing releases the pointer;
  - area row: 5 steps only with presets, a click requests the preset's value; Info tab and the Show HUD toggle;
  - `BlockEditor` with the registry: picking fire keeps the selection, a non-registry selection isn't sent;
  - toasts (fade in/out, 2.5 s, errors 3 s, same text restarts, at most 3, long text shortened), link watch (quiet first connect, one lost/restored each), error texts;
  - with the jar: the PNG decoder matches the test reader on 6 textures (RGBA, palette, grey, animated, colormap); icons: stone is an isometric cube (67 % coverage) lit top > left > right, the grass top is green, the torch is exactly its item sprite, redstone wire shows the dust item, the fence uses `oak_fence_inventory`, the chest (builtin item) its box, red stained glass is translucent red, no pack gives flat cubes; disk cache round trip and old-pack cleanup; the worker renders on request, reads `en_us.json`, and a second start reads all icons back from disk.
- Unity 6000.0.84f1 batch compile clean (no warnings in Assets), ShaderCheck 80 variants 0 errors, `TabletopBatch.BuildApk` succeeded (46 MB APK, not installed).

### Live server (own Paper 26.3, seed 12345, `tools/edit_check.py`)
`REGISTRY 1130 placeable blocks {building=433, colored=263, natural=228, functional=129, redstone=77, other=0}, 67 variants, 66903 bytes`, built in 100–170 ms at plugin start. `edit_check.py` 21/21 PASS: the 14 existing checks, then `welcome` → `block_registry` → `chunk_snapshot` in that order (66,929 bytes with the envelope), a `minecraft:diamond_block` (not in the region) placed from the registry comes back as a `block_update` with `palette_add` in 149–153 ms and a fresh connection's snapshot has it, fire is refused with `bad_state` ("minecraft:fire is not a placeable block (not in block_registry)"), and the diamond block is broken again. `Tests~ --live` adds "block_registry before the first snapshot (1130 blocks, 67 variants)".

### Icons (offline look check)
`Tests~ dotnet run -- --jar 26.3.jar --icons DIR --registry registry.json` (a saved `block_registry`) renders every block onto one contact sheet: 1130 icons in 416 ms on one desktop core (0.37 ms each, 2× supersampled 64 px). Checked by eye against the game's inventory look: full blocks, logs, stairs (profile on the left), slabs, fences (two posts), walls, trapdoors as 3D models; doors, bars, chains, panes, signs, candles, banners, beds, flowers, saplings, crops, torches, lanterns and rails as flat sprites; wool, concrete and glazed terracotta in the dye order; chests, copper chests and golem statues as their simple boxes; heads as flat-colour cubes. The sheet contains Mojang textures and isn't committed.

### Not verified (needs the headset)
Readability and size of the panel at arm's length (0.31 × 0.25 m, 30 mm cells, text 8–10 mm), the panel's position beside the left hand, the ray/trigger feel and the 1 cm rim, that the right stick never scales the table while on the panel, icon upload cost (CopyTexture, 6 per frame) and the worker's render time on the Quest's CPU, the small hotbar's position by the right controller, toast placement, the area-setup panel, and the Show HUD size.

## Area size presets and batching (PROTOCOL.md 4.25; Paper 26.3, seed 12345, 2026-10-14, no headset)

A separate headless test server (Paper 26.3 build 181, JDK 25, offline mode, normal world seed 12345, its own ports, default `max_region` 288×384×288 and `view_width` 256). The window is 96 blocks tall (y 32..128) plus the 16-block margin, so the region is 128 tall (y 16..144), centred on 0,0. Every client asks for `sections_v1` + `light_v1` + `biomes_v1`. All on the x86 box; frame rates on the Quest 3 are still to be measured (below).

### Tests

| Suite | Result |
|---|---|
| Plugin (JUnit) | 72 tests, 0 failures. New: `RegionRequestTest.viewWidthIsClampedAroundTheCentre` (the clamp fixture, uneven cuts rounded to whole sections, no margin, a small server max, `max_view_width` in the limits), `SectionCodecTest.resizeDeltasListOnlyTheNewRing` (grow lists the ring only, shrink lists nothing, a change inside is still listed, a partial edge section past the old region is listed) and `resizeFixturesRebuildFromTheFullSnapshot`. |
| Tools (pytest + ruff) | 63 passed, ruff clean. New: the grow/shrink resize deltas applied to the sections fixture (and a delta missing a ring section refused), `max_view_width` in the limit fixtures (`validate` now requires it in `welcome` and `region_info`). |
| Quest scripts (`Tests~`, .NET 8) | 373/373 PASS without a client jar, 421/421 with `--jar`. New in `AreaTests.cs`: presets against server limits, next/wrap, slider snapping, block-size scaling and its clamp, the resize plan (centred within 8 blocks and section-aligned for any view, server max, block cap shrinks the margin, a margin added to an old region), `NavState.TryIssue` and the view kept across grow/shrink, re-centring for a region elsewhere, `SectionPlan` for grow (all 72 old sections kept, only the 128-section ring meshed), shrink (nothing meshed, the 40 border sections refreshed) and listed deltas, the grow/shrink fixtures assembled in C#, batch keys (world-aligned, kept across shifts), position + offset = section corner for any y anchor, rebuild planning after shift/vertical shift/grow/shrink, `SectionMesh` (normals, Z mirror, winding, zero normal for a double-sided quad), `BatchData.Assemble` (every vertex kept, index ranges, bounds, water-back submesh) and `FrameStats`. |
| Unity 6000.0.84f1 batch compile | 0 errors, no warnings in `Assets/`. |
| `Editor/ShaderCheck` | 80 variants, 0 errors (no shader changed). |
| `TabletopBatch.BuildApk` (IL2CPP ARM64) | Build succeeded in 1:41, first try (not installed). |
| `Editor/BatchMeshCheck.Run` | 0 errors: four real sections (cubes, slabs, glass, plants, water) uploaded as one batch match the old per-section path vertex by vertex: positions, colours, UVs, triangles per submesh, bounds, and normals against `Mesh.RecalculateNormals` (27,748 vertices, 3,000 of them zero normals on double-sided plant quads, also zero in the batch). |
| `Editor/BatchMeshCheck.RunRenderer` | 8/8 PASS: the real `TabletopRenderer` driven frame by frame (atlas without pack) through first load, a one-section shift, a block edit (3 frames), grow 96 → 160, shrink 160 → 64, a vertical shift, a staged jump far away and the underwater submesh; each time what is on screen (vertex and triangle counts, summed positions) equals the model meshed under the re-key contract. 16 → 12 → 30 → 6 → 4 batches. |
| `Tests~ --live-resize` on the server | all PASS: 256 → 64 → 256 → 96, each a delta that re-keys (64 → 256: 2,304 sections sent, 288 kept, 679 ms; shrinking: 0 sections, about 150 ms), the view stays put, and a full resend (`bad_snapshot`) matches the delta-assembled region in blocks and light. |
| `tools/area_e2e.py --light` | `ok: true` in every run below: after each resize a fresh client's full snapshot equals the headset's delta-assembled grid, blocks and light. With `--server-clamp` a 320-wide request is narrowed to 256 (`REGION_CLAMP dims=352x128x352 -> 288x128x288`), still as a delta. |

### Resizing on the live server (`tools/area_e2e.py --widths 64,96,128,192,256,64 --light`, chunks generated)

The left-stick path, one click at a time:

| step | region | request → snapshot | resize delta | full snapshot of the same region | server walk (async) |
|---|---|---|---|---|---|
| start 64 | 96×128×96 | 164 ms | – | 336 KB, 1 part | 19 ms |
| 64 → 96 | 128×128×128 | 270 ms | 287 KB, 224 of 512 sections | 651 KB, 1 part | 32 ms |
| 96 → 128 | 160×128×160 | 361 ms | 482 KB, 288 of 800 | 1.12 MB, 2 parts | 51 ms |
| 128 → 192 | 224×128×224 | 712 ms | 1.55 MB, 768 of 1,568, 3 parts | 2.70 MB, 5 parts | 100 ms |
| 192 → 256 | 288×128×288 | 884 ms | 2.12 MB, 1,025 of 2,592, 6 parts | 4.91 MB, 13 parts | 186 ms |
| 256 → 64 | 96×128×96 | 134 ms | 17 KB, 0 sections | 336 KB | 19 ms |

Jumps: 64 → 256 in one go sends the whole ring, 4.4–4.5 MB (2,304 sections, 12 parts) in 1.6–1.7 s (2.6 s on the first run, which generated the chunks); 256 → 96 sends 18–33 KB (0–3 sections changed by the world in between) in 135–198 ms.

### Server main thread

- **Before:** taking the ChunkSnapshots for a 288×288 region (361 chunks with light and biomes) cost 2.99 ms in one tick with the chunks warm and 5.83 ms right after generating them, at or over the 3 ms budget, and every diff of a 256-wide area pays it again.
- **After** (`stream.snapshot-budget-ms` 1.5): REGION_SET at 288×288 takes 2.8–3.8 ms of main thread spread over 2–4 ticks; far away on fresh terrain (3000, −3000) 2.99 ms over 4 ticks (the chunk generation itself, 2.65 s, is off the main thread). 224 wide: 1.7–1.9 ms over 2 ticks; 160 and smaller: one tick, under 1 ms.
- **Holding a 256-wide area** with one client (`READS` lines): the world keeps the region dirty (water, kelp, mobs), so it is diffed about 4 times a second, 120 reads per 30 s at 3.0 ms each: 366–374 ms per 30 s, 1.2 % of the main thread. Max per tick 1.51–1.53 ms; right after a region change up to 4.2–4.7 ms in one tick (one slow chunk). The diff compares, palette remap and encoding stay on the worker and the outbound thread (read 170–190 ms, encode 23–30 ms at 288×128×288).

### Batching (`Tests~ --jar 26.3.jar --measure-batches DIR`, the full snapshots above with the pack and smooth light, x86, one thread)

`columns` = section columns per batch side (a batch spans the full height). Draw calls count opaque + transparent submeshes (the water's inside faces add one per batch with water while the eye is under water).

| window (region) | non-empty sections | faces | vertices | per-section meshes: renderers / draw calls | 1×1 | **2×2 (default)** | 4×4 | GPU vertex + index data |
|---|---|---|---|---|---|---|---|---|
| 64 (96×128×96) | 108 | 72k | 310k | 108 / 206 | 36 / 72 | **16 / 32** | 4 / 8 | 11 MB |
| 96 (128×128×128) | 196 | 119k | 506k | 196 / 374 | 64 / 128 | **16 / 32** | 4 / 8 | 18 MB |
| 128 (160×128×160) | 328 | 190k | 805k | 328 / 605 | 100 / 200 | **36 / 72** | 16 / 32 | 29 MB |
| 192 (224×128×224) | 706 | 426k | 1.78M | 706 / 1,230 | 196 / 392 | **64 / 128** | 16 / 32 | 65 MB |
| 256 (288×128×288) | 1,223 | 702k | 2.92M | 1,223 / 2,058 | 324 / 645 | **100 / 200** | 36 / 72 | 106 MB |

- **Rebuild cost.** The biggest 2×2 batch has 85k vertices (3.2 MB); an edit in the middle of the area re-uploads its batch, 1.35 MB. With 4×4 the biggest is 241k vertices (8.9 MB) and an edit re-uploads 3.2–5.2 MB, which on the headset is several ms of copying per edit (20 per second while a trigger is held). With 1×1 a one-section shift rebuilds 18 batches (19 MB at 256). 2×2 is the default; `"batch_columns"` in `tabletop.json` switches it for headset comparisons.
- **Assembly** of all 100 batches at 256 takes 8–9 ms of worker time here, the biggest 0.3 ms; meshing the sections themselves takes 1.5 s on one x86 thread (unchanged; the Quest spreads it over 3 workers).
- **Counts depend on alignment:** batches are world-aligned, so an 18-section-wide region covers 9 or 10 batches per side.
- **Faces, not draw calls, are now the likely limit:** 702k faces / 2.92M vertices at 256 is about 3.7× the 128 preset, and the face soft cap of area setup (250k) is passed from 192 up. The presets skip that cap on purpose; the headset numbers below decide what stays a preset.

### What to measure on the headset (per preset)

For each width W in 64, 96, 128, 192, 256: put `"area_width": W` in `tabletop.json` (same table, same spot; the block size is fitted to the table so every preset covers the same area), restart the app and read `adb logcat -s Unity | grep Tabletop` next to VrApi's FPS lines:

1. `AREA_LOADED width=W ... load_s=...` (time until the area is complete) and its `renderers`, `draw_calls~`, `verts`, `tris`.
2. 30 s of `PERF` lines standing still, then 30 s gliding (left stick) and 30 s editing (hold a trigger): `fps`, `frame_ms p50/p90/p99/max`, `over15ms`, and the app GPU time in OVR Metrics.
3. Click through the presets once (64 → 96 → 128 → 192 → 256 → 64): the table never empties, the area grows/shrinks around the view, its footprint on the table stays the same (`AREA_RESIZED ... block ... mm in ... ms`), and the status line shows `AREA W wide ... server max 256`.
4. Look for batching artefacts: water, leaves and glass across batch borders (world x or z a multiple of 32), the underwater view (inside faces), the cut faces at the window after a resize, the light at the new border after shrinking, and edits right at a batch border.
5. At 256, repeat 2 with `"batch_columns": 4` to see whether fewer draw calls beat the bigger re-uploads.

## Sessions, prefs, discovery and the keyboard (0.3.1 comfort; Paper 26.3, 2026-10-14, no headset)

A separate headless Paper 26.3 test server (JDK 25, offline mode, seed 12345, its own ports, ViaVersion/ViaBackwards for the mineflayer player), the region the earlier runs left (80×96×80 with a 16-block margin).

### Tests

| Suite | Result |
|---|---|
| Plugin (JUnit) | 87 tests, 0 failures. New `LanBeaconTest` (8): the probe fixture is recognised and other packets aren't; only private, link-local and loopback senders get an answer; the reply equals `tools/fixtures/discovery_reply.json` (name cut to one 48-character line); 20 replies per second; a real UDP probe on loopback gets the reply and junk gets nothing; the `Headset URL` lines (192.168 first, then 10, then 172.16/12, interface names; a specific bind host alone); the constant-time token check; `PlayerFilter` (the margin on x, y and z; spectator, invisible and vanished are never shown). `FixtureTest.regionChangedFixtureCarriesLimits`. |
| Tools (pytest + ruff) | 80 passed, ruff clean. New: discovery probe/reply fixtures (no envelope) and bad replies (port 0 or 70000, a path without `/`, a non-bool token, `v` 2), `region_changed.limits.build_height`. |
| Quest scripts (`Tests~ --jar 26.3.jar --entity-models …`) | 629/629 PASS (531 before), new in `Tests~/ComfortTests.cs`: |

- **Keyboard** (`World/KeyboardModel.cs`): 32 keys on the letter layer and 36 on `?123`, inside their box and not overlapping; every digit and the symbols commands need are on `?123`; one-shot shift, caps lock on the second press, off on the third; a full server URL and a command with every symbol typed key by key; enter, del and clear; search collapses spaces; max length; the field shows its tail with the cursor.
- **Info tab** (`World/BlockMenuModel.cs`): Show HUD, Reset view and the Server button below the status, nothing overlapping; the Server field opens with the keyboard and Find / Connect / Cancel (0.35 m tall panel); typing goes to the field, not the search; enter connects to the normalised address; found servers are buttons, hovering shows the URL, a click connects; a bad address is refused and the field stays open; cancel keeps the old server; leaving the tab closes the field; enter in Search picks the first result.
- **prefs.json** (`World/AppPrefs.cs`): round trip; the tabletop.json values it saw; a broken file is reported (the app starts fresh); the shared-key rule (a new or edited tabletop.json value wins, an unchanged one doesn't); the table pose relative to the table anchor round-trips and follows a moved and turned table; yaw like `Quaternion.Euler`; debounced saving (1.5 s quiet, 10 s at most, never twice, flush); the Server field rewriting tabletop.json keeps every other key and refuses to silently replace a broken file; the hotbar and tab restore (empties skipped, a category tab waits for the block list).
- **Addresses and discovery** (`Net/LanDiscovery.cs`): `192.168.1.50` → `ws://192.168.1.50:8765/tabletop`, typed ports kept, full URLs kept, junk refused; the reply fixture parsed with the sender's address; the limited + /24 broadcast targets; a UDP probe answered by a loopback responder (a duplicate reply listed once); the TCP fallback finds the one open port among 254 loopback hosts in 12 ms.
- **Link** (`Net/LinkClient.cs`, against a small RFC 6455 server inside the test): a full snapshot drops the queued snapshots and updates (a delta drops nothing) and the queue is bounded; pongs measured inside the client and not queued; shutdown sends `avatar_config` then `bye`, clears the RTT and doesn't reconnect; a server that goes silent after `welcome` is noticed after `DeadSeconds` and reconnected; close 4003 stops the retries; a URL without `ws://` doesn't start; nobody listening gives an error status and no RTT.
- **Session helpers** (`World/SessionState.cs`): the setup card after 5 s without a first connection or at once on a fatal close, never for a later drop; the offline badge and hidden figures after 1.5 s; a new server can show the card again; the card text (URL, status, settings path, the Info > Server and QUEST_SETUP Part D pointers, "not retrying" on 4003); `Loading area N %` only for big loads, then `Area loaded` once; keyed toasts update in place, custom lifetimes; hold A fires once after 0.5 s, a tap never, a press that starts on or passes over the menu never.
- **Orientation** (`World/NavState.cs`): the 8-way compass (north = -Z), the readout (`58 16 41  facing N` for a known view), Reset view's re-centring, the badge's point on the table edge toward the head.

Unity 6000.0.84f1 batch compile clean (no warnings in Assets), ShaderCheck 80 variants 0 errors (no shader changes), `TabletopBatch.BuildApk` succeeded (46 MB APK, il2cpp in 81 s, not installed). The Android manifest is unchanged: discovery needs no new permission (PROTOCOL.md §7).

### Live server
- **Startup log:** `Link listening on ws://0.0.0.0:8802/tabletop`, then one `Headset URL: ws://<lan-ip>:8802/tabletop (<interface>)` per private IPv4 (two on the test box: the Ethernet adapter and a VPN interface), then `LAN discovery on UDP 8802 as "Comfort Test"` (the MOTD).
- **Discovery** (`Tests~ --discover 8802`): the probe to loopback and to the LAN broadcast found the server twice (127.0.0.1 and the LAN address) within 1.5 s: name, plugin 0.3.0, mc 26.3, players. With `auth.token` set and `/tabletop reload`, the replies say `token`.
- **Dead link** (`Tests~ --live-link`, the server frozen with `kill -STOP` for 12 s): `no answer for 6 s: reconnecting` exactly 6.0 s after the freeze, the RTT back to -1, connection attempts while frozen end with `no answer within 5 s` (the kernel accepts TCP, nobody answers the upgrade), and a new `welcome` 0.5 s after `kill -CONT` (12.5 s in all).
- **Wrong token** (`--live-token`, `auth.token: test-secret`): close 4003 with one `auth_failed` error, then no reconnect for 3 s (the server log has one `Link closed … code=4003`).
- **Players and limits** (a Python client plus the mineflayer player, console commands through the FIFO), 9/9: shown in creative inside the region; hidden in spectator; shown again; hidden with the invisibility potion; shown after `effect clear`; hidden 18 blocks below the region's 16-block margin (y 10 for a region from y 44); shown again at y 100. `region_request` to `world_nether` → `region_changed` with `limits.build_height` [0, 256], and back to the overworld with [-64, 320].
- `Tests~ --live` still passes (its RTT now comes from `LinkClient`).

### Not verified (needs the headset)
- Headset off and on: the giant disappears at once in the Java world, the app reconnects on its own when the headset is back, the old table shows meanwhile, no "Link lost" toast, `LINK paused (headset off)` in the Info tab; whether Quest sends `HMDUnmounted`, `OnApplicationPause(true)` or both, and when.
- A Wi-Fi drop or a server stop while playing: the red badge at the table edge toward you, players and mobs hidden after 1.5 s and back after reconnecting.
- First run with no server reachable: the setup card after about 5 s (size and position), hidden while the menu is open; Find servers on the real LAN (UDP broadcast from the Quest, reply through the PC firewall; the TCP fallback when UDP 8765 is blocked); typing an address on the keyboard and connecting; the toasts for a missing `pack.zip` / `entity_models.json`.
- prefs.json across restarts: block size, table position and turn on the same MRUK table, hotbar, selection, menu tab, Show HUD; first run fitting the area to the table.
- Hold A (tick, then buzz after 0.5 s; a tap does nothing; nothing on the menu); a late MRUK scene keeping a manual placement.
- The N marker, the coordinates line under the small hotbar, Reset view; player labels disappearing outside the window; the `Loading area N %` toast on a width change.

### Known gaps
- Players and entities are hidden while offline rather than faded: neither the player skin shader nor the entity shader has a fade, and hiding needs no shader change.
- The headset can't type a token yet (only the address); a server with `auth.token` needs it in `tabletop.json`.
- The table pose is remembered only relative to an MRUK table; without one the miniature starts in front of you each session (a spatial anchor is future work).
- Named entities' labels outside the window are the entity renderer's (not changed here).

## Building like vanilla (block editing, PROTOCOL §4.20; Paper 26.3, seed 12345, 2026-10-14, no headset)

Replaceable cells, vanilla placement orientation, two-block structures, placed states, mobs in the way, connections, edits across deltas, the placement ghost and readable refusals. Rules taken from the 26.3 classes with `javap` (`BlockPlaceContext`, `StandingAndWallBlockItem`, each block's `getStateForPlacement`/`canBeReplaced`, `Direction.orderedByNearest`, `RotationSegment`); the replaceable list from the `Properties#replaceable` calls in `Blocks` (hand-written into the code, nothing copied from the jar).

### Tests

| Suite | Result |
|---|---|
| Plugin (JUnit) | 84 tests, 0 failures. New `EditRulesTest` (6): readable labels, state properties, vanilla replaceability (one snow layer, sculk vein, never by the same block), water sources (falling water isn't one), two-block partners (doors, double plants, beds both ways; not stairs, trapdoors or the pitcher crop), placing from the lower half or the foot. |
| Tools (pytest + ruff) | 71 passed, ruff clean. `error_edit_denied.json` now carries a readable message ("stone is in the way"). |
| Quest scripts (`Tests~`, .NET 8) | 634/634 PASS with `--jar` and `--entity-models` (about 100 new in `EditTests.cs`): the look (yaw, horizontal direction, nearest-direction order as `Direction.orderedByNearest`), Java-style rotation rounding, wall forms by name checked against the registry; per family: log axis, furnaces/anvils/campfires, standing vs wall torch/redstone torch/sign/banner/skull/coral fan, a torch with nothing to hold it refused, hanging signs under a full block (4-way) and on a wall (along it), button/lever/grindstone floor/wall/ceiling, bell floor/single wall/double wall/ceiling, slab/stair/trapdoor top halves from the underside and the upper half of a side, end rods (flipped on an end rod), lightning rods, amethyst, shulker boxes, hoppers, observers, pistons, dispensers, barrels, crafter orientation, door lower half + upper, hinge from the hit point and against a wall, no room above, bed foot + head along the look, tall grass and small dripleaf, lanterns, vines, glow lichen, cocoa, ladders, persistent leaves, rails; replaceable cells (aim at short grass → its cell, next to one snow layer → replaced, two layers refused); door/bed breaks guess both halves and a refused one puts both back; picks take the registry default (wall torch → torch, a door's open top half → the door, wheat → age 0, water cauldron → cauldron), without a registry lower half/foot/age 0/unpowered/dry; **optimistic edit → sectioned delta → error: the moved guess is reverted** (a guess in a listed section is dropped, one in a copied section still expires after 3 s); a block new to the palette isn't sent twice within a second, the `block_update` answers it, a shown block can be broken at once; the preview ghost world (bed halves) and the target readout. |
| Unity 6000.0.84f1 | Batch compile 0 errors, no warnings in `Assets/`; `ShaderCheck` 80 variants, 0 errors (no shader changed); `TabletopBatch.BuildApk` succeeded (46 MB, not installed). |

### Live (own Paper 26.3 server, seed 12345, region origin (0, 20, 0) 32×64×32: at the default (0, 60, 0) the region is open ocean with the sea floor below it)

- `tools/edit_check.py --url ws://127.0.0.1:8801/tabletop --fifo tools/run/edit/stdin.fifo`: **51/51 PASS**. The earlier checks (the messages now read "gold block is in the way" and "fire is not a placeable block (not in block_registry)"), then on a 5×5 stone platform built 8 blocks under the region's top (and removed again): short grass and a single snow layer replaced, two layers refused ("snow is in the way"), short grass on stone refused ("short grass can't stay there"), leaves sent with `persistent=false` stored `persistent=true`, a `waterlogged=true` slab placed in air stored dry; door both halves, breaking the top removes both, a door sent as its top half placed from the bottom, bed foot + head and breaking the head removes the foot, tall grass both halves and replacing its lower half removes the top, a door without room above refused, a door on the region's top layer refused ("its other half would be outside the area"); fence, glass pane, iron bars and cobblestone wall next to their own kind connect both ways, a fence beside a stone block connects (`east=true`), a stair against a perpendicular stair gets `shape=outer_right`; with the console: a NoAI cow in the cell refuses stone ("a cow is in the way") but takes a torch, and with a 3-block world border around the platform a cell outside it is refused ("outside the world border") while one inside works.
- Baseline for the connections (the audit's "probably don't connect"): plugin 0.3.0 from master on the same server gave a fence next to a fence `west=true` (the neighbour's own shape update cascades back), but a fence beside stone stayed `east=false` and a stair against a perpendicular stair stayed `shape=straight`. With this build the server log says `EDIT_SHAPES on: placed blocks connect to their neighbours (vanilla Block.updateFromNeighbourShapes)` and both are right.
- `Tests~ --live-edit ws://127.0.0.1:8801/tabletop`: 7/7 PASS (gravel broken and put back, a gold block new to the palette not guessed and confirmed in 152 ms, the denied command block reverted from the error's `pos` in 51 ms; the refusal now reads "command block can't be placed from the headset (edit.deny)").
- Main thread per edit (105 edits, `EDIT ... (ms)`): median 0.13 ms, p90 0.38 ms, max 3.9 ms (the first place, which also sets up the reflection). A door or bed logs `cells=2`.

### Not verified (needs the headset)
The ghost's look in passthrough (alpha 0.4 of the block's own textures; does it read as "here", is it visible on bright terrain), the red refusal tint and outline, the target readout's size and position under the selected block, the miss stub, and how the orientation feels with the controller ray as the look (e.g. stairs and furnaces when pointing steeply down at the table: the horizontal facing comes from the ray's small horizontal part).

## Looking like vanilla 26.3 (0.3.1 rendering; Paper 26.3, seed 12345, 2026-10-14, no headset)

Sources: the 26.3 client jar's assets and the renderers' bytecode (`javap -p -c` on the unobfuscated jar), never copied into the repo. Golden numbers for vanilla's randomness were computed by calling the real 26.3 classes (`Mth.getSeed`, `RandomSource.createThreadLocalInstance`) from a scratch harness.

### Tests

- `quest-app/Tests~ dotnet run -- --jar <26.3 jar> --entity-models <entity_models.json>`: **570 PASS** (531 before). New, mostly in `Tests~/RenderTests.cs`:
  - Randomness at six positions (including ±30,000,000) against the real classes: `Mth.getSeed`, the Random LCG, `nextInt(4/5/16)`, the multipart part seed and picks, XYZ offsets. Lily pads (4 turned variants), fire (2 × 4 combinations on the ground, 4 on two walls), chorus plants (5, weights 2+1+1+1); a poppy at (1000, −12, −999) meshes 0.05 / 0.117 blocks off its cell.
  - Random UV tables: stone (mirrored, all sides), dirt and grass top/bottom (turned), netherrack (16), deepslate `axis=x`, sand; every table entry reproduces that variant's baked face UVs; a 4×4 stone floor still merges into 6 quads.
  - Tints: exactly vanilla's 28 tinted blocks over all 35,723 states (probe); cherry/pale oak leaves, bamboo, bamboo sapling, lava/powder snow cauldrons and the stonecutter untinted; pink petals tint only their stems.
  - Grass sides: the overlay is a second quad on the same corners after the base (10 quads for one block, 2 blocks still merge); snowy grass has none. Only `grass_block` has an overlay among all 26.3 states.
  - Lighting in a closed dark stone box: magma's top full bright (255), the stone walls dark (20), the open eyeblossom's emissive faces glow, its plain faces dark; doors and rails flat, active sculk sensors full bright; the heavy core resolves its `"all"` slot.
  - Pistons: push (head + 2 blocks), retract and sticky pull, with the change sets a Paper 26.3 server sends (recorded live, below); the push meshes the head and both blocks in their moving cells.
  - Mips: 16 px → 5 levels, linear-light means (black + white → 188), solidified cut-outs, a poppy keeps 0.50 coverage at 2×2 (a box filter: 0), oak leaves stay closer to their level-0 coverage.
  - Entity roots (`EntityTests.cs`): arrow, trident, end crystal and leash-knot figure transforms equal the vanilla `PoseStack` through the Z mirror for 4 yaws × 3 pitches (worst error 2.6e-7 blocks); an arrow flying east points east, pitched up points up; the crystal glass bobs 4.8-11.2 px below its pivot. Armour stand fixture: arms, base plate, pose, parts (default pose, sticks follow the body, the plate turns against the yaw, no hat).
- `server-plugin ./gradlew build`: 79 tests, 0 failures (new `EntityDeltaTest.armorStandFixture`). `tools`: pytest 73 passed (new `test_entities_armor_stands`), ruff clean.
- Unity 6000.0.84f1 headless: compile clean, `ShaderCheck` 80 variants / 0 errors (Vulkan + GLES3, stereo keywords), `BatchMeshCheck.RunRenderer` 8/8 PASS.

### Live server (own Paper 26.3, ports 25603/8803, a scratch recorder)

- Armour stands summoned with `{ShowArms:1b,NoBasePlate:1b,Pose:{Head:[10f,20f,0f],RightArm:[-110f,25f,10f]}}`, a plain one and `{Invisible:1b,Small:1b}`: the `entities` entries carried `arms`, `stand_pose` (the two set parts, the rest at vanilla's defaults), `base_plate` only on the other two, `small` + `invisible` on the third.
- Pistons: every move is one `block_update` when it starts and one 2 ticks (0.2 s) later. Extending (piston, stone, 2 × dirt in front): `piston[extended=true]` at the base, `moving_piston[type=normal]` on the head cell and the three destination cells, no separate air changes. Retracting: the base cell becomes `moving_piston`, the head cell air. Sticky pull: base `moving_piston[type=sticky]`, the head cell `moving_piston[type=normal]` (the pulled stone), the stone's cell air. `PistonGhosts` handles exactly these.

### Faces (`Tests~ --measure-batches`, pack, smooth light, the same snapshots before and after)

| region | master 9f0e1ab | 0.3.1, `leaves_inner: false` | 0.3.1 (default) |
|---|---|---|---|
| 160×128×160 at 0,0 (ocean, dark forest) | 190,494 | 191,239 | 192,096 (+0.8%) |
| 128×96×128 forest (−224, 288) | 163,958 | 170,846 | 177,864 (+8.5%) |
| 128×96×128 dark forest (96, −32) | 104,008 | 106,416 | 106,936 (+2.8%) |

Without the leaves change the difference is the grass side overlays (one extra quad per merged grass side; the forest: +6,920 with only that commit); faces between identical leaves add 0.3-4.1%. `leaves_inner` stays on by default (vanilla's look).

### Texture memory

A 16 px layer is 1,024 bytes, 1,364 with mips. Real regions need 55-98 layers (73-130 KB with mips, +18-32 KB); every block texture and animation frame of the 26.3 pack would be 1,945 KB → 2,590 KB. Animated textures copy every mip level per frame change (5 small copies instead of 1).

### Not verified (needs the headset)

Everything above is engine-free or editor-only. The look of the per-block texture rotation and the mips (and their GPU cost: the block shaders now sample with explicit gradients), grass sides in different biomes, offsets, emissive blocks, leaves, pistons and the entity roots must be checked on the Quest (checklist in the 0.3.1 merge notes).

### Known gaps (deferred)

- Greedy cubes pick their rotation from a hash of the world block, not the Java client's exact choice (that needs 64-bit integer maths per pixel); model blocks (lily pads, fire...) match exactly.
- Blocks with a light level (glowstone, torches, lanterns...) still get smooth light and AO; vanilla lights every light-emitting state flat. The server sends no per-state light level, and only magma and sculk sensors (emissive rendering) are handled.
- Pistons: blocks jump to their new cell instead of sliding for 2 ticks.
- 26.x `shade_direction_override` (flowerbed stems, emissive crosses) is not applied.

## Chat, commands and server status (`chat_v1` / `command_v1` / `status_v1`, PROTOCOL §4.26–4.28; Paper 26.3, 2026-10-14, no headset)

### Tests

| Suite | Result |
|---|---|
| Plugin JUnit (`./gradlew build`) | 109 tests after merging 0.3.1, 0 failures. New: `CommandPolicyTest` 9 (namespaces, aliases, every `execute`/`return run` split including a player named "run", nesting limit, allow list, unknown roots, the token bucket's burst), `ChatTextTest` 5 (name and text sanitising, MiniMessage tags typed on the headset stay literal, the gold prefix, red feedback as errors), `ControlFixtureTest` 8 (every new fixture rebuilt with the plugin's builders and parsers) |
| Python (`pytest -q`, `ruff check .`) | 118 passed after merging 0.3.1, ruff clean: the 17 new fixtures validate, plus 21 tests for kinds, suggestions applied from `start`, `ref_seq` pairing and 15 malformed variants |
| Tests~ (`dotnet run -- --jar … --entity-models …`) | ALL PASS, 735 checks after merging 0.3.1; 106 new in `ControlTests.cs`: fixtures through `Net/ControlProtocol.cs`, `ChatLog` (formatting, 200-line ring, wrist lines fading after 10 s, wrap, our own echo by session), `ChatBubbles` (uuid only, 3 per player, 6 s + 0.05 s/char), `ConsoleModel` (history up/down and its JSON, 100 kept, completion debounce, stale replies dropped, chips from `start`, grouped output), `OperatorPanelModel` (every tab inside the panel without overlaps and hit tests round-trip, templates, greyed buttons without `command_v1`, pointer checks, kick with a reason, tap/hold X, stick scrolling), and 16 typing through the shared `KeyboardModel` (shift, ?123 symbols, delete, clear, enter = send/run/kick, `max_len`, history recalled into the field) |
| Unity 6000.0.84f1 headless compile, `ShaderCheck` | clean (no errors, no warnings in `Assets/`); 80 shader variants, 0 errors |

### Live server (own Paper 26.3, seed 12345, `auth.token` set, ViaVersion/ViaBackwards 5.12.0 for the bot)

`tools/control_check.py --url ws://127.0.0.1:8811/tabletop --token … --mc-port 25611 --console tools/run/talk/stdin.fifo`: **50/50 PASS**, three runs in a row, and again after merging 0.3.1:
- caps: a wrong token gets `auth_failed`; the headset gets `chat_v1`, `command_v1`, `status_v1`, `welcome.chat.name` `TalkCheck` (from `operator_name` "Talk Check!") and the quick buttons; a headset that didn't ask for `command_v1` gets `command_denied`.
- commands: `time set day` → `Set minecraft:overworld to time marker minecraft:day` (26.x feedback); `/minecraft:time set noon`; `time sett` → `ok:false`, `Incorrect argument for command at position 5: time <--[HERE]` (unwrapped from the `CommandException` that `dispatchCommand` throws for this sender); an unknown command; `stop`, `minecraft:stop`, `execute as @a run stop`, `execute as run run stop`, `op`, `whitelist off` all `command_denied`; a newline and 300 characters `bad_message`.
- `at`: `setblock ~ ~ ~ minecraft:gold_block` at a pointed block lands exactly there (`execute if block x y z` passes), `~ ~ ~` is that block, `summon … ~ ~1 ~` puts the entity on top of it, an unknown world is `world_not_found`. Plugin commands run through `execute … run` too (`execute in minecraft:overworld positioned 0 70 0 run tabletop status` worked in the first probe).
- completion: `gamerule adv` → `advance_time`, `advance_weather` from 9; `ti` and `/ti` → `tick, time, timings, title` from 0 and 1; `time set ` → `minecraft:day…` from 9.
- `say` from the console tab goes out as the giant's chat; `server_status` every 2.0 s (578 bytes with three worlds); `world_info` with 58 game rules by their 26.x names; `gamerule advance_weather` toggled through a command and back shows in `world_info`.
- chat with a mineflayer Java player: join → `TalkWalker joined the game`; `players_info` lists the bot (uuid, survival, 20/20, food 20); the bot's chat reaches the headset with its uuid; the headset's `hello bot <red>…</red>` reaches the bot as `[Giant] TalkCheck: hello bot <red>…</red>` (tags literal); `/tabletop tell` → `whisper`; `advancement grant` → `TalkWalker has made the advancement [Stone Age]`; `kill` → `TalkWalker was killed`; the giant's bubble is a tagged text display (`execute if entity @e[type=text_display,tag=tabletop_avatar]` passes), `entities` never lists it, and it's gone after 5 s; console `say` → kind `say` from `Server`; with the bot opped, the bot sees `[Tabletop: /time set day]`; quit → `TalkWalker left the game`; a read-only headset sees the operator lines too.
- rate limit: 10 commands at once → 5 run, 5 `rate_limited`.

`Tests~ --live-control ws://127.0.0.1:8811/tabletop TOKEN`: 8/8 PASS with the app's own `LinkClient.SendTracked`, `ConsoleModel`, `OperatorPanelModel` and `ChatLog` (result matched by seq, denied root, chips `advance_time,advance_weather`, world/players/status parsed, the chat echo marked as ours).

Main thread (before the merge): commands median 0.85 ms (26 commands; the first `time set day` 22 ms while classes load); a `server_status` build 0.19 ms (`/tabletop status` prints the average). The command round trip on localhost was 72 ms including the check's pacing.

### Not verified (needs the headset)

The panel's look and reach beside the left hand, X tap/hold and the ≡/X switch, the right ray, trigger, stick scroll and stick-click Tab on the panel, the wrist lines' placement and fade, bubbles over player figures at different table scales, the toasts for joins and deaths, `history.json` surviving an app restart, and typing on the panel keyboard.

### Known gaps

- `/msg`, `/tellraw` and `say`/`me` inside `execute … run` fire no server event, so they don't reach the headset (and a headset's `execute … run say` shows as `[FeedbackForwardingSender]` in game).
- Late `command_output` (async plugin commands) and plugin `BroadcastMessageEvent`s are covered by code review only: no command on the test server answers late, and nothing there broadcasts.
- `ok` is "ran without red feedback": a vanilla command that succeeds without feedback shows "(done)"; a plugin that reports failure in another colour counts as ok.
- The pointer for commands is block editing's target, so it needs `edit_v1` on the server.

## Block entities (`block_entities_v1`, PROTOCOL §4.29; Paper 26.3, 2026-10-15, no headset)

Chests, shulker boxes, heads, banners, bells, decorated pots, conduits, copper golem statues, lectern and enchanting books, end portals and gateways, sign text, beacon beams, spawner mobs and campfire/shelf/vault items, drawn like vanilla 26.3's block entity renderers (ARCHITECTURE.md §3.10 and "Block entities" under §4).

### Tests

| Suite | Result |
|---|---|
| Plugin JUnit (`./gradlew build`) | 123 tests after merging the chat work, 0 failures. `BlockEntityJsonTest` 6 (both fixtures from the builders, defaults left out, `BlockPos`-style position keys for negative coordinates, per-chunk deltas add, change and remove, pruning on a region change) and `FixtureTest` (both fixtures). |
| Python (`pytest -q`, `ruff check .`) | 131 passed, ruff clean: both fixtures validate, `BlockEntityMirror` applies, prunes and resets, 7 malformed entries are refused. |
| Tests~ (`dotnet run -- --jar 26.3.jar --entity-models entity_models.json`) | ALL PASS, 943 checks; 67 in `BlockEntityTests.cs`: kind by block id, which blocks are code-drawn, the vanilla transforms (chest, shulker box, floor and wall skulls, banner, pot, statue, sign text on wall, standing and hanging signs, campfire and shelf item slots, spawner mob; `--be-probe` prints them, and they were compared once with the matrices the 26.3 client's renderer code computes), parts and textures per state (double chest halves, ender, trapped and copper chests, dyed shulker boxes, player head skins, mob heads with the dragon jaw, banner flags, pot sherds, statue poses, lectern book, conduit), both fixtures through `BlockEntityStore`, banner compositing, beacon beam sections (stained glass averaging, blocked beams), the section scan, model bounds baked from the generated layers (chest, double chest, shulker, skulls, dragon head, banners, bell body, pot, statue, lectern book), the pack's sign font (2,412 glyphs, widths, wrapping, colours, glowing outline) and the terrain baker leaving code-drawn blocks out while previews keep a box. The model tests SKIP with an `entity_models.json` from before this work. |
| Unity 6000.0.84f1 headless compile, `ShaderCheck` | clean; 120 shader variants (with `BlockEntityFx` and `EndPortal`), 0 errors. `BatchMeshCheck.RunRenderer`: all PASS. |
| `TABLETOP_PACK=26.3.jar TABLETOP_ENTITY_MODELS=entity_models.json Unity … -executeMethod TabletopCraft.EditorTools.BlockEntityCheck.Run` | 11/11 PASS: the real `BlockEntityRenderer` on a scene of 35 block entities finds them all (75 ms), 39 instances in 37 groups (42 draw calls, 978 triangles), 80 sign text quads, 9 end portal and gateway faces, a 16-quad beacon beam, the spawner's mob figure, a chest kept in place by a 16-block re-key, and a broken chest removed. |

### Live server (own Paper 26.3, seed 12345, ports 25612/8812)

`SERVER_FIFO=run/blocks/stdin.fifo TABLETOP_URL=ws://127.0.0.1:8812/tabletop python tools/block_entities_check.py 200 150 200`: **17/17 PASS**, again after merging the chat work. A sign (both sides, colour, glowing), a hanging sign, a banner with two patterns, a player head with a profile, a pot with sherds, a spawner (zombie, size 0.6×1.95), a beacon on an iron base (tier 1 after 1.0–1.6 s), a campfire and a shelf with items all arrive within 0.15–1.0 s of placing them; a chest sends nothing; a sign changed with `/data` (no event) arrives with the round robin in 0.15–0.45 s; a removed banner is removed in 0.2–0.45 s; the first message carries `reset`, and so does the one after subscribing the same box in `world_nether` (0.35 s, nothing left). A head placed with only a name (`jeb_`) gets its skin 3.0 s after placing on a freshly started server (offline mode: Paper's lookup gives no textures, so the name goes through the public profile API first, as for the giant's `skin_player`), 0.1 s once cached; the check prints this as INFO since it needs internet. About 10 messages, 3.4 KB for the whole run.

Server main thread (`BLOCK_ENTITIES` log line, a 288×64×288 region that includes a stress box of 2,752 block entities, 840 of them with data): steady average 0.083 ms per tick, max 1.96 ms (the slowest single chunk read 1.9 ms: a chunk with 256 player heads). The first 30 s after start peaked once at about 18 ms (the first `createEntity` for a spawner's mob size and cold chunks). The initial burst for the 840 entries is 115 KB.

### Faces and draw calls (`Tests~ --jar 26.3.jar --entity-models entity_models.json --measure-blocks DIR`, saved snapshots)

| Snapshot | Block entities | Terrain faces with boxes → without | Instanced groups / draw calls | Block entity triangles |
|---|---|---|---|---|
| Stress box 32×16×32 (1,088 chests, 512 banners, 512 skulls, 256 signs, 256 shulker boxes, 64 bells, 64 pots) | 2,752 | 22,686 → 3,840 | 15 / 18 | 75,008 |
| Village 128×48×128 | 2 bells | 106,917 → 106,893 | 1 / 1 | 48 |
| 256×64×256 | 2 bells, 1 spawner | 519,150 → 519,126 | 1 / 1 | 48 |

Draw calls count chunks of 256 instances; with single-pass stereo each instance is drawn twice in the same call.

### Offline look check

`Tests~ dotnet run -- --jar 26.3.jar --entity-models entity_models.json --be-scene scene.json` places one of each kind as `BlockEntityRenderer` does, and `tools/render_mesh.py scene.json 26.3.jar out.png --view se|nw` renders it. Checked by eye: every kind shows its model with the jar's textures, in place and facing the right way (chests, double chest halves, ender and copper chests, dyed shulker boxes, skulls and the dragon, piglin and player heads, banners on the pole and on the wall bar, the bell body, pots with sherds, the conduit, copper golem statues in their poses, the open book, campfire items). The scene uses untinted banner flags and base skins. The images contain Mojang textures and aren't committed. (A Unity offscreen render in batch mode didn't draw the custom shaders, so it isn't used.)

### Not verified (needs the headset)

The look of every kind at table scale, lighting at night and in caves, the end portal starfield, sign text legibility, beacon beam colours, the spawner mob spin, the enchanting book bob, clipping at the window edges while gliding, re-keying on a glide or resize, edits adding and removing block entities, the hand preview and placement ghost boxes, and FPS and draw calls in a village (`RenderMeshInstanced` under single-pass instanced stereo).

### Known gaps

- Maps in item frames (`map_data`) and the item in a brushable block aren't drawn.
- The enchanting book stays closed and idle (vanilla opens it toward a nearby player); banner flags don't sway; the conduit's wind isn't animated.
- Sign text is plain: styles and per-line colours from commands are dropped.
- A player head placed with only a name gets its skin after a lookup (about 3 s the first time); a server without internet access sends no skin, and the head shows the default one.

## Faster gliding at wide areas (0.4.0 nav; Paper 26.3, seed 12345, 2026-10-14/15, no headset)

The headset at 256 wide (region 288×160×288, 16-block margin) glided at 4–9 blocks/s (1–2 cm/s on the table): the stream cap allowed 12 blocks of runway per round trip, and the round trip (until every new section was meshed) was 1–1.5 s. Measured on an own Paper 26.3 server (2 GB heap, own ports, seed 12345, chunks generated, every client with `sections_v1` + `light_v1` + `biomes_v1`), on an x86 PC (Ryzen 7 9800X3D). "Before" is master 9f0e1ab (plugin and app logic), built from the same sources.

### Tests

| Suite | Result |
|---|---|
| Plugin (JUnit) | 125 tests, 0 failures. New: `RegionMergeTest` (9: a one-section shift of 288×160×288 reads one column of 180 sections and 18 chunks; the merged grid equals a full read for shifts on every axis, two sections, grow, shrink, an unaligned move and partial edge sections, light included; the sections read are exactly what a delta lists; shrinking reads nothing; when a base is usable; base biome lookup; section size estimates; mapped section encoding; biomes weigh only on part 0), the default caps fit 256 + 2 × 48. |
| Tools (pytest + ruff) | 120 passed, ruff clean. `nav_e2e.py` gained `--fresh` (the v1 client reads every region itself with a private `subscribe`), `--churn` (setblock and falling sand near the centre while the region moves), `--margin-y`, and the time until the last part arrived. |
| Quest scripts (`Tests~`, .NET 8, `--jar` + `--entity-models`) | 908/908 PASS. New in `NavTests.FastGlideTests` (32): margins by width and presets on 352 and 288 servers; the margin grown on the first push (whole sections, window and view kept); runway and caps; a simulated glide (72 Hz, answers after 0.3 s) at the cap without stalls, well above it with stalls; stopping and jitter ask for nothing; reversing turns the region round once; wiggling 6 blocks never ping-pongs; diagonal; vertical lookahead stops at the build height top and bottom; a resize doesn't count as glide latency; `MeshScope` boxes, lead ahead, order, no margin; the mesh cap and rate meter. |
| Unity 6000.0.84f1 batch compile | 0 errors; the only warning in `Assets/` is the existing CS0618 (`overlayType`). |
| `Editor/ShaderCheck` | 80 variants, 0 errors (no shader changed). |
| `Editor/BatchMeshCheck.RunRenderer` | 10/10 PASS: the eight existing steps with jobs of 4 sections, plus meshes outside `KeepScope` dropped and meshed again when the scope comes back. |
| Live: `nav_e2e.py --fresh --churn --light` | `ok`: 6 and 8 one-section shifts at 128 wide (margins 16 and 32), each delta-assembled region equal, blocks and light, to an independent full read; 41 block changes during the run. |
| Live: `area_e2e.py --light --widths 64,96,128,192,256,64,256,96` | `ok`: every resize (now read incrementally) equals a fresh client's full snapshot. |
| Live: `Tests~ --live`, `--live-resize` | all PASS (`--live-resize` 64 → 256 now loads the 352-wide region: 4,480 sections in 2.1 s; the full resends match). |

### Server: one-section shift (`nav_e2e.py --no-v1 --light`, REGION_SET / SNAPSHOT log lines)

| | before, 288×160×288 | after, 288×160×288 | after, 352×160×352 (256 + 48 margins) |
|---|---|---|---|
| sections read | 3,240 (all) | 180 | 220 |
| chunks captured, main thread | 361 in 2–3 ticks, 3.0–3.5 ms | 18 in 1 tick, 0.16–0.27 ms | 22 in 1 tick, 0.2–0.5 ms |
| walk (async) | 212–227 ms | 20–31 ms (copy 3–10 ms) | 22–42 ms (copy 7–21 ms) |
| remap / encode (outbound) | 16–18 / 19–25 ms | 1–5 / 2–10 ms | 1–6 / 2–8 ms |
| REGION_SET total | 300–350 ms | 45–51 ms | 50 ms (one 100 ms) |
| until the last part arrived (client) | about 400 ms | 68–151 ms | 158–236 ms |
| delta size | 0.44–0.89 MB | the same | 0.5–1.2 MB, 1–2 parts |

- The total is now one tick: the walk used to wait for Bukkit's async heartbeat (up to 50 ms) and the load step for the next tick; both are gone when the chunks are loaded, which `stream.ticket-ring: 1` arranges for the next shift.
- Resizes read only the ring too: 192 → 256 reads 1,024 of 2,592 sections (walk 68 ms, was 186 ms); shrinking reads nothing.
- **Fixed on the way:** at 352 wide every section went in its own part (220 parts, 6.8 MB for a one-section shift) because the biome estimate (4 bytes per cell, 1.24 MB) counted on every part; it is now the RLE size, on part 0 only.
- The `region_request` size estimate of an incremental move scales the previous estimate to the kept volume and adds the new sections in `sections_v1` form, which is 15–40% above the old one-message estimate; it drifts up a little while gliding (12 MB at 352 wide with light, cap 32 MiB).
- **Memory:** `max_region` is now 352×384×352 and 25,165,824 blocks (`blocks` was 16,777,216). One copy of 352×160×352 is 79 MB of ids and 20 MB of light on the server (a moving region holds two or three copies: the session's last grid, the move being read, a diff), and 59 MB (ushort + light) on the headset, which holds two while a delta decodes.

### Pan speed per preset (`Tests~ dotnet run -c Release -- --live-glide ws://… --widths 64,96,128,192,256 --glide-seconds 15`)

Full stick for 15 s along +x and back along −x, block size fitted to a 0.8 × 0.6 m table (2.23 mm at 256), 72 Hz frames running `NavState` and the renderer's meshing schedule (3 jobs, flat colours with light). "Asked" is 0.12 m/s × 3 (the ramp) / block size. "4× slower meshing" holds each worker 4× as long per section (`--mesh-slowdown 4`, 3.2 ms per section instead of 0.8), a stand-in for the Quest until it is measured there.

| width (margin after) | asked, blocks/s | before: blocks/s (cm/s) | server change only | after (cm/s) | after, 4× slower meshing (cm/s) |
|---|---|---|---|---|---|
| 64 (16) | 40 | 21–22 (18–20) | 24 (21) | 38.5 (34.3) | 38.5 (34.3) |
| 96 (16) | 61 | – | – | 57.9 (34.4) | 57.9 (34.4) |
| 128 (32) | 81 | 14 (6.1–6.3) | 16 (7.1–7.3) | 77.2 (34.4) | 53–54 (23.4–23.9) |
| 192 (32) | 121 | 9.2–9.4 (2.7–2.8) | 11.3–11.8 (3.3–3.5) | 66–68 (19.5–20.1) | 40.5–41 (12.0–12.2) |
| 256 (48) | 162 | 5.8–18 (1.3–4.1) | 8.7–9.6 (1.9–2.1) | 52–54 (11.5–12.0) | 33.5–34 (7.5) |

- Before, the cap used the latency until every new section was meshed (0.5–1.5 s at 256) and 12 blocks of runway; the server change alone cut the round trip from about 450 to 130 ms but not the meshing.
- After, at 256: round trip 152–158 ms (stream cap 156–184 blocks/s), so meshing is the limit: about 900 sections/s here (560–650 with the slowdown), mesh cap 52–54 (34). Up to 128 wide the asked speed is reached: the same 34 cm/s of table at every scale, as the stick curve intends. At 192 and 256 the table speed is lower (12–20 cm/s here) because every block of travel brings more sections into view.
- Frames with sections on screen not meshed yet: under 1.5% up to 192, 2.5–8% at 256 (4–9% with the slowdown), at the far edge of the window. With 80% of the measured rate instead of 65% it was 20–40% at 256 for 15% more speed.

### Not verified (needs the headset)

The real mesh rate and the speed it allows at 192 and 256 (read `GLIDE ... mesh_rate_s=... blocks_s=... holes_s=...`), frame times while gliding with jobs of 4 sections (`mesh_sections_per_job`), sections popping in at the far edge, the lookahead's feel when stopping and turning round, and GC pauses from the new grid each delta allocates (59 MB at 352×160×352).

### Known gaps

- When a delta lands, sections that were being meshed are meshed again (their job is dropped as stale): up to about 5% of the meshing at 256.
- The headset still copies the whole grid per delta; reusing buffers would remove that allocation.
- The mesh cap is unknown until meshing has been saturated for 0.5 s; until then only the stream cap applies.

## Using blocks, items and mobs (`interact_v1`, PROTOCOL "Using blocks and items", "Acting on mobs"; Paper 26.3, 2026-10-15, no headset)

The left trigger in vanilla's right-click order (mob, usable block, item on the face, place; left grip = sneak), items and spawn eggs in the block menu, hitting, carrying, naming, freezing and removing mobs, and the info card.

### Tests

| Suite | Result |
|---|---|
| Plugin JUnit (`./gradlew build`) | 155 tests after merging 0.4.0, 0 failures. `InteractRulesTest` 15: every empty-hand family on state strings (iron doors with and without the switch, both door halves and their sounds per wood, a gate swinging away from the ray, a lever's and a button's support block, button ticks, note/delay/mode wrap-around, cakes, candle cakes, composters, jukeboxes, candles, campfires, container and sign hooks), axes (strip, scrape with the copper naming, unwax), honeycomb, hoes and shovels (air above, not from below, rooted dirt drops), shears, fire (TNT, candles, campfires, soul fire, switches), buckets (sources, flowing water, waterlogging, replaceable cells, ultra-warm), eggs, bone meal, the wand, and the usable list per switch. `InteractFixtureTest` 8: all 12 fixtures through the parsers and builders, 17 malformed messages, name cleaning. |
| Python (`pytest -q`, `ruff check .`) | 159 passed after merging 0.4.0, ruff clean; 16 new: the 12 fixtures validate, caps and the welcome switches, the `interact`/`entity_op` builders against the fixtures, 12 malformed variants, the error codes and every op. |
| Tests~ (`dotnet run -- --jar 26.3.jar --entity-models entity_models.json`) | ALL PASS, 1046 checks after merging 0.4.0; 72 in `InteractTests.cs`: the fixtures through `Net/InteractProtocol.cs`, `UseRules.Kind` against the server's table, the right-click order (sneak, the `left_trigger` swap, no cap, mobs off, containers and signs), outline colours, which actions repeat, the bucket's fluid ray, the entity ray (nearest wins, the carried one skipped, a mob in front of and behind a wall, half-block minimum, the visible window, drop points), the Use and Eggs tabs (order, captions, a saved tab restored when the items arrive, search, the name tag field and its keyboard, egg picks), `BlockEditor.Interact` and the info card. |
| Unity 6000.0.84f1 headless compile, `ShaderCheck` | clean before and after merging 0.4.0 (no errors, no warnings in `Assets/`); 120 shader variants, 0 errors |

### Live server (own Paper 26.3, flat world, ports 25621/8821)

`python tools/interact_check.py --url ws://127.0.0.1:8821/tabletop --log tools/run/hands/server.log`: **41/41 PASS**, before and after merging 0.4.0. It moves the region to the grass (y = −61), builds a bench with `block_edit`, and checks: `item_registry` between `block_registry` and the first snapshot (18 usable items, 86 spawn eggs, no wither egg); an oak door opens with both halves (91–142 ms round trip) and closes from its top half; a lever on a stone block powers dust on the far side of that block to 15 and back to 0 (the neighbour bridge, `INTERACT_NEIGHBOURS on`); a stone button goes down and is back up 1.15–1.19 s after the click (20 ticks plus the diff); a note block goes up a note; bone meal grows a sapling (2–7 tries: vanilla's 45 % per use); an axe strips a log; a water bucket pours a source and the empty bucket picks it up; a pig egg spawns a pig, which is named Rex, hit (a `hurt` action, 10 → 5 health), held at the hand point with AI off, dropped with AI back, left held without updates (let go after 3 s), frozen and unfrozen, made to glow, moved, refused a move out of the region (`out_of_region`), and killed; an unknown uuid is `not_found` with `op` and `uuid`; flint and steel primes TNT (the block goes, a `minecraft:tnt` entity comes) and `entity_op remove` takes it away before it explodes; the lightning wand strikes; refusals with `op`/`pos`: a chest (the container hook), an axe on stone, a position outside the region, a diamond (not offered), a client without `interact_v1` (and it gets no `item_registry`).

`Tests~ --live-interact ws://127.0.0.1:8821/tabletop`: **12/12 PASS** with the app's own `LinkClient`, `SnapshotAssembler`, `EntityStore`, `UseRules`, `BlockEditor.Interact`, `EntityRaycast` and `EntityCardModel` (a door opened, a pig spawned, hit from above by the entity ray before the grass, its card shown and the pig killed from the card's Kill button).

Main thread per use (the `INTERACT` log lines): 0.16–3 ms; a first spawn 9.6 ms while classes load; building the item registry 11–26 ms once on start.

### Not verified (needs the headset)

The outline colours and the yellow mob box at table scale; aiming at small mobs and players among blocks; the left grip as sneak while gliding (the grip is also the vertical-glide modifier: the stick decides); the right grip carry (the 0.25 s delay against both-grips drags, the ghost box, the drop point on slopes, hold messages at 10 Hz) and the both-grips drag taking over; the item icon above the controller; the Use/Eggs tabs, icons of items and the lightning wand (lightning rod icon), the name tag keyboard; the info card's placement above the mob, readability and its buttons with the right ray while the block menu or operator panel is open; haptics; toasts for refusals.

### Known gaps

- Chests, barrels, shulker boxes, other containers and signs call the hooks (`OnContainerUse`, `OnSignUse` in `XR/BlockEditMode.cs`; an `interact` on them is refused with `interact_denied`): since the operator tools they open the container view and the sign editor (section "Operator tools" below).
- Only the curated table: no anvils, crafting tables, beds, respawn anchors, lecterns, flower pots, cauldrons, beehives, sweet berries, cave vine berries, composting items, candles on cakes, stacking items on blocks (sea pickles, candles), item frames, armour stands' equipment, villager trading, riding, or items such as potions, bows, fireworks, ender pearls and music discs.
- Sounds and particles are the plugin's own (`World#playSound` with vanilla keys, a few particles), not the game's level events; a door that another plugin protects opens anyway (no events fire, as with `block_edit`).
- A daylight detector's power follows its new mode within a second (its block entity's tick), not at once.
- A held mob's AI-off is saved with the mob: if the server crashes while a headset carries it, it stays NoAI (`/data merge entity <uuid> {NoAI:0b}` fixes it).
- Fire and lava can spread beyond the region; TNT can blow up blocks outside it.

## Undo, box edits and brushes (`power_v1`, PROTOCOL "Undo, box edits and brushes"; Paper 26.3, 2026-10-15, no headset)

### Tests

- Java: `PowerFixtureTest` (6: groups on `block_edit`/`interact`, `box_edit` and `brush` parsing and refusals, `history`/`box_progress`/error builders against the fixtures, welcome limits), `UndoJournalTest` (10: one hold = one group, first-before/latest-after merging, a cell back where it began leaves the group, redo cleared by a new group, groups per world, the depth keeps the newest group whole, brush strokes merging, block entity snapshot cap, position keys), `BoxPlanTest` (5: walk order, the cells of fill/clear/replace/walls/hollow, the clone's quarter turns as bijections, brush shapes (81 cells for a radius-2 sphere, 2553 for 8), smoothing a bump and a hole).
- Python: `test_tabletop_protocol.py` validates the 15 new fixtures and rejects 17 broken variants (`validate_power`); `ruff` clean.
- C#: `Tests~/PowerTests.cs`, 72 checks: fixtures, B's tap/hold/repeat and a press carried over from a blocked moment (`TapHoldButton`), groups stamped on `block_edit` and `interact` by `BlockEditor`, the wand's corners, fill/replace/clear refusals (no box, no filter, an item, over the cap), the undo-depth warning, clone paste size, origin and turns, B ending paste, brush cycling, radius limits, stroke throttling and the client's blocks-per-second bucket, undo/redo counts, one job at a time and its timeout, the Tools tab (placement after Use/Eggs, restored from prefs, buttons fit, clicks, removed for a server without `power_v1`). `ALL PASS` with `--jar` and `--entity-models`.
- Unity: batch compile clean (no `error CS`, no warnings in `Assets/`), `ShaderCheck` 120 variants, 0 errors.

### Live server (own Paper 26.3, flat world, ports 25622/8822, `auth.token` set for `command_v1`)

`python tools/power_check.py --url ws://127.0.0.1:8822/tabletop --token T --log tools/run/power/server.log`: **39/39 PASS** (after merging master with the living and operator branches). It clears its bench with four 32×32×32 boxes, then: three places in one group undone and redone as one (history 3 blocks, then 1 to redo); a break group back; an opened door closed again by undo (both halves), then its placement undone (both halves); a door broken from its top half back whole; a chest with 7 diamonds (`/item replace`) broken and undone with its diamonds (`/data get block`); a box clear over that chest spills nothing (`execute if entity @e[type=item]` fails) and its undo brings the diamonds back; a `container_edit` (5 gold ingots into slot 3) undone (the gold goes, the diamonds stay) and redone; a `sign_edit` ("Hello") undone (the text goes, the sign stays); a cell changed by `/setblock` skipped and reported (`skipped_pos`); a new edit clears redo; fill 500, replace 500 (corners in reverse), clear 500 and its undo; walls 60 (nothing inside); hollow (a stone shell, the gold block inside cleared); clone turned a quarter (east-facing stairs face south, positions as `BoxPlan.rotate`); refusals with readable messages (33600 blocks over the cap, a box outside the region, a command block from `edit.deny`); a 32768-block fill with `box_progress` while running and an undo refused meanwhile ("wait: a fill (80 %) is still running"); brushes (81 moss blocks for a radius-2 sphere, erased, a lone dirt bump smoothed away, strokes over the queue or blocks-per-second refused); a client without `power_v1` refused undo; a fresh session has nothing to undo; every message validates; the neighbour bridge is on.

`Tests~ --live-power ws://127.0.0.1:8822/tabletop TOKEN`: **6/6 PASS** with the app's own `LinkClient`, `SnapshotAssembler`, `BlockEditor` (group stamping) and `PowerTools` (undo of a three-block hold, a wand fill of 32 blocks and its clear).

Main thread (the `POWER` log lines, JIT warm): a 32768-block fill in 21–22 ticks (1.0–1.06 s), 41–44 ms of main thread (fix-up 19 ms), at most 2.06–2.11 ms in a tick with `edit.box-budget-ms: 2`; its undo in 19–21 ticks (0.91–1.0 s), 36–42 ms; a 500-block fill right after start 6 ticks (11 ms, the JIT still cold); a 3-block undo 1.8 ms. One earlier run had a single 3.8 ms tick in the big fill (a GC pause inside the slice).

### Not verified (needs the headset)

B's hold timing (0.6 s, then 0.4 s repeats) and the pick moving to B's release; the Tools tab's layout and readability (eleven tabs, seven button rows, five status lines); the wand's cyan box and the paste's orange box at table scale, cut to the visible window; aiming corners with the left trigger while the right ray aims; the left grip for corner B (the grip is also the glide modifier); brush strokes while sweeping (rate, haptics, the reach box); toasts for finished jobs and refusals; how a 32768-block job looks on the table while the diffs and section snapshots arrive.

### Known gaps

- Undo doesn't bring back entities (spawned mobs, primed TNT, items spilled by a chest broken with the trigger, which therefore duplicate when the break is undone), nor what happened later (sand that fell, water that spread, fire, crops grown): those cells are skipped.
- `block_edit`/`interact` are journalled from a window around the target (3×3×3, bone meal 7×14×7): a big tree grown with bone meal can reach past it; a lever's redstone further away comes back by itself through the neighbour update.
- The journal lives with the connection: a reconnect starts with nothing to undo. Cells outside the current area are skipped (glide back to undo them).
- A brush stroke's cells outside the area are left out silently; strokes send no `box_progress`.
- Box and brush jobs empty block inventories (chests, barrels, hoppers, jukeboxes, lecterns, pots, chiseled bookshelves...) before replacing them (kept for undo); a campfire's items aren't an inventory in the API and still drop.

## A living world: time of day, weather, leads, gear, displays (`world_v1`, PROTOCOL "World state" and "Living entities"; Paper 26.3, 2026-10-15, no headset)

### Tests
- Java `LivingTest` (5): `world_state` against `world_state.json`, the send rule (first message, weather, time set, world change, once a second, a stopped clock), the living entity fields against `entities_living.json`, cleared fields and the lid threshold, the display state rounding. All plugin tests: 137 pass.
- Python: `test_world_state_fixture_and_rules`, `test_entities_living_fields` (validator: dimensions, rain/thunder, display shapes, billboards, brightness, shulker faces). All: 135 pass; ruff clean.
- C# `Tests~/LivingTests.cs` and `LivingEntityTests.cs` (102 checks): `BlockShading.RawLightmap` equals 26.3's `lightmap.fsh` written out again in doubles for noon, midnight, dusk, thunder, the Nether and the End at Brightness 0, 0.5 and 1 (worst 1.7e-7), and `LightmapFor(Noon)` is the baked reference; the day timeline (noon 1, midnight 0.24, dusk and the dawn wrap), the weather layers (rain 0.7625, thunder 0.5992 at noon, half thunder, storms at night, the flash); the ratio table (all 1 at noon; at midnight open sky 0.44, torch-lit 1.00, caves 1.00); light codes through the SNorm8 normal w; greedy merging keeps a torch-lit cell apart from noon-identical sunlit ones; the world clock (20 ticks/s, rain fading 0.01 a tick, fixed time, flash, world change, moon phase); the fixture through the entity store; the gear catalog (armour by asset, leather's undyed colour, undead horse armour, chests, carpets, trader llama blankets, collars, harness, nautilus, saddles not on babies, tropical patterns, pumpkin, mushrooms, carried block, name labels); with the jar, the equipment rules equal the pack's `assets/minecraft/equipment/*.json` for 56 asset layers and every texture exists; with `entity_models.json`, every layer exists and the giant carries scale 6; the dragon, fangs, llama spit, cushion, wither skull and shulker bullet roots equal their renderers' PoseStacks (worst 1.9e-7 blocks), the sulfur cube rests on the ground; the evoker fangs' bite; leads against `LeashFeatureRenderer` (vertices exact, colours, sag both ways), leash offsets, hold points, quad leashes, the fishing line; `JavaRandom` against `java.util.Random`; the bolt (224 quads, 128 blocks, ends at the strike point); the temperature noise against the swamp noise, precipitation by biome and height, drop landing and layout; display orientation (`rotationYXZ(-yaw, pitch, 0)`, the three billboards, facing a camera); orb icons, UVs, colour, glow. All Tests~: 1065 pass (with `--jar` and `--entity-models`).
- Unity 6000.0.84f1: compile clean, `ShaderCheck` 160 variants 0 errors (Weather and Lines added), `BatchMeshCheck.RunRenderer` 0 errors (the batch vertex now carries the light code).

### Faces (`Tests~ --measure-batches`, the area snapshots with the pack and smooth light, before and after the light codes)

| window | before | after |
|---|---|---|
| 64 | 72,381 | 72,381 (+0.0%) |
| 96 | 118,996 | 119,054 (+0.05%) |
| 128 | 192,096 | 192,267 (+0.09%) |
| 192 | 438,266 | 438,936 (+0.15%) |
| 256 | 725,477 | 726,586 (+0.15%) |

Faces whose colours match at noon but whose light differs (sunlit ground next to a torch on Bright) no longer merge. The vertex size is unchanged (the code uses the normal's spare byte). GPU: one 16×16 texture fetch per vertex in the block shaders when it isn't noon in clear weather (skipped by a uniform branch otherwise); rain and snow are at most 2,500 quads in one draw call.

### Live server (own Paper 26.3, ports 25613/8813, `tools/living_check.py`, region (0, 96, 0) 32³, a platform built at y 100)
All 25 checks pass: `world_state` after the welcome and within 0.6 s of `/time set 18000` and `/weather thunder` (the command's own round trip included), then 3 messages in 3 s with the clock rising 20 ticks each; a wolf leashed to a fence (holder = the knot's id), a boat leashed to a happy ghast (holder = the ghast); horse armour and saddle, a chested llama with a carpet, a harness, wolf and cat collars; an orb's value 17, an enderman's grass block, a shulker's lid (0.5) on a wall (`north`), a snow golem without its pumpkin, a villager's visible name; text, item (transformation, brightness, context) and block displays; the lightning bolt, the dragon, wither, giant, fangs, wither skull, llama spit, shulker bullet, dragon fireball, cushion, sulfur cube and ominous item spawner (with its item) all listed. The still scene costs 161 B/s including `world_state`. 26.3 reads a block display's and an enderman's block as `"minecraft:stone"` or `{id: ..., properties: {...}}`; the check uses that form.

### Offline look check (`Tests~ --living-scene OUT.json`, `tools/render_mesh.py`)
Horses, llamas, donkeys, mules, pigs, striders, camels and a skeleton horse in their gear, a wolf in armour with its collar, a cat with its collar, a tropical fish pattern, the wither, a sulfur cube, a cushion, the fangs, spit, skull and bullet, and a happy ghast in its harness and goggles render with the right textures in place.

### Not verified (needs the headset)
How night, dusk, rain and thunder look over passthrough (and whether the night of the "Bright" setting reads well), the rain and snow streak sizes (1.2 × 12 mm, 3 mm flakes) and densities at 64 and 256 wide, the lightning bolt and flash, frame times in a storm at 256 wide, leads following a walking mob and a player, the gear on animated mobs, display entities and text displays (size of the text, billboards), orbs, the evoker fangs' bite, and the shulker lid.

### Known gaps
- The bolt's shape uses the entity id as its seed (the server doesn't expose the game's), and it doesn't re-flash.
- Rain and snow are soft streaks and flakes sized for the table, not the game's rain.png/snow.png columns; no splashes, no thunder sound.
- Fishing lines and player-held leads assume a right-handed player (the main arm isn't sent).
- Text displays are plain text in the app's font (no colours, shadows or see-through); `first person` and `head` item contexts draw untransformed.
- Sulfur cube size variants other than the default, the wither's armour and invulnerable look, the ender dragon's flight pose and the happy ghast's ropes aren't drawn; the camel's lead offset is approximate.

## Operator tools: follow, marks, containers, signs (PROTOCOL "Operator tools"; Paper 26.3, seed 12345, 2026-10-15, no headset)

Following a player, the Marks tab (worlds, spawn, typed coordinates, shared bookmarks), the container view and editing, and the sign editor.

### Tests

| Suite | Result |
|---|---|
| Plugin JUnit (`./gradlew build`) | 168 tests, 0 failures. `OperatorFixtureTest` 8: all 18 operator fixtures through `OperatorMessages`' parsers and builders (bookmarks, worlds, the jump request, containers, edits with "was", sort, the closed view, sign edits), 7 malformed bookmarks, 7 malformed container edits, 4 malformed sign edits, name and line cleaning. `ContainerRulesTest` 5: which blocks open and sort, vanilla titles, slot rules (furnace output and fuel, brewing stand slots, shulker in shulker), sorting (merging per stack size, components kept apart, order), the bookmark store (unique names ignoring case, sort, cap, YAML maps round trip, bad entries skipped). |
| Python (`pytest -q`, `ruff check .`) | 201 passed, ruff clean; 24 new: the 18 fixtures validate, caps/worlds/marks in welcome and region_info, a jump inside the nether's height, the `bookmark` and `container_edit` builders against the fixtures, 20 malformed variants, `mark_denied`. |
| Tests~ (`dotnet run -- --jar 26.3.jar --entity-models entity_models.json`) | ALL PASS, 1150 checks; 104 in `OperatorTests.cs`: the fixtures through `Net/OperatorProtocol.cs`; `Jumps.Plan` (dims and margins kept, whole-section origins in the same world, centred within 8 blocks, clamped at bedrock and the top, another world's height, a 384-high region shrunk to the nether's 256); typed coordinates (`x z`, `x, y, z`, `~`, refusals); `JumpFocus`; following against the real `NavState` and a fake server (a player walking 4.3 blocks/s for 20 s stays within 2.5 blocks of the centre by section shifts alone, dead zone, vertical band, a big climb, a far teleport found by `region_query` and jumped to, into the nether within its height, the player leaving, Go to once, a manual stop); the Marks tab (header, world buttons, Spawn, typed jumps, rows, Go, Delete, Add mark with a name, read-only); Players' Follow / Stop / Go to (also without `command_v1`); the container view (grids, slot labels, hotbar items and icons, Take/Put/Clear/Sort messages with "was", a view-only server, a refused edit, the server closing the view, a tab click and closing the panel sending `container_close`, a double chest's other half); the sign editor (filled from `block_entities`, typing, next line, colour, glow, sides, Save sends only changed sides, a veto, an empty hanging sign); every new layout inside the panel, without overlaps, at most 40 cm. |
| Unity 6000.0.84f1 headless compile, `ShaderCheck` | clean (no errors, no warnings in `Assets/`); 120 shader variants, 0 errors (no shader changed) |

### Live server (own Paper 26.3, seed 12345, ports 25623/8823, `auth.token` set, ViaVersion/ViaBackwards 5.12.0 for the bot)

`python tools/operator_check.py --url ws://127.0.0.1:8823/tabletop --token T --mc-port 25623 --data-dir tools/run/operator/plugins/TabletopCraftAR`: **35/35 PASS** (the last two runs; earlier runs found two test bugs: setup commands over the 2/s command limit, and blocks left from a previous run). Worlds: `world` (normal, −64..320, spawn 96 145 −32), `world_nether` (0..256, spawn 0 65 4), `world_the_end` (0..256, spawn 0 65 0); `bookmarks` arrives after `item_registry` and before the first snapshot; `region_info` carries the spawn and the worlds; a spawn jump lands where asked with its margin (105–594 ms); a bookmark added by one headset reaches a second one and `bookmarks.yml`, a remove in another case reaches both, an unknown name and a headset without `marks_v1` get `mark_denied`; the Java player (mineflayer) is found by `region_query` and listed in `player_positions` of the region around them, again after a 500-block teleport (210–881 ms) and in the nether (151 ms; `region_changed.limits.build_height` [0, 256]); a double chest is one view of 54 slots with the other half named; 16 torches put arrive by themselves (93 ms); 64 ender pearls are clamped to 16; a stale "was" is refused; an emerald put into the other half by command arrives live; sort merges 3 + 5 diamonds; taking 6 leaves 2; a shulker box refuses a shulker box; a furnace refuses stone as fuel and takes coal; a barrel's `open` property is true while viewed and false after `container_close`; breaking the chest ends the view ("the container is gone"); outside the region `out_of_region`; without `interact_v1` `interact_denied`; a sign edited on both sides (lines, light blue glowing front, red back) shows in `block_entities`; a waxed sign and a block without a sign are refused.

`Tests~ --live-operator ws://127.0.0.1:8823/tabletop T FollowMe` with a mineflayer player online: **10/10 PASS** (three runs in a row) with the app's own `LinkClient`, `NavState`, `FollowState`, `JumpFocus` and `OperatorPanelModel`: the player is teleported 3 blocks east twice a second for 10 s and the view glides 60 blocks after them by 4 region shifts and no jump (ending 0.7 blocks behind); a 1000-block teleport is followed by one jump with the view on the player; a teleport into the nether is followed into it with a region inside 0..256; a bookmark round trip. (A survival bot that is teleported about can get kicked for `invalid_player_movement`; the test makes it creative first.)

Main thread (the `CONTAINER`/`SIGN` log lines): 0.04–0.25 ms per container view or edit, 0.06–0.22 ms per sign edit (with the `SignChangeEvent`).

### Not verified (needs the headset)

The Marks tab's readability and the keyboard for coordinates and names; the world buttons into the nether and the end at table scale (a new world loads in full); the view landing on the target after a jump; following a walking, riding and flying Java player for minutes (smoothness, the vertical band, the moment it jumps), following through a portal and a death; that the stick stops following at once; the container view beside the left hand with item icons from the pack (icons of items that aren't blocks, enchanted items show as their plain item), counts and slot labels, selecting with the right ray, Take/Put/Clear/Sort; Java players seeing a chest's or shulker box's lid open while the headset looks (`Lidded#open`; the barrel's property was checked live) and close again; the sign editor's swatches (dye colours from `entity_models.json`, else wool colours), typing four lines, and the sign on the table updating after Save; a protection plugin vetoing a sign edit (no such plugin on the test server).

### Known gaps

- Container and sign edits are journalled for undo since the power tools merged (`PowerService.journalledTiles`); an undo puts the whole block entity back, so another player's change to the same chest in between is overwritten.
- Items are shown and set by id and count only: names, enchantments, potion contents, shulker box contents and other components aren't shown, and Put creates a plain stack (the same item already in the slot keeps its components). No drag between slots, no taking items into a hotbar (the headset has no inventory).
- Lecterns, chiseled bookshelves, decorated pots, jukeboxes, campfires, ender chests, crafting tables and furnace minecarts don't open a view.
- The sign editor sets plain text (no per-line colours or formatting) and can't edit waxed signs; vanilla's line width isn't enforced (40 characters on the headset, 90 on the server).
- `SignChangeEvent` has no player: plugins that need one log an error and don't block the edit. Container edits fire no inventory events (protection plugins aren't asked).
- Following is client-side: two headsets following different players fight over the shared region (the last request wins, as for gliding).
- Jumps to typed `x z` keep the view's height, which may be underground or in the air at the target.

## Faster meshing and the surface skirt (mesh; Paper 26.3, seed 12345, 2026-10-15, no headset)

At 256 wide every block of travel brings about 16 new sections into the active box, so meshing limited gliding (TESTING "Faster gliding": 52-54 blocks/s on a PC, 34 with meshing 4× slower; the headset with 0.3.0 did 4-9). Measured on an x86 PC (Ryzen 7 9800X3D), own Paper 26.3 server (ports 25624/8824, 2 GB heap, plugin 0.4.0, chunks generated), clients with `sections_v1` + `light_v1` + `biomes_v1`, the 26.3 client jar as the pack.

### Tests

| Suite | Result |
|---|---|
| Quest scripts (`Tests~`, .NET 8, `--jar` + `--entity-models`) | 1,354/1,354 PASS (master 0.5.0: 1,326). New in `MeshTests.cs` (28): `ShadeCache` lights 7,200 random faces exactly like the world (random shapes and light, a 37×21×29 region, every section border and region edge, cubic and inner faces, light floors, flat and full-bright shades, smooth light on and off) and falls back to the world outside its section; the greedy pass on sections without cubes, partial edge sections, unknown palette ids and `leaves_inner` on/off; job size and the work meter; `skirt_speed` in the mesh cap; off-screen batches; skirt tiles (one merged top face just below the surface, plants skipped, step sides from the lower neighbour's surface, the window top cutting a pillar, nothing in range, tint × light and light code of the cell above, `SurfaceRange`); the skirt planner (tiles for pending columns, no repeats while building, kept while their surface is pending, dropped when it is on screen even with sections pending deep below, re-keyed on a shift, dropped outside the region or the box, no builds while a job runs). |
| Unity 6000.0.84f1 batch compile | 0 errors; warnings in `Assets/` only the existing CS0618 (`overlayType`) and CS0414 (`BlockEntityRenderer._fullScan`). |
| `Editor/ShaderCheck` | 160 variants, 0 errors (no shader changed). |
| `Editor/BatchMeshCheck.RunRenderer` | 11/11 PASS: the ten existing steps (with deferred drops and the off-screen wait) plus "skirt while gliding east": a one-section shift with the view moving shows skirt tiles for the new columns (up to 6, on screen in 2 frames) and none once everything is meshed and uploaded, and what is on screen then equals the meshed model. Fixed on the way: a section meshed again after it was dropped (KeepScope) was re-meshed at the next snapshot. |
| `TabletopBatch.BuildApk` | Build succeeded in 1:38 (IL2CPP ARM64; not installed). |

### Mesher output and cost per section (`Tests~ --profile-mesh DIR --jar 26.3.jar [--hash OUT]`, one thread)

Four saved snapshots with light and biomes: a dark forest (0, 32, -128) and a meadow (384, 48, 448) and old-growth taiga (192, 32, 64), each 192×160×192, and the 288×128×288 area at 0,0 from the batching measurements. Every one of the 6,912 sections hashes the same (vertices, UVs, colours, light codes, the three index lists, in order) as master's mesher with the pack, with `leaves_inner` off, with flat light, without light and without a pack (stable flat colours: `BlockRules.FlatColor` hashes unknown names with .NET's per-process string hash).

| snapshot | before: ms/section | after | air sections | all-stone sections | sections with faces |
|---|---|---|---|---|---|
| forest | 0.549 | 0.167 (3.3×) | 0.321 → 0.019 | 0.597 → 0.109 | 0.764 → 0.328 |
| meadow | 0.487 | 0.134 (3.6×) | 0.327 → 0.019 | 0.577 → 0.105 | 0.692 → 0.321 |
| taiga | 0.475 | 0.124 (3.8×) | 0.320 → 0.018 | 0.566 → 0.105 | 0.670 → 0.291 |
| 0,0 (288 wide) | 0.534 | 0.157 (3.4×) | 0.335 → 0.019 | 0.603 → 0.115 | 0.775 → 0.342 |

- Where the time went: two string-keyed dictionary lookups per cell and direction in the greedy pass (`BlockRules.Shape(state)` for the cell and its neighbour, then `Face(state, dir)`), about 50,000 per section whatever it holds, so an all-air section cost 0.32 ms; then about 20 bounds-checked world reads per lit face. Now: one padded copy of the section and its border per section, lookups per palette id, a light cache per section; the rest is the faces themselves (the greedy merge, emitting, `SectionMesh.From`).
- On the headset (IL2CPP, ARM) the string hashing and the small-method calls removed were relatively dearer, so the gain may be larger there; it is unmeasured.

### Gliding (`Tests~ --live-glide ws://… --jar 26.3.jar --widths 128,192,256 --glide-seconds 12 [--mesh-slowdown 4]`)

The harness now meshes exactly like the renderer (Build + BuildSpecials + `SectionMesh.From`, the jar's models and textures prewarmed on the main thread like the atlas; it used to call `BlockRules.FlatColor` per face, string work the app never does, which made sections cost 0.9-2.7 ms there) and tracks the batches (rebuilds and MB re-uploaded per second). "4× slower" now spins the worker for 3× the section's own time (`Thread.Sleep` rounded sub-millisecond waits to 0 or 1 ms). "Before" is master's mesher, fixed jobs of 4 and 0.4.0's batch rules (`--per-job-max 4 --old-batch-rules`); "after" this branch, skirt off; "skirt" this branch with the skirt and `skirt_speed` 2. Glides start at the meadow (384, 48, 448) and go +x then -x. Asked speed (stick, 3× ramp): 81 blocks/s at 128, 121 at 192, 162 at 256. Table cm/s at the table-fitted block size (4.45 / 2.97 / 2.23 mm). "Holes" = frames (of about 890) in which a visible column's surface sections weren't on screen (dirty, being meshed, or batch not uploaded); with the skirt: not covered by a skirt tile either.

| width (asked) | before | after | after + skirt | before, 4× slower | after, 4× slower | after + skirt, 4× slower |
|---|---|---|---|---|---|---|
| 128 (81) | 76.1-76.3 (33.9-34.0) | 76.2-76.3 (33.9-34.0) | 76.1-76.3 (33.9-34.0) | 76.2-76.3 (33.9-34.0) | 76.1-76.3 (33.9-34.0) | 76.2-76.3 (33.9-34.0) |
| 192 (121) | 65.6-68.1 (19.5-20.2) | 110.7-111.5 (32.9-33.1) | 111.0-111.8 (33.0-33.2) | 62.2-64.1 (18.5-19.0) | 110.5-112.1 (32.8-33.3) | 111.4 (33.1) |
| 256 (162) | 52.8-56.1 (11.8-12.5) | 145.8-146.3 (32.5-32.6) | 145.5-149.4 (32.4-33.3) | 47.6-49.5 (10.6-11.0) | 117.5-118.9 (26.2-26.5) | 140.9-144.1 (31.4-32.1) |

Blocks/s (table cm/s), both directions. At 256:

| | before | after | after + skirt | before, 4× | after, 4× | after + skirt, 4× |
|---|---|---|---|---|---|---|
| mesh rate (sections/s) | 907-913 | 7,493-7,983 | 7,238-7,549 | 792-806 | 2,071-2,334 | 2,396-2,553 |
| mesh cap (blocks/s) | 53-56 | 430-445 | 860-865 | 48-50 | 121-127 | 247-252 |
| worker ms per section (real) | 0.60-0.65 | 0.12-0.13 | 0.13 | 0.57-0.60 | 0.14 | 0.14 |
| job size | 4 | 58-64 | 47-64 | 4 | 6-20 | 8-18 |
| surface holes (frames of ~890) | 60-175 | 8-18 | 3-6, uncovered 0-4 | 47-97 | 148-168 | 475-677, uncovered 25-55 |
| batch rebuilds/s (MB/s) | 50-51 (18-24) | 102-119 (40-47) | 108-109 (43) | 48-49 (17-19) | 92-94 (40-42) | 98-104 (43-44) |
| skirt tiles/s (ms each, real) | | | 166-168 (0.16-0.17) | | | 167-177 (0.12-0.14) |

- Before, meshing was capped at about 900 sections/s whatever a section cost: jobs of a fixed 4 sections were handed out once per frame (12 per frame at 72 Hz). The faster mesher alone gave nothing until jobs took ~1.5 frames of work (`per_job`): now the measured rate is 8× at full speed and 2.6-3× with the 4× slowdown, and at 256 the stream cap (about 130-200 blocks/s here) and the asked speed limit the glide on the PC. 192 and 256 now reach about the same table speed as 128 (33 cm/s) at 1×; with the slowdown 256 reaches 26 cm/s, or 31-32 cm/s with the skirt.
- The skirt keeps the view's surface covered: with the slowdown and `skirt_speed` 2 at 256, detail was still missing somewhere on screen in 54-77% of frames, but a skirt tile covered it in all but 25-55 frames (3-6%), usually a column or two at the leading edge for a frame. Without the slowdown the skirt is rarely needed (mesh cap 860 blocks/s, holes in under 1% of frames). These harness skirt counts are conservative: it dropped tiles whose build a new snapshot overtook instead of planning them again, as the app now does.
- Batch uploads: each block of travel at 256 re-uploads 0.28-0.32 MB instead of 0.34-0.43 (about 40% of the rebuilds are now empty trailing batches with nothing to upload). At 146 blocks/s that is 40-47 MB/s on the headset's main thread (within the 3 ms per-frame upload budget, at least one batch per frame); at the headset's likely speeds a third to a half of that.
- 128 is at the asked speed in every run (as before).

### Not verified (needs the headset)

What a section costs on the Quest (`GLIDE ... mesh_ms=` is the worker time per section, `per_job=` the job size, `mesh_rate_s=`), the speed at 192 and 256 with and without the skirt (`"surface_skirt": false`, `"skirt_speed": 1`), how the skirt looks while gliding (tiles at the leading edge, replaced by detail within moments; no z-fighting, no seams with the detail, water and leaves as opaque stand-ins), frame times (PERF) while gliding: the skirt adds a pending-grid pass over the active box per frame while moving, a few pooled tile uploads (about 10-30 KB each) per frame and one small worker job; `MESH workers=... cores=...` at start (what the Quest reports), and whether a 4th worker (`"mesh_workers": 4`) helps or costs frames.

### Known gaps

- The skirt is a stand-in: top faces only plus steps, flat-lit (no AO), no grass side overlays, water and leaves opaque, nothing for overhangs or caves seen through the cut sides (the caps show those). It covers only what the region snapshot has (the margin ahead); beyond the region there is nothing to show until the next delta.
- With `skirt_speed` 2 and slow meshing, detail can trail far behind (the skirt covers the window's surface, and detail fills in when the glide slows or stops).
- Dropped sections' geometry stays on the GPU until their batch is rebuilt (at most 4 s, hidden by the clip box).

## Sound and asset delivery (`sound_v1` / `assets_v1`, PROTOCOL "Sound" and "Asset delivery"; Paper 26.3, 2026-10-15, no headset)

### Tests
- Java (`./gradlew build`): `SoundTest`: the `sound_groups` message against its fixture, `SoundRules` (note pitch, container, explosion, bucket, ignite sounds), sound actions in `ActionBatch` (the fixture, collapse within a tick, other worlds, the 8-block margin, nothing for sessions without `sound_v1`), the asset frame byte for byte against `asset_chunk.bin`, the offer and `asset_denied` against their fixtures, the LAN check (private, ULA, link-local, loopback in; public and CGNAT out), SHA-256.
- Python (`pytest`): `test_sound_fixtures_and_rules`, `test_asset_messages_and_frame` (validators for `sound_groups`, the `sound`/`sound_stop` kinds, `assets`, `asset_get`, the frame parser and builder).
- C# (`Tests~`, `SoundTests.cs`, 70 checks): sounds.json parsing and the variant pick (weights within 3% over 8000 picks, event references multiply volume and pitch, stream and attenuation), block sound groups (server table, the fallback guess), every state-change rule, the director (the actions fixture, explosion breaks silent, door transition once at the lower half, dedupe of a server sound against an inferred one, own edits, steps every 1/0.6 blocks with mobs' own step sounds, ambient rate, hurt, death, pickups, thunder, TNT, player steps and none while riding, digging hits, rain, water and lava ambience, chat ping), the mixer (fall-off, voice caps, stealing, `sound_stop`), sounds.zip (extraction cache, stale caches deleted, refused paths), the downloader against an in-memory server (download, nothing re-downloaded or re-hashed next time, resume after a dropped link, checksum mismatch keeps the old file, `asset_denied`), the Info tab's volume row, prefs.json. With `--sounds sounds.zip` (the user's own, from `PackSounds`): all 84 hand-written sound ids exist in the 26.3 sounds.json, picked variants (also through an event reference) extract to Ogg files. Full run with `--jar`, `--entity-models` and `--sounds`: ALL PASS (1424 checks). Python: 249 passed, ruff clean.
- Unity 6000.0.84f1 batch compile: no errors or warnings in Assets; ShaderCheck 160 variants, 0 errors.

### PackSounds (this machine's launcher, 26.3, asset index 34)
- `--list`: 1991 events; blocks 28.5 MB, mobs 34.0, items 3.6, weather 0.8, ui 0.3, ambient 15.9, music 258.9, records 34.3, other 0.6 (files counted once per group).
- Default groups: 1915 events, 4679 files, 67.0 MB zip, 1.3 s (SHA-1 checked).

### Live server (own Paper 26.3, flat world, ports 25625/8825, `auth.token` set, `assets.serve` on with DUMMY files: 4 MB pack.zip, 12 MB sounds.zip of random bytes)
- `SOUND_GROUPS 1283 blocks in 129 groups, 62562 bytes, 8 ms` at start; about 64 KB once per `sound_v1` connection.
- `tools/sound_check.py --assets-dir DIR`: 15/15. `sound_groups` arrives after `block_registry` and `item_registry`, before the first snapshot; a note block played from the headset comes back as `block.note_block.harp` at the block centre, volume 3, pitch 0.53 (note 1); a door as `block.wooden_door.open`; TNT lit with flint and steel as `entity.tnt.primed`, then `entity.generic.explode` (volume 4) 4.1 s later; a client without `sound_v1` got 48 actions and no sound; the offer matched the folder (sizes, SHA-256); both files downloaded and matched at 8.1 MB/s (the configured 8 MB/s pace); a name outside the three is `not_found`, a 2 MiB length `bad_message`, a client without `assets_v1` `asset_denied`.
- `Tests~ --live-assets ws://127.0.0.1:8825/tabletop TOKEN` (the app's LinkClient and AssetSync): 3 files, 16 MB downloaded and verified in 2.0 s (7.9 MB/s). Server log: one `ASSETS session=… sent <file> … MB/s` line per file.

### Not verified (needs the headset)
- That Unity decodes the extracted OGGs on the Quest (`UnityWebRequestMultimedia`, `AudioType.OGGVORBIS`, `file://`), the first-play latency (the copy out of the zip and the decode), and memory with ~300 clips.
- How it sounds: the fall-off scale (`sound_range` 8), panning from the head, the 24-voice limit in a busy area, rain and ambience density at wide presets, footsteps of many mobs.
- The asset download on Wi-Fi to the headset (speed, the toast, resume after taking the headset off), and the scene reload after a new `pack.zip` (OVRManager and MRUK coming back).

### Known gaps
- 26.x mob sound variants (moody cows, big and mini pigs, wolf moods, royal cats) play the base mob's sounds: the server doesn't send the sound variant.
- Sounds other plugins play with `World#playSound` (no Bukkit event) and the game's own server sounds without a block or entity change (eating, drinking, shearing by Java players, a goat horn) aren't heard; biome and underwater ambience loops and music aren't played.
- Unity's built-in panning only (no HRTF; the Meta XR Audio SDK would be a new package).
- `sound_groups` is about 64 KB per connection (full block ids); fine on a LAN.

## Motion: flowing fluids, ambient particles, block and entity animations (PROTOCOL "Block animations"; Paper 26.3, 2026-10-15, no headset)

### Tests
- Java (`./gradlew build`, 205 tests): `MotionTest`: the `lid`, `bell` and `explode` actions against `actions_motion.json` (a lid opened and closed in one tick keeps the last; an explosion 4.5 blocks outside the region still counts, a lid there doesn't), `ActionBatch.lidded` and `explosionPower`, the `fuse`/`swell`/pose entries against `entities_motion.json`, and the resend rules (a fuse every 10 ticks, a swell by 0.05, a calmed creeper at once).
- Python (`pytest`, 252 passed; `ruff` clean): `test_motion_messages` (validators for the three kinds, `fuse` and `swell`).
- C# (`Tests~` with `--jar` and `--entity-models`, 1,460/1,460 PASS). `WaterTests.Flow` (6): vanilla's top UVs turned to the flow over 16 directions (half the sprite per block, centred), a still ocean (still tops and bottoms, flowing sides), a stream (`getFlow` east on every top), a waterfall edge (the pull toward the water below an open neighbour), the overlay against glass, lava's own flowing sprite. `MotionTests`: torch, wall torch, candle (vanilla's offsets), candle cake, campfire, fire, portal and leaves templates; the neighbour rules in small grids (lava pops only under air, leaves over open space with the fall depth, drips through stone but not glass, bubble column height, the portal's axis); slots keep the rate, density by width, the 3,000 cap; the lid, bell, death, TNT, creeper, sitting and sleeping curves against the 26.3 numbers (`ChestModel`, `ShulkerBoxModel`, `BellModel`, `LivingEntityRenderer`, `TntRenderer`, `CreeperRenderer`, `WolfModel`/`AdultWolfModel`, `AdultFelineModel`); the sitting parts exist in the user's `entity_models.json`; the pose keys; parsing both fixtures.
- Unity 6000.0.84f1 batch compile: no new warnings in Assets; `ShaderCheck` 180 variants (with `TabletopCraft/Particles`), 0 errors; `BatchMeshCheck.RunRenderer` 0 errors.

### Mesher cost (`Tests~ -c Release --profile-mesh` on the four saved snapshots with the 26.3 jar, one thread)

| snapshot | before: total / fluid ms per section | after |
|---|---|---|
| forest | 0.179 / 0.023 | 0.179 / 0.023 |
| meadow | 0.136 / 0.011 | 0.134 / 0.011 |
| 0,0 (288 wide) | 0.157 / 0.038 | 0.157 / 0.039 |
| taiga | 0.125 / 0.011 | 0.125 / 0.011 |

Same quads and vertices in every snapshot; 81 sections with fluids hash differently (their side UVs now name the flowing sprite).

### Live server (own Paper 26.3, flat world, ports 25626/8826)
- A headset-like client (`actions_v1`, `entities_v1`) with console commands: a bell over a redstone block gives `bell` with `dir` north; an ignited creeper streams `swell` 0.04 to 0.54 in about 0.07 steps, then `explode` power 3; primed TNT on a block streams `fuse` 29, 19, 9 (every 10 ticks), then `explode` power 4; a zombie damaged to death streams the `dying` pose. TNT falling out of the region sends no `explode`.
- Not live: `lid` needs a Java player opening a chest (covered by the Java test; `tools/e2e.sh`'s mineflayer bot could do it).

### Not verified (needs the headset)
- How flowing water and lava look (the four-layer split, the turned tops, the overlay through glass) and that the animation reads as flowing down the sides.
- The particles: sizes at table scale, density at 64 / 128 / 256 wide, the GPU cost of up to 3,000 alpha-blended quads, the worker scan time on the Quest, fireflies fading in at dusk, leaves landing.
- Lids, bells, the explosion cloud and flash, the death tilt, villagers in beds (lying toward the bed's facing), sitting wolves and cats, TNT and creeper flashes.

### Known gaps
- Pistons still jump instead of sliding over 2 ticks (the moving cells keep drawing the moved block, `PistonGhosts`): a slide needs the moving blocks drawn outside the section meshes.
- Particles not done: furnaces, smokers, blast furnaces, brewing stands, end rods, spore blossoms, crying obsidian, dripstone tips, enchanting glyphs, signal fire smoke; redstone torch dust uses the smoke sprites tinted red; particles don't collide (leaves and drips stop at the floor found by the scan).
- Parrots and camels don't sit; sleeping mobs lie down only in a bed.

## Bedrock players (Geyser + Floodgate; Paper 26.3, seed 12345, 2026-10-15, no headset)

Setup (QUEST_SETUP.md Part Q): Geyser-Spigot 2.12.0 build 1258 (`/geyser version`: Java 26.3, Bedrock 26.30-26.52) and Floodgate 2.2.5 build 141 from download.geysermc.org (SHA-256 checked against the API), on our own test server: ports 25627, WS 8827, Bedrock UDP 19142, `online-mode=false`, Geyser `java.auth-type: floodgate`. For the bot only, Geyser's `advanced.bedrock.validate-bedrock-login` is set to `false`: `bedrock-protocol` logs in offline, without Xbox auth. Never do this on a real server: anyone could log in as anyone. ViaVersion and ViaBackwards 5.12.0 let the mineflayer Java bot join.

### Tests
- `./gradlew build`: `BedrockTest` covers `avatar.bedrock` parsing, when the giant gets its Bedrock Mannequin (auto + a bridge), the Mannequin's scale (its head as big as the floating head, capped at 16 or `max-scale`, at least 0.0625), and `skin` being sent again when a player's skin URL changes. All plugin tests pass.
- Engine-free: the operator panel's player buttons already take Floodgate's `.` prefix (`SafeName`). `Tests~` ControlTests now checks `tp .Steve ~ ~1 ~`.

### Live server (`tools/bedrock_check.py --java`, 13/13 PASS)
The Bedrock bot is `tools/bedrock/bedrock_player.js`: PrismarineJS `bedrock-protocol` 3.60.1 speaking Bedrock 1.26.51 (protocol 2193) through Geyser. The run, step by step:
- **Joining.** The bot joins through Geyser. Floodgate names it `.Bedrock<n>`, and the plugin logs `BEDROCK players detected through floodgate`.
- **On the table.** Put in creative in the middle of the region, the bot is listed in `player_positions` under that name. Its first entry carries a `skin` URL: Floodgate had already applied a converted skin.
- **Chat.** The bot's chat reaches the headset as `chat` kind `chat` from `.Bedrock<n>`. The headset's `chat_send` reaches the bot as `[Giant] BedrockCheck: ...`.
- **Floating giant.** The bot receives an `add_player` (Geyser's translation of the Mannequin), `set_entity_data` scale 16, and moves that follow the poses. The Java bot receives the `item_display`s and no Mannequin.
- **Speech bubble.** The giant's bubble reaches the bot as an armour stand whose name tag is the line.
- **Ground giant.** The bot receives the Mannequin.
- **Leaving.** After the bot quits, it drops out of `player_positions`. Geyser notices by timeout, about 10 s later.

```
cd tools/bedrock && npm install && cd ../bot && npm install && cd ..
python tools/bedrock_check.py --url ws://127.0.0.1:8827/tabletop --console tools/run/bedrock/server/stdin.fifo \
    --bedrock-port 19142 --java --mc-port 25627 [--trace]
```

What Geyser sends a Bedrock client, for each part of the giant (seen with `--trace`):

| Giant part (Java entity) | Bedrock client gets |
|---|---|
| Floating head, hands (`item_display`) | nothing: Geyser 2.12 has no item display translation |
| Mannequin (ground/exact, and the Bedrock-only one) | `add_player` with an empty name, then `set_entity_data` scale 16 (a `player_list` packet arrives too, which is where Bedrock carries skins; its content wasn't inspected) |
| Speech bubble (`text_display`) | `add_entity` `minecraft:armor_stand` (scale 0) with the text as its name tag |

### Not verified
- **With a real Bedrock client** (phone, Windows, console): the Mannequin's look and skin, its scale on Bedrock, and how its head pitch reads. Also Xbox sign-in with `online-mode=true`, and chat with `enforce-secure-profile` on.
- **The Floodgate skin path with a real Xbox account**: the global API conversion, and the figure on the headset switching from the default skin. The plugin now re-sends `skin` when it changes (PROTOCOL 4.6 always said so, but it was sent only once).
- **Account linking and Chunker conversion**: documented, not exercised here.

### Known gaps
- Bedrock players never see the giant's hands: Geyser drops every item display. The Bedrock Mannequin's arms hang still.
- At the default block size the Bedrock Mannequin's head is 8 blocks against the 25-block floating head, because 16 is the `SCALE` maximum.

## GPU at 256 wide (gpu; 2026-10-15)

### On the headset before (Quest 3, 0.8.0 + passthrough alpha, link thread, perf levels/foveation; width 256, region 352x186x352)
- 1.34M faces, 5.46M vertices, 108 batches, about 216 draw calls. PERF 26-31 fps, frame_ms p50 34-37, and a 145-160 ms hitch in every 5 s window.
- VrApi: GPU% 0.95-0.99, app GPU time 26-35 ms, GPU clock 640 MHz. High dynamic foveation (applied) didn't help.
- `ovrgpuprofiler`: 405M vertices shaded/s (about 15M a frame: two views plus binning), ALU/vertex 100, vertex fetch stall 39%; 1.35G fragments shaded/s (about 50M a frame, 5x overdraw with 2 mm blocks and triangles a few pixels big), time 65% fragments / 35% vertices, 2.0 textures/fragment, shader ALU capacity 24%.
- A/B through tabletop.json (about 20 s each, same view): `mipmaps`, `particles`, `surface_skirt` and `leaves_inner` off one at a time: no change (27-28 fps). All of them plus `weather_fx` off: 31 fps and no hitch.
- At 0.3.0 the same face count ran at 55-65 fps; the block shaders have grown since (random rotation, explicit gradients, day light lookup, more varyings).

### What changed (bisectable commits: the rain fix, the shaders, a skirt fix, the vertices, the draw order)
1. **Rain hitch.** `WeatherRenderer` rebuilt its 2,500 drops every 5 s whether blocks had changed or not, running ~40 culture-aware string tests per drop (`Precipitation.Blocks`) and biome string lookups. These are now cached per palette index and per biome, the buffers are reused, and the timed rebuild waits for a block change. `Tests~ -c Release --measure-weather DIR`, full layout on x86 (before: the 0.8.0 code in the same run): 64 wide 4.7 -> 0.5 ms, 256 wide 8.9 -> 0.9 ms. Culture-aware comparisons are far slower under IL2CPP on the headset, which fits the 150 ms.
2. **No discard for opaque terrain.** Every block pixel had a `discard` (clip box and alpha test), which turns off early depth rejection: each hidden fragment was shaded, then depth-tested. Opaque faces on fully opaque layers now go to a solid submesh drawn without alpha test (Geometry queue, first), the window is cut by hardware clip planes (`SV_ClipDistance`), fog and day light are keyword variants, and batches assemble their sections top down.
3. **20-byte vertices** for batches (`PackedVertex`, was 32).

### Offline numbers
GLES3 statements in `main` (`ShaderCheck.Stats`, multiview, HLSLcc emits about one per DXBC instruction), vertex outputs in components (clip position and eye index included), vertex inputs:

| Blocks variant | vertex stmts | fragment stmts | varyings | inputs | discard in fragment |
|---|---|---|---|---|---|
| 0.8.0 | 61 | 88 | 22 | 15 (32 B) | 2 |
| solid, clip planes, noon | 46 | 74 | 16 | 15 (32 B) | 0 |
| solid, clip planes, noon, packed | 48 | 74 | 16 | 12 (20 B) | 0 |
| cutout, clip planes, noon, packed | 48 | 76 | 16 | 12 (20 B) | 1 (alpha test) |
| packed, day light (night, rain) | 58 | 76 | 16 | 12 (20 B) | 1 |
| eye in water (+ fog) | +1 | +8 | +3 | | |

`Tests~ -c Release --jar 26.3.jar --measure-batches DIR` on the area snapshots (seed 12345, mostly ocean with lush caves):

| window | opaque triangles | solid (no alpha test) | vertex + index data, 32 B -> 20 B vertices |
|---|---|---|---|
| 64 | 134,524 | 90,992 (68%) | 11.2 -> 7.7 MB |
| 128 | 353,354 | 225,262 (64%) | 29.4 -> 20.1 MB |
| 192 | 765,840 | 473,462 (62%) | 66.3 -> 45.4 MB |
| 256 | 1,253,670 | 780,718 (62%) | 109.5 -> 74.9 MB |

Faces and vertices are unchanged (726,586 faces, 3.02M vertices at 256 here). At 256 the alpha-tested rest is mostly kelp and seagrass (360k of 473k triangles), then grass side overlays, cave vines, leaf litter and short grass. No batch falls back to the classic vertices. Draw calls: one more per batch with both solid and alpha-tested faces (about 216 -> 320 at 256).

### Tests
- `Tests~` (with `--jar` and `--entity-models`): ALL PASS, including the drop-stop cache against the name test, the solid split (flat colours, opaque layers, random tables, the 4-layer flow table, unknown layers, alpha in vertex colours, `solid_pass` off), batches keeping each section's solid faces first, `PackedVertex` against the classic vertex (positions, uv, light code, face direction and shade, layer and table) and the fallback for coordinates out of range.
- Unity: `ShaderCheck.Run` 360 variants, 0 errors (Vulkan and GLES3, with the new keywords); `BatchMeshCheck.Run` (classic and packed uploads read back through Unity, four submeshes) and `RunRenderer` (now also: no non-empty submesh without a material, the skirt glide with every face alpha-tested) pass; `BuildApk` builds.

### To measure on the headset (same view, width 256, about 20 s each)
1. Defaults: PERF fps and frame_ms, VrApi app GPU time, and whether the 5 s hitch is gone in the rain.
2. `ovrgpuprofiler`: fragments shaded/s (expected well under half of 1.35G/s), vertex fetch stall %, vertices shaded/s.
3. A/B one at a time: `"clip_planes": false`, `"solid_pass": false`, `"packed_vertices": false`.
4. Look: terrain cut cleanly at the window edges while gliding (clip planes), leaves, plants and grass side overlays unchanged, water and glass unchanged, night and rain light unchanged, the fog when the eye is in water.

### Not verified (needs the headset)
Everything on the GPU: these are offline counts. Whether Unity's Vulkan device enables `shaderClipDistance` (the variant compiles; if terrain shows outside the window, `"clip_planes": false`).

### Known gaps
- The first time night or rain starts, or the eye enters water, the block materials switch to a variant the GPU hasn't built yet: expect one short hitch then (not prewarmed).
- Random texture rotation stays a per-face branch (coherent across a face); a variant would need another submesh and draw call per batch. The vertex colour is still converted from sRGB per vertex (3 vector multiply-adds).
- If 72 fps isn't reached at 256 wide, the next step is a distance LOD: section columns far from the view centre meshed at 2x2x2 blocks (or the surface skirt's heightfield), full detail near it, with skirts on the seams.

### Headset measurement (Quest 3, 2026-10-15, width 256, region 352x186x352, same view, ~20 s each)

| Run | fps | VrApi app GPU | worst frame | GPU profiler |
|---|---|---|---|---|
| before the GPU work (0.8.0) | 26-31 | 26-35 ms | 145-160 ms (rain rebuild) | 405M vertices/s, 1.35G fragments/s, vertex fetch stall 39% |
| GPU work, `clip_planes` on | 14-27 | 38-57 ms | 120 ms | 127M vertices/s, 0.68G fragments/s, vertex fetch stall 72% |
| GPU work, `clip_planes` off | **55-61** | **13-17 ms** | **19-22 ms** | 331M vertices/s, 1.07G fragments/s, vertex fetch stall 22% |
| `clip_planes` on + `solid_pass` off | 28 | 24-30 ms | 40 ms | stall 69% |
| `clip_planes` on + `packed_vertices` off | 28-29 | 24-33 ms | 42 ms | stall 66% |

Hardware clip planes cost far more than the fragment discard they replace on this GPU, so `clip_planes` is off by default. The weather fix removed the periodic hitch. Width 128 runs a steady 72 fps.

## Smooth gliding (smooth; 2026-10-15)

### On the headset before (Quest 3, 0.8.1, width 96, region 128x186x128, margin 16, gliding or following a walking player)
- A region shift every 0.5-1 s, each a `sections_v1` delta of 96 sections (`re-keyed by (16, 0, 0): 96-180 to mesh, ~670 kept decode+plan 7-24 ms`; the decode and plan already ran on a worker, so that is worker time).
- PERF while moving: p99 37-42 ms, max 60-71 ms; standing still a steady 72 fps, p99 15-17 ms. With `surface_skirt` on: blocks that aren't there under leaves and stone overhangs, and surfaces blinking while moving; both gone with it off.

### What changed (one commit per step)
1. `HITCH` log line for every frame over `hitch_ms` (default 30): the frame's apply, re-key, upload, prewarm, skirt and caps time, uploads and KB, sections meshed, GCs, heap, and `script_ms` (first Update to `onBeforeRender`). Log lines carry no stack trace.
2. Cheaper snapshot apply: palette-derived tables extend instead of being rebuilt per snapshot (block-entity mask with culture-aware `EndsWith`, clip-cap face keys, sound ambience, atlas prewarm, shapes), the atlas grows in place (spare layers, GPU copies) instead of being re-created for a new texture, glide shifts reuse retired grid and light arrays, JSON integers don't allocate, `SectionMesh.From` uses per-thread scratch, re-key reuses its collections; the delta plan looks the sent sections up in a set (separate commit).
3. Batch geometry is written into writable mesh data by the worker that assembles it; the main thread only applies it within `upload_budget_ms` (skirt tiles too).
4. Margin 32 from the smallest width; steady lookahead moves two sections at a time (three at a 48 margin) with 20 blocks of runway left; following a player gets lookahead from the player's velocity and no longer pauses while a shift is in flight.
5. Surface skirt: full blocks and fluids only (leaves, glass, plants, slabs, models are see-through for it), sides only over the column's own solid run, fluid tops at the fluid's height; one keep/drop rule (a tile while its column is pending and none of its window sections is on screen); `surface_skirt` off by default.

### Offline numbers (PC, `Tests~ -c Release --live-glide ws://127.0.0.1:8828/tabletop --height 186 --mesh-slowdown 4 --jar <client jar>`, Paper 26.3 test server)
- New `SMOOTH` line per width and direction: shifts and shifts/s, blocks per shift, main-thread frame time, apply time, decode worker time, garbage per shift (all threads), GC counts, buffers reused; `--old-apply` runs the 0.8.1 apply, `--old-shifts` the 0.8.1 margins and one-section lookahead, `--speed B` caps the glide at B blocks/s.
- Garbage per shift at 96 wide (128x186x128), 8 s glides: 23.7 / 22.7 MB (0.8.1 apply) -> 15.5 / 14.3 MB; the decode alone (`--no-mesh`) 12.6 -> 4.8 MB. GCs in 8 s (gen 0/1/2): 10/10/8 -> 7/7/5. Main-thread apply p50 0.71 -> 0.49 ms and 0.36 -> 0.19 ms on a PC with a palette of about 250 states (the passes that are gone grew with the session palette and ran string tests that are much slower under IL2CPP).
- The delta plan at 256 wide (352x186x352): 28 ms of decode worker time per shift -> 12 ms (and 115-122 MB of garbage per shift in the harness, boxing in the old stubs -> 0.1 MB).
- `--profile-mesh` now prints garbage per stage: `SectionMesh.From` 52.6 KB per section before the scratch buffers (build and specials allocate nothing).
- Shifts, engine-free (`SmoothTests`, 72 Hz, 0.4 s answers): 96 wide at 20 blocks/s, 10 shifts at margin 16 -> 6 at margin 32 with pairs (one section at a time at margin 32: 11), no stall, at least 19.8 blocks of runway when a shift goes out. 256 wide at 40 blocks/s: 46 blocks per shift, no stall. A walking player (4.3 blocks/s) followed for 20 s: 3 shifts over 84 blocks, no stall.
- Shifts, live: 96 wide, 128x186 area, glide capped at 24 blocks/s (`--speed 24`): 0.80 shifts/s, 30 blocks per shift, no stall, 0-1 frames with holes of 760 (0.8.1 on the headset: 16-block shifts, 1.5/s at that speed). At full harness speed (55 blocks/s at 96, 110-118 at 256): 1.75 and 2.25-2.5 shifts/s, 31 and 46-49 blocks per shift, no stall.
- Skirt, live with `--skirt --skirt-factor 2`: 96 wide no surface holes; 256 wide 178-220 frames of 600 with missing surface detail, 1-11 of them not covered by a tile; 0.13-0.18 ms per tile.

### Tests
- `Tests~` (with `--jar` and `--entity-models`): ALL PASS (1,506). New in `SmoothTests.cs`: the HITCH line (threshold, counters per frame, 4 lines a second, off), palette tables (a delta computes only new entries, another palette rebuilds), snapshot buffers (reuse rules, Keep), JSON numbers (integers, negatives, -0, fractions, exponents, long numbers, shared boxes), shift sims (pairs and threes, runway, no stall), follow lead and velocity, the follow not pausing during a shift, skirt overhang, canopy, pond and default off. `MeshTests` skirt planner rewritten for the new rule; `NavTests`/`AreaTests` margins updated (the lazy-margin scope test keeps one-section shifts).
- Unity: compile clean; `BatchMeshCheck.Run` 0 errors; `BatchMeshCheck.RunRenderer` 0 errors (load, shift, edit, grow, shrink, vertical shift, staged jump, lazy margin, skirt while gliding) with the batch mesh data written on workers; `TabletopBatch.BuildApk` built (not installed). No shader changes.

### To measure on the headset
1. `adb logcat -s Unity | grep -E "HITCH|PERF|GLIDE|snapshot"` while gliding at 96 wide for 30 s (stick held one way, then back), then at 256 wide, then following a walking player at 96. For each HITCH line: which part dominates (`apply_ms`, `upload_ms`, `prewarm_ms`, `skirt_ms`, `caps_ms`) or none of them (`ms` far above `script_ms`: render thread, GPU or GC; `gc=1`: a collection in that frame).
2. PERF p99 and max while moving, against 37-42 / 60-71 ms before; the snapshot lines' `re-keyed by (32, 0, 0)` (pairs) and their rate (about half of before at the same speed).
3. A/B (one at a time, same glide): `"mesh_data_on_workers": false`, `"reuse_snapshot_buffers": false`, `"upload_budget_ms": 2` and `4`.
4. `"surface_skirt": true`: glide at 256 wide over forest, cliffs and water: no blocks that aren't there, no blinking, no z-fighting at the hand-off.

### Not verified (needs the headset)
Everything on the Quest: these are PC numbers. `Mesh.MeshData` written from a `Task.Run` thread passed in the editor (`RunRenderer`); on the player a failure falls back to the main-thread copy with a `batch ... mesh data:` warning in logcat. The atlas's in-place growth needs `CopyTextureSupport.Basic` (else it re-creates the array as before).

### Known gaps
- A meshed section still allocates its `SectionMesh` arrays (about 50 KB each); they become garbage when the section is replaced or dropped. Pooling them needs lifetimes across batch assemblies: the next step if HITCH lines show `gc=1`.
- The JSON message text and its lists are still garbage per delta (about 5 MB per shift at 96 wide, 22-27 MB at 256).
- A margin of 32 at 96 wide makes the region 160x186x160 (4.8M blocks instead of 3M): more server reads and a bigger first glide load; meshing is lazy, so frame time doesn't grow.

### Headset check of smooth gliding (Quest 3, 2026-10-16, v1.0.0 candidate)

Gliding back and forth (and following a walking player) with the HITCH log on, skirt off:

| Width | PERF fps (avg) | p99 frame (median of 5 s windows) | worst frame |
|---|---|---|---|
| 64 | 70 | 25 ms | 68 ms |
| 96 | 71.5 | 14.6 ms (was 37-42 ms in 0.8.1) | one 976 ms frame when the margin grew to 32 on the first push |
| 256 | 47 | 52 ms | 148 ms |

The user's verdict: "Looks great". 256 wide while gliding remains GPU-heavy (known issue).

