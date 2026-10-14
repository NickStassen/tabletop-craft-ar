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

- **Draw calls are the likely limit.** Every non-empty 16³ section is its own mesh (one draw call, two when it has water, glass or plants). The headset has only been measured at 64×96×64: about 50 meshes, 72 FPS, 2.5 ms app GPU time. A 128-wide window loads about 330 meshes; 192 about 700; 256 about 1,200–1,800. Area setup now warns above about 600 estimated meshes (and still above 250k faces); hold B to try bigger areas anyway. Bigger areas need the section meshes batched (for example 2×2×2 sections per mesh), which is the next step.
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

