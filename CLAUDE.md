# CLAUDE.md: agent briefing for tabletop-craft-ar

Read this, then `README.md`, `docs/PROTOCOL.md` (normative), `docs/ARCHITECTURE.md`, `docs/TESTING.md` and `docs/QUEST_SETUP.md`. `docs/PLAN.md` has the phases and `docs/RESEARCH.md` the sources.

## What this is
A Paper plugin streams a box of blocks, player positions and block changes over a JSON WebSocket to a Quest 3 Unity passthrough app, which renders it as a table-top miniature. The Quest sends head and hand poses back at about 30 Hz, and the plugin drives a giant avatar (Mannequin with the SCALE attribute + two ItemDisplay hands) that Java players see. The Quest has no game account and never speaks the game protocol.

## Current state (read carefully)
- **server-plugin/** targets Paper 26.3 on Java 25 and builds with JDK 25 only. It passes end to end on a headless Paper 26.3: `tools/e2e.sh` (the mineflayer bot joins as 26.1 through ViaBackwards on the test server) and `tools/region_e2e.py`. Earlier results were on Paper 1.21.11. Unit tests: `./gradlew build` (JAVA_HOME = JDK 25). Full stack: `tools/e2e.sh` (downloads Paper, offline mode, fake Quest + mineflayer bot). Results are in `docs/TESTING.md`.
- **quest-app/** compiles and builds an APK headless on Linux (`tools/patch-meta-sdk-linux.sh` first, then `TabletopBatch.Setup` / `BuildApk`; see QUEST_SETUP.md Part B "Linux and command line"), but has **not run on a headset yet**. The engine-free scripts (Json, Protocol, LinkClient, WorldModel, BlockRules, GreedyMesher) pass tests under .NET 8 in `quest-app/Tests~` (`dotnet run`, add `-- --live ws://host:8765/tabletop` for a live session). Runtime behaviour of Meta XR, MRUK, TextMesh, shaders and `Texture2DArray` is unverified.
- Pins: Paper **26.3** (build 181) on **Java 25**, Gradle wrapper **9.8.0**, Shadow **9.6.1**, Java-WebSocket **1.6.0** (shaded to `dev.tabletopcraft.ar.libs.websocket`), Unity **6000.0.84f1**, Meta XR Core + MRUK **85.0.0** (same as ironman-quest3-hud). Don't bump to make errors go away.
- The scene is built in code: **TabletopCraft > Create Scene** (`Assets/Scripts/Editor/TabletopSceneBuilder.cs`). Fix the builder instead of hand-editing scene files.

- **Area setup (v1.1):** `region_request`/`region_query` → `region_changed`/`region_info`. The region is shared by all headsets, and the last writer wins. Caps come from config.yml `max_region`. The plugin reads regions asynchronously: chunks load via getChunkAtAsync, ChunkSnapshots are taken on the main thread, and the walk runs on a worker, keeping main-thread time under 3 ms. Diffs work the same way. Quest side: `XR/AreaSetupMode.cs` (input, wireframe; unverified in Unity) drives the engine-free `World/AreaSetupState.cs` and `World/AreaMath.cs` (fit and budget; tested in Tests~). `TabletopRenderer` meshes on worker threads after prewarming the atlas on the main thread. Never call `ResourcePackAtlas.Face` for an unseen state off the main thread.
- Tests: `tools/e2e.sh` (refuses to start if 25565/8765 are busy), `tools/region_e2e.py` (needs the e2e world), `tools/measure_regions.py OUT "c:cx,cy,cz,dx,dy,dz,y0"` and `quest-app/Tests~ dotnet run -- --measure OUT` for sizes and face counts.

## First hardware session checklist
Prereqs (human): Unity 6000.0.84f1 with Android module, Quest in Developer Mode connected over USB with debugging allowed, and the vanilla game installed for that version on this PC.
Work through these in order and record what you verify in `README.md` Status and `docs/TESTING.md`:
0. Push textures: find the client jar (Windows: `%APPDATA%\.minecraft\versions\26.3\26.3.jar`, Linux: `~/.minecraft/versions/26.3/26.3.jar`; if missing, ask the human to launch 26.3 once in the launcher). Copy it to a temp `pack.zip`, then `adb shell mkdir -p /sdcard/Android/data/com.tabletopcraft.ar/files/` and `adb push pack.zip` there (re-push after a fresh install if the folder was wiped). Never commit the jar or pack.zip.
1. Open `quest-app/`, resolve Meta XR/MRUK 85.0.0 packages, fix compile errors with minimal changes (no version bumps). Run **TabletopCraft > Create Scene**, then build the APK. (Done on Linux 2026-10-13, headless: see TESTING.md section 5.)
2. Start the plugin on Paper 26.3 / Java 25 on the PC. A new 26.x server has `white-list=true`: set it to false or `whitelist add` the Java players. Confirm the Quest can reach port 8765 over Wi-Fi (LAN IP, firewall rule).
3. `adb install` the APK. Watch `adb logcat -s Unity | grep Tabletop` for LINK up and the first snapshot.
4. Confirm MRUK detects the table and the area wireframe snaps to it.
5. Exercise area setup: resize, auto-fit, small (16x16) and max (128x96x128). Note FPS.
6. Confirm the atlas loads from `pack.zip` (PACK in the status text). The parser was checked offline against the 26.3 client jar (sprite-object textures and fluids fixed), not yet on the headset. Fix grey grass/leaves (biome tint) and missing-texture fallbacks.
7. Place and break blocks from a Java client and confirm live diffs on the table.
8. Check the giant avatar (Mannequin + hands) tracks head and hand poses.
9. Profile with OVR Metrics. Tune the 250k-face soft cap if needed.
10. Enable CI: `gh auth refresh -s workflow`, then `git mv ci/github-actions-ci.yml .github/workflows/ci.yml`.

## Protocol rules
- `docs/PROTOCOL.md` is the contract for the plugin, Quest app and tools. A breaking change bumps the version and adds a changelog entry. Golden examples are in `tools/fixtures/`; Java (`FixtureTest`) and Python tests read them.
- Frames: tabletop frame is right-handed, metres, axes parallel to the world, origin at the region's local (0,0,0) corner. Unity conversion is `p=[ux,uy,-uz]`, `q=[-qx,-qy,qz,qw]` (PoseSender/TabletopPose). Mesh vertices are mirrored on Z and triangles re-wound in TabletopRenderer.
- Known gaps vs the spec are listed in `docs/TESTING.md` under "Spec deviations".

## How to debug
- Server: `/tabletop status`, `/tabletop avatar status` (logs `AVATAR_STATE` / `AVATAR_ENTITY`). Set `debug.avatar-state: true` to stream entity positions to clients.
- Quest: `adb logcat -s Unity | grep Tabletop`. The status text under your view shows LINK, RTT, WORLD, PACK, TABLE and POSES.

## Repo rules
- **This repo will be public.** No personal details: no employers or jobs, family or relationships, events, health, money, home location, hostnames or schedule. Keep docs strictly technical, and keep IPs and usernames in examples generic.
- Default branch **master**. Small conventional commits (`feat(plugin):`, `fix(quest):`, `docs:`). Never force-push or rewrite history.
- Never commit `eula.txt`, server jars, worlds, resource packs, client jars, APKs or Unity's generated folders (`Library/`, `Temp/`, `Builds/`, `UserSettings/`).
- Cross-link with real links: [ironman-quest3-hud](https://github.com/NickStassen/ironman-quest3-hud) (Unity/Meta XR setup, Quest networking notes) and the idea file [VR-7](https://github.com/NickStassen/ideas/blob/master/vr/07-minecraft-ar-tabletop.md).
- When something is verified on a headset, update the Status table in `README.md`.
