# Quest setup: server on a Windows PC + Quest 3 app

Versions follow [ironman-quest3-hud's SETUP.md](https://github.com/NickStassen/ironman-quest3-hud/blob/master/docs/SETUP.md): Unity **6000.0.84f1**, Meta XR Core SDK + MRUK **85.0.0**, OpenXR 1.15.1, built-in render pipeline, Android IL2CPP ARM64. The app compiles and builds an APK headless on Linux and runs on a Quest 3 (TESTING.md section 5 and "First headset session"); Troubleshooting lists the compile fixes that were needed.

## Part A: run the server (Windows)

1. **JDK 25**: install Eclipse Temurin 25 (MSI from adoptium.net, tick "Set JAVA_HOME" and "Add to PATH"). Check in a new terminal: `java -version` shows 25. Paper 26.x and the plugin build both need it.
2. **Paper 26.3**: make a folder such as `C:\mc\tabletop`. Download the latest 26.3 build from https://papermc.io/downloads/paper (pick version 26.3; the plugin is built against build 181), save it as `paper.jar` there.
3. **Plugin**: in the repo, `cd server-plugin` and run `gradlew.bat build` (or grab the `tabletop-plugin` artifact from the latest GitHub Actions run). Copy `build\libs\tabletop-craft-ar-plugin-<version>.jar` (the version in the repo's `VERSION` file) into `C:\mc\tabletop\plugins\`.
4. **First start**: in `C:\mc\tabletop`, run `java -Xmx4G -jar paper.jar --nogui`. It stops and asks you to accept the EULA: read https://aka.ms/MinecraftEULA, set `eula=true` in `eula.txt`, start again. Wait for `Done (...)! ` and `[TabletopCraftAR] Link listening on ws://0.0.0.0:8765/tabletop`. The next lines, `Headset URL: ws://192.168.1.50:8765/tabletop (Ethernet)`, one per LAN address of the PC, are what the headset can connect to, and `LAN discovery on UDP 8765` means the headset's **Find servers** (Part D) can find it.
   **Whitelist**: a new 26.x server has `white-list=true` in `server.properties`, so every Java player not on the list is rejected, including you on `localhost`. On a LAN test server, stop the server, set `white-list=false` and start it again, or run `whitelist add <name>` in the console for each player.
5. **Firewall**: allow Java through Windows Defender Firewall on **Private** networks when prompted, or open the ports explicitly in an admin PowerShell:
   `New-NetFirewallRule -DisplayName "tabletop ws" -Direction Inbound -Protocol TCP -LocalPort 8765 -Action Allow -Profile Private`
   `New-NetFirewallRule -DisplayName "mc 25565" -Direction Inbound -Protocol TCP -LocalPort 25565 -Action Allow -Profile Private`
   `New-NetFirewallRule -DisplayName "tabletop discovery" -Direction Inbound -Protocol UDP -LocalPort 8765 -Action Allow -Profile Private` (Find servers; without it the headset falls back to scanning for TCP 8765)
   Make sure the PC's Wi-Fi/Ethernet network profile is **Private**, and that the router has AP/client isolation **off**.
6. **PC IP**: the `Headset URL` lines in the server log (step 4), or `ipconfig` and the IPv4 address of the LAN adapter, e.g. `192.168.1.50`.
7. **Pick the region**: join the server from the Java launcher (**version 26.3**, the "Latest release", multiplayer, `localhost`). In the server console: `op <yourname>`. Stand at the north-west bottom corner of what you want on the table and run `/tabletop setorigin`, then `/tabletop setsize 32 64 32` (or smaller to start, e.g. `32 32 32`). `/tabletop status` confirms.
8. **Smoke test without the headset** (optional): `pip install -r tools/requirements.txt`, then `python tools/fake_quest.py --url ws://127.0.0.1:8765/tabletop --avatar -v --duration 15`. In game you should see the giant head (25 blocks wide) circling 45 blocks above the middle of the region, with two hands below it.

## Part B: Unity project

1. **Unity Hub** -> Installs -> Install Editor -> Archive -> **6000.0.84f1** (or the deep link `unityhub://6000.0.84f1/78ab6fc243d5`). Tick **Android Build Support** with **OpenJDK** and **Android SDK & NDK Tools**.
2. Hub -> Add -> Add project from disk -> pick `quest-app/`. Open it. The first import resolves the Meta XR packages from `Packages/manifest.json` (Unity registry, no scoped registry needed) and generates ProjectSettings. If it opens in **Safe Mode**, there are compile errors: see Troubleshooting.
3. **File > Build Profiles > Android > Switch Platform.**
4. **Edit > Project Settings > XR Plug-in Management > Android tab**: tick **OpenXR**, then enable the **Meta XR** feature group. Don't tick the deprecated "Oculus" plugin.
5. **TabletopCraft > Create Scene** (menu from this project). It creates `Assets/Scenes/Tabletop.unity` with OVRCameraRig (passthrough underlay), MRUK (load scene from device on startup), and TabletopApp, sets Passthrough + Scene support to Required and adds the scene to the build list.
6. **Meta > Tools > Project Setup Tool**, Android tab: **Fix All**, then **Apply All**, repeat until nothing is red. Then check `Assets/Plugins/Android/AndroidManifest.xml` still has `com.oculus.permission.USE_SCENE` and `android.permission.INTERNET`, and `android:usesCleartextTraffic="true"` (plain `ws://` needs it). Re-add them if Meta's tools removed them.
7. **Player Settings**: Company/Product name, package name e.g. `com.tabletopcraft.ar`. Minimum API level as set by Fix All.
8. The generated `ProjectSettings/*.asset`, `Assets/XR/`, `Assets/Oculus/`, `Assets/Resources/`, `Assets/Materials/` and `Assets/Scenes/` are committed. `Packages/packages-lock.json` is git-ignored: on Linux it records the embedded, patched Meta SDK, and `manifest.json` pins exact versions anyway.

### Linux and command line

Meta officially supports only Windows and macOS. On Linux the project compiles and builds for Android, but there's no Link, XR Simulator or Play mode, so every test is an install on the headset. The same notes apply as in [ironman-quest3-hud's SETUP.md](https://github.com/NickStassen/ironman-quest3-hud/blob/master/docs/SETUP.md) (udev rule for vendor `2833`, use Unity's bundled adb).

- **Run `tools/patch-meta-sdk-linux.sh` once before opening the project.** Meta XR Core SDK 85.0.0 doesn't compile in the Linux Editor (`Installer.cs: error CS0103 'downloadedInstallerPath'`). The script embeds 85.0.0 into `quest-app/Packages/` (git-ignored) and applies the fix Meta shipped in 207.0.0.
- Steps 3 to 7 and the build also run headless through `Assets/Scripts/Editor/TabletopBatch.cs`. Close the Editor first, because an open Editor locks the project:

```bash
UNITY=~/Unity/Hub/Editor/6000.0.84f1/Editor/Unity
# OpenXR loader on Android, TabletopCraft > Create Scene, Fix All, save. No -quit: it exits when the fixes are done.
"$UNITY" -batchmode -nographics -projectPath quest-app -buildTarget Android -executeMethod TabletopCraft.EditorTools.TabletopBatch.Setup -logFile setup.log
# -> quest-app/Builds/TabletopCraftAR.apk
"$UNITY" -batchmode -nographics -quit -projectPath quest-app -buildTarget Android -executeMethod TabletopCraft.EditorTools.TabletopBatch.BuildApk -logFile build.log
adb install -r quest-app/Builds/TabletopCraftAR.apk
adb shell am start -n com.tabletopcraft.ar/com.unity3d.player.UnityPlayerGameActivity
```

If il2cpp crashes with a segmentation fault, BuildApk builds again (up to 3 builds; see Troubleshooting). On Linux, Fix All logs an `ArgumentException: Invalid path` from the Meta XR Simulator download. The Simulator doesn't exist for Linux, so ignore it. The server side works on Linux unchanged (JDK 25, `./gradlew build`). On a firewalled box, open TCP 8765 to the LAN (e.g. `sudo ufw allow from 192.168.1.0/24 to any port 8765 proto tcp`).

## Part C: headset

1. Developer Mode on (Meta Horizon phone app), USB debugging allowed, latest OS. `adb devices` lists the headset (Unity bundles adb under `Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools`).
2. **Space Setup** (Settings > Physical space > Space setup): scan the room and **mark the table** as a Table (furniture). The app looks for MRUK `TABLE` anchors; without one it places the world 0.5 m in front of you. Where you put the miniature on the table (hold A, drag, turn) is remembered against that table for the next session, and a scene that loads late never moves a miniature you placed yourself.
3. **Build And Run** (File > Build Profiles > Android > Run Device: Quest 3). Accept the **spatial data** permission on first launch.

## Part D: point the app at the server

**In the headset (no adb needed).** If the app can't reach a server for about 5 seconds after it starts, a **setup card** appears in front of you: the address it tried, what went wrong (`Connection refused`, `no answer within 5 s`, a wrong token...), where `tabletop.json` lives, and what to do. Press the **menu button ≡** (left), open the **Info** tab and click **Server: ... (change)**:
- The app searches the network at once (**Find servers** searches again). Every Tabletop server on your LAN answers with its name (the server's MOTD, or `discovery.name` in config.yml) and shows up as a button, marked `token` if it needs one. Click it to connect. If no server answers (UDP 8765 blocked on the PC, Part A step 5), the app looks for PCs with TCP port 8765 open instead and lists them as `(port open)`.
- Or type the address on the panel keyboard: `?123` switches to digits and symbols, `ABC` back; `del` deletes, `clear` empties, **connect** (or the Connect button) connects. `192.168.1.50` is enough (the app adds `ws://`, `:8765` and `/tabletop`); `192.168.1.50:8800` or a full `ws://host:port/path` work too. **Cancel** keeps the old address.
- Connecting saves the address in `tabletop.json` (every other key is kept) and reconnects. A short message says `Connecting to ...`, then `Connected to ...`. The setup card goes away as soon as the link is up, and while the menu is open.
- A wrong token (close 4003) or an app and plugin that don't match (close 4001) stop the retries and show the card with the reason, until you change the address or the token.

**With adb.** The app reads `tabletop.json` from its files folder (`/sdcard/Android/data/<package>/files/`). Create it on the PC:

```json
{"server": "ws://192.168.1.50:8765/tabletop", "token": "", "skin_player": "YourJavaName"}
```

and push it (after the first launch has created the folder):

```
adb push tabletop.json /sdcard/Android/data/com.tabletopcraft.ar/files/tabletop.json
adb shell am force-stop com.tabletopcraft.ar
```

Then relaunch the app. Alternatively set **ServerUrl** on the TabletopApp component before building. The token must match `auth.token` in the plugin config (empty = off); the headset can't type it yet, so a server with a token needs it in `tabletop.json`. `"discovery_port"` changes the UDP port Find servers asks (the plugin's `discovery.port`, default 8765).

**Block size.** Leave `block_size_m` out: on the first start the app fits the area to the table (block size = min(table width, depth) / area width × 0.95, a 0.8 × 0.6 m table without MRUK), and afterwards it remembers your zoom (below). `"block_size_m": 0.01` makes a 32-block region 32 cm wide.

**What the app remembers.** `prefs.json`, next to `tabletop.json`, is written by the app a second or two after you change something (and when the headset goes off): the block size and the area width it was chosen for, where the table is and how it's turned (relative to the MRUK table, so the next session on the same table puts it back the same way round), the hotbar, the selected block, the menu tab and Show HUD. If the server's area has another width at the next start, the block size is scaled so the miniature keeps its size on the table. `block_size_m`, `area_width` and `show_hud` can be in both files: a value in `tabletop.json` takes effect at the next start when it's new or you changed it there since the last start; otherwise the headset's own choice from `prefs.json` stands. Delete `prefs.json` to start fresh (`adb shell rm /sdcard/Android/data/com.tabletopcraft.ar/files/prefs.json`).

**Giant skin.** `skin_player` is the Java account whose skin the giant wears. Leave it out to use the plugin's `avatar.skin-player` (config.yml), or the default skin if that's empty too. The plugin looks the name up on Mojang's servers in the background, so the PC running Paper needs internet access. Then `/tabletop avatar status` shows `"skin_resolved":true`, and the server log has `SKIN resolved <name>`. This works with `online-mode=true` (recommended) and also on an offline-mode test server. If the lookup fails (wrong name, no internet), the headset gets a `skin_failed` error. Java clients may still show the skin, because they resolve the name themselves. You can also change the skin from the console with `/tabletop avatar skin <name>`.

**Java players' skins on the table.** Each player's skin comes from their game profile, which only an `online-mode=true` server has. On an offline-mode server, everyone gets the default skin the game would show for their UUID (Steve, Alex and the others), read from your `pack.zip` (Part E). Without a pack they get plain colours.

`"show_hud": true` brings back a small head-locked status for debugging (off by default; also the **Show HUD** toggle in the block menu's Info tab, Part J).

Optional lighting keys (the server sends light and biomes when its config has `stream.light` / `stream.biomes` on, the default): `"smooth_lighting": false` gives one light value per face and no ambient occlusion (about 15-18% fewer faces than smooth lighting on real terrain), and `"brightness"` is Minecraft's Brightness slider, 0 (Moody) to 1 (Bright), default 1 (Bright, so caves stay readable over passthrough). `"min_light"` (default 0.2) is how bright light level 0 is drawn, 0..1; everything brighter is rescaled up to full daylight, so a cave reads as dark but visible and the seabed 10 blocks down at about half of daylight. A negative value gives vanilla's own curve (level 0 = 0.15 at Bright).

Optional operator keys (Part L): `"operator_name"` is your name in game chat, `"quick_commands"` adds Console buttons. The `token` also unlocks server commands from the headset (Part L).

Optional area keys (Part K): `"area_width"` starts the app at a width preset with the block size fitted to the table (once per value you put there, see "What the app remembers"), and `"batch_columns"` sets the mesh batch size.

**Headset off, Wi-Fi gone.** Taking the headset off (or the app going to the background) despawns the giant at once and closes the link; putting it back on reconnects, and the table you left is shown meanwhile. If the link drops (Wi-Fi gone, server stopped or frozen: 6 s without anything from the server counts as dropped), the app keeps reconnecting with backoff, a red **Offline: reconnecting...** badge sits on the table's edge toward you, and players and mobs are hidden after 1.5 s, because their positions are stale; the terrain stays. Everything updates when the link is back.

Optional look keys: `"mipmaps": false` turns off the block textures' mipmaps (on by default: distant terrain stops shimmering, about 33% more texture memory), and `"leaves_inner": false` stops drawing the faces between two leaves blocks (vanilla draws them, so canopies look full; on by default, about 0.5-4% more faces in forests).

## Part E: textures (resource pack)

Without a pack the app uses flat colours. To use textures you already own:

1. Copy your client jar, e.g. `%APPDATA%\.minecraft\versions\26.3\26.3.jar` (Linux: `~/.minecraft/versions/26.3/26.3.jar`), or any Java resource pack `.zip`, to a temp folder and rename it `pack.zip` (a jar is a zip). The atlas loader was checked offline against the 26.3 jar; on the headset it's still untested.
2. `adb push pack.zip /sdcard/Android/data/com.tabletopcraft.ar/files/pack.zip`, then restart the app. The menu's Info tab (Part J) shows `PACK yes`.
3. Entity models (boats, minecarts, mobs, armour, and the block entities: chests, shulker boxes, heads, banners, bells, decorated pots, conduits, copper golem statues, lectern and enchanting books). The app ships no model data; generate it from the same client jar with JDK 25 from the repo root, then push it next to `pack.zip`:
   ```
   java tools/entity_models/DumpEntityModels.java %APPDATA%\.minecraft 26.3 entity_models.json     (Windows)
   java tools/entity_models/DumpEntityModels.java ~/.minecraft 26.3 entity_models.json            (Linux)
   adb push entity_models.json /sdcard/Android/data/com.tabletopcraft.ar/files/entity_models.json
   ```
   It reads the game's own model definitions out of `versions/26.3/26.3.jar` and the libraries the launcher downloaded (the game doesn't start) and writes about 90 KB of box sizes, pivots and skin offsets plus the dye colours and sign text colours. Regenerate it after a game update, and after updating the app when its release notes say so (a file from before 0.4 has no block entity layers: chests and banners then stay plain boxes). The app loads it in the background at start; logcat shows `[Tabletop] entity_models.json: 211 models (Minecraft 26.3)`. Without it, entities are boxes of their real size in the average colour of their texture, and the Info tab shows `ENTITIES n (boxes)`.

Don't commit or redistribute the jar, the pack or `entity_models.json` (all three are git-ignored).

## Part F: test with a second Java player

1. On another PC on the same LAN (or a second launcher profile on the same PC), start Minecraft **26.3** -> Multiplayer -> Direct Connection -> `192.168.1.50`. The player must be whitelisted, or `white-list=false` (Part A step 4).
2. Walk around inside the region: a small Minecraft figure with your skin and name walks on the table. Its legs and arms swing with your speed, its head turns and tilts with your view, the body follows like in the game, and it crouches when you sneak. The skin appears once it has downloaded (a second or so). Until then the figure shows the default skin. Break and place blocks: the miniature updates within a second (events are applied next tick, a full diff runs every second).
3. Entities and actions (needs the plugin and app from the entities work; `ENTITIES n` in the Info tab counts them). Mobs, boats, minecarts, dropped items, item frames, paintings and falling blocks in the region show on the table with their Minecraft models and textures from `pack.zip` (babies, variants and colours included; types the app doesn't know show as a box of their size). Get in a boat or minecart or on a horse: your figure sits in it, legs forward, and moves with it. What you hold shows in your figure's hands (and in a zombie's or skeleton's), and armour shows on the body. Swinging, digging and placing: the figure's arm swings, the block being dug shows the game's growing cracks, and a broken block bursts into small particles of its texture. Hit a mob (or get hit): it flashes red. The models need `entity_models.json` (Part E step 3); without it entities are boxes of their size. Without `pack.zip` the models are plain white and items don't show. Server side, `entities.*` and `actions.*` in `config.yml` turn this off or cap it; `/tabletop entities` prints counts, main-thread time and bytes/s.
4. Look up for the giant. With the default `avatar.placement: floating` it is a huge player head wearing the `skin_player` skin, plus two hands, exactly where the headset and controllers are relative to the table. The head turns, tilts and rolls with yours, and the hands follow the controllers. Sizes match a real head (0.25 m) and hand (0.1 m) at the table's scale: a 25-block head and 10-block hands at 1 cm/block, capped at 64 blocks by `avatar.max-head-blocks`. The hands are flat boxes in the block colour closest to your skin's hands (`avatar.hand-block` to override). Standing 1.6 m tall at a table puts the head about 100-160 blocks above the terrain, so look up. Java players more than 128 blocks away horizontally don't receive the entities; raise `entity-tracking-range.display` in `spigot.yml` if needed. For a body instead, `/tabletop avatar placement ground` puts a Mannequin (up to 28.8 blocks tall) on the terrain below you, and `exact` hangs one from the mapped head position.
5. Block entities (`BLOCK ENTITIES n` in the Info tab). Chests (double, ender, trapped, copper), shulker boxes, heads and skulls (player heads with their skin), banners with their patterns, bells, decorated pots with sherds, conduits, copper golem statues and the books on lecterns and enchanting tables show with their Minecraft models, lit like the blocks around them. Sign text shows on both sides in the sign's colour (glowing text with its outline), end portals and gateways show a starfield, beacons their beam (coloured by stained glass above), spawners their spinning mob, and campfires, shelves and vaults their items. Write on a sign or add a banner pattern from Java: the table follows within about a second. The models need `entity_models.json` (Part E step 3); without it these blocks are plain boxes. Server side, `block-entities.*` in `config.yml` turns this off or changes its budget.

Headset controls: **left thumbstick** = glide across the world (hold the **left grip** and push up/down to move up/down; see Part H); **right thumbstick left/right** = turn the table; **right thumbstick up/down** = scale the miniature (also changes the giant's mapping); **hold A** (right) for half a second = re-place the world at the right controller (a tick when you press, a buzz when it moves; a quick tap does nothing, and A is ignored while the right ray is on the block menu); hold **both grips** and move the right hand = drag/rotate; **Y** (left) = area setup (Part G); **click the left thumbstick** = next area width (64 → 96 → 128 → 192 → 256 blocks, Part K). Editing blocks (Part I): **right trigger** = break, **left trigger** = place, **B** = pick block. Block menu (Part J): **menu button ≡** (left) = open/close, **right thumbstick click** = next hotbar slot.

## Part G: setting up a play area in the headset

The server picks the starting region (config.yml `region`). You can change it from the headset at any time. The new area is shared: every connected headset switches to it, and the last request wins.

1. Press **Y** (left controller) to enter area setup. A cyan wireframe box shows the area you're about to request, drawn at the current scale relative to the terrain on the table. A small panel above your left hand shows its size, how many blocks it contains, the estimated face/vertex count, the block size it would use, warnings, and `A confirm  B cancel` (the panel is there only while setup runs; the block menu closes and can't be opened meanwhile). The box is what you will **see**; the headset also asks for a 16-block view margin around it (shown as `+16/16 margin`) so you can glide smoothly (Part H). The margin shrinks when the box gets close to the server's limits.
2. Choose where the area is centred, using **X** (left) to cycle through these modes:
   - **CurrentOrigin**: the area currently shown.
   - **Player**: an online player. Push the **right stick left/right** to step through players; their feet end up about a third of the way up the box.
   - **Coordinates**: hold the **right grip** and move the controller to nudge the box by whole blocks (vertical moves shift it up or down).
3. Size the area:
   - **Left stick**: width (left/right) and depth (up/down), in 8-block steps.
   - **Right stick up/down**: height, in 16-block steps.
   - Or point at one of the yellow handle spheres (+X face, +Z face, top) and hold the **right trigger** to drag that face. The box grows symmetrically around its centre.

   Sizes are clamped to the server's limits (by default 288×384×288 including the margin, and at most 16,777,216 blocks; a server with an older config.yml keeps its own `max_region`, e.g. 128×384×128).
4. Block size: **auto-fit** is on by default. It sets block size = min(table width/X, table depth/Z) × 0.95, so the area fills the table detected by MRUK; without a detected table it assumes a 0.8 × 0.6 m table. Click the **right stick** to switch to keeping your manual scale; outside setup, the right stick still scales the model.
5. Press **A** to confirm (if a glide shift or a width change is still on its way, the request goes out right after it). The server answers with `region_changed` and a new snapshot, and a short "Area set" message appears low in your view, then `Loading area 40 %` until the new area is on the table. The old terrain stays on the table while the new area is meshed in the background ("N sections pending" in the menu's Info tab), then the new one replaces it in one go. Tap **B** to cancel.
6. If the estimate is over the headset budget, the box turns red and A won't confirm. There are two budgets: about 250k faces, and about 300 mesh batches (sections are merged into batches of 2×2 section columns over the full height, each one renderer and about two draw calls; Part K). **Hold B for 1 s** to override the warning. If the server rejects the request (`region_too_large`), the panel shows the server's limits.

Recommended sizes (estimates from seed 12345 terrain; see TESTING.md "Navigation and bigger areas" and "Area size presets and batching"), all with the 16-block margin on top:
- 64×96×64 is a comfortable area to start with.
- 128×96×128 is the recommended maximum until the wider presets are measured on the headset: about 190k faces with the pack and smooth lighting, and 36 mesh batches for the 160×128×160 region it loads.
- 192×96×192 (about 430k faces, 64 batches) and 256×96×256 (about 700k faces, 100 batches) go over the face budget. Batching removed the draw-call limit; whether the headset holds 72 FPS with that many faces is what Part K's measurement hooks are for. The width presets (Part K) skip this budget.
- Taller areas work, but snapshots and load times grow.

## Part H: moving around

You can glide across the whole world without leaving the table. The miniature stays a neat box: terrain slides through it, and the cut sides show the blocks inside, like a cross-section.

| Control | Action |
|---|---|
| **Left thumbstick** | Glide. Forward moves the view away from you (relative to where you look), sideways strafes. Speed is about 0.12 m of table per second at full tilt, so it feels the same at any scale, and ramps up to 3× while you hold the stick. |
| **Left grip (hold) + left thumbstick** | Up/down (stick forward = up); sideways still strafes. Holding both grips drags the table instead, as before. |
| **Right thumbstick left/right** | Turn the table about its centre (push right = turn right). |
| **Right thumbstick up/down** | Scale, as before. |

Moving around only uses the thumbsticks and grips (outside area setup, which owns the sticks while it is open). The index triggers and **B** are left free for editing blocks from the headset.

How it works:
- The server sends a region a bit bigger than what you see: a **view margin** on every side, 16 blocks up to a 96-wide view, 32 at 128–192 and 48 at 256 (16 above and below). The headset shows only the inner box and clips everything outside it, so terrain from the margin slides in smoothly by fractional blocks. It only meshes the part of the margin close to the box (more ahead of you while you glide), so a wide margin costs no frame time. An area set up with a smaller margin grows it the first time you push the stick (one bigger load, then gliding is faster).
- When you've moved about 4 blocks past the region's centre, the headset asks for the region moved by one 16-block section in that direction. While you keep gliding one way it asks for the region further ahead (as many sections as fit while 16 blocks stay loaded behind the box), so the margin ahead of you is runway. Stopping changes nothing; turning round re-centres it once you've come back a few blocks. The server reads and sends only the new slice of sections (a **delta**: about 220 of 4,840 at 256 wide, in about 50 ms). The headset keeps all other meshes and moves them. Nothing on the table blinks or goes empty.
- The glide speed is capped so the view can't outrun the stream (the runway one round trip covers, divided by the measured round trip: about 12 blocks at a 16 margin, 32 at 48) or the meshing (measured on the headset: how fast it fills the sections coming into view). If you outrun them anyway, the view stops at the edge of the margin until the next region arrives. The menu's Info tab (Part J) shows `VIEW` (window size and position), the margin, `loading...` while a request is in flight, and the measured round trip. logcat has a `GLIDE` line every 5 s of stick time: the speed you got, the caps, the mesh rate and how long sections were missing on screen.
- The region is shared: other headsets on the same server move with you (the last request wins).
- The giant and the player figures stay where they belong in the world while the region moves (each pose says which snapshot's origin it's relative to).
- An area set up before this version has no margin. The first push asks the server for the same view plus a margin (needs a server that advertises `view_margin` in `welcome.caps`; older servers step 16 blocks at a time instead).
- Vertical moves stop at the world's build height, also right after area setup moved you to another world (the server sends the new world's height with the change).

**Finding your way.** A small red **N** stands just outside the visible box's north edge (north is -Z in the game). Under the small hotbar by the right controller, a line shows the block coordinates at the centre of the box's floor and the direction you're looking, e.g. `105 64 -35  facing NE`. **Reset view** in the block menu's Info tab turns the table so north is the far side, fits the area to the table again and moves the view back to the middle of the loaded area.

## Part I: editing blocks in the headset

Break and place blocks in the miniature like a creative-mode player. The plugin allows it by default (config.yml `edit.enabled: true`); everyone else on the server sees the changes as normal block changes, and every headset gets them through the usual updates.

1. Point the **right controller** at the terrain. A thin dark outline marks the block you're aiming at (like the game's selection box), and a faint laser runs from the controller to it; when the ray hits nothing, a short faded stub still shows where you point. Under the selected block by the controller, a small line names what you aim at and where it is in the world (`oak stairs @ 105,64,-35`). The ray goes through air and water, so you can aim at the sea floor. Plants, slabs, torches and other non-full blocks are targeted by their whole cell. Only blocks inside the visible box count: the hidden margin around it (Part H) can't be hit or built on, and blocks cut by the box's sides can be hit on their visible part.
2. **Right trigger**: break the outlined block. Hold it to keep breaking every quarter second while you sweep. A door, bed or tall plant goes as a whole.
3. **Left trigger**: place the selected block (unless the block you point at is one a hand uses, such as a door or a lever, or a mob is in the way: then it uses that, as a right click does; hold the **left grip** to always place. Part M). A faint see-through copy of it, turned the way it will be placed, shows the cell it fills, inside a second outline. Both turn red when the placement would be refused (the cell is taken, a torch has nothing to hang on, a door has no room above). As in the game, aiming at short grass, a single snow layer, vines, a fern or similar places into that cell, and placing next to one replaces it; two or more snow layers stay. Hold to keep placing. Placing into water works and waterlogs slabs, stairs and the like; on land they're dry. You can't place outside the area, into a solid block, or a solid block into a player or a mob.
4. **B** (right): pick the outlined block as the selected block, like middle-click; it also goes to the front of the hotbar (Part J). You get the block as its item would place it: a wall torch picks a torch, a door's top half the door, grown wheat young wheat. The selection starts as stone. It floats above the right controller as a small textured cube with its name. Any other block comes from the block menu (Part J). A block the server won't place (fire, a potted plant) isn't picked: "Can't place fire" appears briefly instead.
5. Orientation follows the game's own rules, from the face you click, where on it, and where the controller points:
   - logs, pillars and chains line up with the face you click;
   - most blocks with a facing turn toward you (furnaces, chests, pumpkins, repeaters); stairs, doors, fence gates, beds and campfires face the way you point, anvils sideways;
   - pistons, dispensers, droppers and barrels face you up and down too; observers look away from you;
   - torches, signs, banners, heads and coral fans stand on top and hang on a side as their wall form; hanging signs hang under a block or sideways on a wall;
   - buttons, levers and grindstones go on the floor, a wall or the ceiling; bells on the floor, the ceiling or a wall (between two blocks: double);
   - slabs, stairs and trapdoors go upside down (top half) when you click the underside of a block or the upper half of a side;
   - end rods, lightning rods, amethyst and shulker boxes point out of the face you click; hoppers point into it (down from above);
   - doors get both halves and their hinge on the side you click (or against a wall, or next to another door); beds get their head one block the way you point; tall flowers and grass get both halves;
   - lanterns hang under a block; vines and glow lichen cling to the face; rails run the way you point;
   - placed leaves never decay. Fences, panes, walls, iron bars and stairs connect to their neighbours (the server works that out).

Each edit gives a short buzz. Breaking and placing blocks that already appear in the area shows up at once; a block that's new to the area appears when the server confirms it (about 0.2 s), and holding the trigger doesn't send it twice meanwhile. If the server refuses an edit, the block snaps back and a short message low in your view says why, with block names (for example `Edit refused: stone is in the way`, `Edit refused: a cow is in the way`, `Edit refused: short grass can't stay there`, `Edit refused: outside the world border`). Editing is paused while area setup (Y) is open, because setup uses the right trigger and B, and while the right controller points at the block menu. Moving around (Part H) uses only the thumbsticks and grips, so you can glide and edit at the same time: edits still in flight while the view moves are kept and still snap back if refused.

Not like the game (yet): clicking a slab with the same slab doesn't make a double slab (it goes on top), snow, candles, sea pickles and petals don't stack, two chests don't join into a double chest, and there's no rotate button.

Server options (config.yml `edit`): `enabled`, `require-token` (only allow edits when `auth.token` is set), `max-per-second` (20), `physics` (sand falls, water flows) and `deny` (blocks that can't be placed: command blocks, structure blocks, jigsaws). Each edit is logged as `EDIT session=... break|place world x,y,z ... old -> new`. Edits don't go through protection plugins.

## Part J: the block menu and the hotbar

Build with any block, like the creative inventory. The server sends its list of placeable blocks when the headset connects (every block with an item form, minus air, barriers, light and structure voids and config.yml `edit.deny`); the icons are drawn on the headset from your own `pack.zip`.

| Control | Action |
|---|---|
| **Menu button ≡** (left controller) | Open or close the menu. It floats beside your left hand, turned toward you, and follows the hand. |
| **Right controller ray** on the menu | Hover: the item lights up and its name shows under the grid. A thin laser and a dot show where you point. |
| **Right trigger** on the menu | Click: choose a block (it becomes the selected block and goes to the front of the hotbar, with a short buzz), switch tabs, press a key, choose a hotbar slot, or press the arrows. |
| **Right thumbstick up/down** on the menu | Scroll the grid a row at a time (hold to keep scrolling). |
| **Right thumbstick click** (menu closed) | Select the next hotbar slot. |
| **B** (right) | Pick a block from the miniature (Part I); it goes to the front of the hotbar. |
| **Left trigger** | Place the selected block (Part I), or use the selected item, a door or lever, or a mob (Part M). |

While the ray is on the menu, the right trigger doesn't break blocks, the right stick scrolls the menu instead of scaling or turning the table, and gliding, turning and editing pause. Everything works normally again the moment the ray leaves the menu or the menu closes. Area setup (Y) closes the menu.

The menu, top to bottom:
- **Tabs:** Build, Color, Nature, Func. (functional), Redst. (redstone), Other: the server's categories, close to the game's creative tabs. Then **Use** and **Eggs** (items and spawn eggs, when the server allows using them, Part M), **Search** and **Info**.
- **Grid:** 9 blocks per row, 5 rows; arrows and a scroll bar on the right. Blocks of a family sit together (oak log, wood, planks, stairs, slab, fence...; colored blocks by kind, in the game's dye order). The selected block is framed.
- **Search:** a keyboard on the panel and 3 rows of results. Letters (lower case; **shift** for one capital, twice for caps lock), `?123` for digits and symbols (`/ : . , - _ @ # ~ ^ [ ] { } = + * ! ? " '`), `ABC` back, **space**, **del**, **clear**, and **pick**, which selects the first result. Every word you type must appear in the name or id (`oak st` finds oak stairs); names that start with what you typed come first. An empty search lists every block. The menu uses its own keyboard rather than the Quest system keyboard: the system keyboard needs an extra Android manifest flag, takes focus from the app (passthrough and tracking pause behind it) and can't be tested in a headless build, while the panel keyboard is the same code path as every other button (and the same keyboard will serve chat and commands later).
- **Info:** the full status (link, RTT, world, pack, light, table, poses, mesh, entities, view, area, edit, block list, icons), refreshed 4 times a second, then the **Show HUD** toggle (below), **Reset view** (Part H "Finding your way") and **Server: ... (change)**, which opens the Server field with the keyboard and the servers found on the LAN (Part D).
- **Name line:** the hovered block's name, otherwise the selected block.
- **Hotbar:** your 9 most recent blocks, newest first, one slot per block (picking oak stairs again moves them to the front). The same hotbar floats small just above the right controller while the menu is closed; the selected slot is framed.
- **Area size:** a row of 5 sizes, only when the app provides area presets.

Icons: blocks whose inventory icon is flat in the game (flowers, torches, rails, doors, panes, signs, candles...) show the item sprite; everything else is the item's 3D model seen from the game's inventory angle. They are drawn on a background thread from `pack.zip` (visible ones first, the rest afterwards) and stored under the app's files folder (`icons/`, one folder per pack), so the next start shows them at once; a new `pack.zip` gets a new folder. Names come from the pack's `en_us.json` (Block of Diamond, Redstone Dust). Without `pack.zip` the blocks are flat-coloured cubes with their names written on the tiles.

## What's where (status and messages)

The old status text in the middle of the view is gone:
- **Info tab** in the block menu: the full status.
- **Short messages** (2–3 s, fading, low in your view): link lost and restored, server errors (refused edits, area too large...), local refusals ("Can't place fire"), and area setup results ("Area set", "Requested 64x96x64...", "Area setup cancelled"). `Loading area 40 %` counts up while a new or resized area is meshed (not while gliding). Once, when the first area shows: `No pack.zip` or `No entity_models.json` if those files are missing (Part E).
- **Setup card** (in front of you): no server reached yet, or the server refused the app (Part D).
- **Offline badge** (on the table's edge): the link dropped; the app is reconnecting (Part D "Headset off, Wi-Fi gone").
- **N and coordinates**: Part H "Finding your way".
- **Area setup panel**: above your left hand while setup (Y) runs (Part G).
- **Selected block**: floats above the right controller (Part I), with the small hotbar; an item shows as its icon (Part M).
- **Mob info card**: above the mob or player you used it on (Part M).
- **Chat**: the last lines above the left wrist, bubbles over players, and the operator panel on **X** (Part L).
- **Small HUD** (for debugging): turn on **Show HUD** in the Info tab, or add `"show_hud": true` to `tabletop.json`. It's the same status as the Info tab, about a third of the old size, low and to the left.

## Part K: seeing a wider area (width presets)

**Click the left thumbstick**, or use the **Area size** row in the block menu (Part J), to step the visible width: 64 → 96 → 128 → 192 → 256 → 64 blocks (x and z; the height stays what it is). Area setup (Y) owns the sticks while it is open, so the click does nothing there.

- The area grows or shrinks around the spot you are looking at: the centre of the view stays where it is in the world.
- The miniature keeps its size on the table: the block size is scaled by old width / new width when the new area arrives (64 → 256 makes each block a quarter as wide). The right stick still zooms afterwards, and both grips still drag and turn the table.
- The table never empties. Growing keeps every terrain mesh already on the table and only meshes the new ring around it, nearest first; the server reads and sends only that ring. Shrinking drops the outer part at once and sends almost nothing. Wider presets come with a wider glide margin (Part H: 48 blocks at 256), so 64 → 256 in one go loads a 352-wide region (about 4,500 sections, 2 s on a PC server), but only the part near the box is meshed.
- The menu's Info tab (and the small HUD) shows `AREA <width> wide`, the presets, and the server's max (`max_region.view_width` in config.yml, default 256; presets above it are left out). If the server narrows a request, the status says `server set width ...`.
- It is the same shared region as area setup and gliding: other headsets on the server follow (the last request wins). One region request is in flight at a time, so a click during a glide shift is sent right after it.
- Only one headset's own clicks rescale its block size; another headset's change just shows the new area at your current scale.

For measurements, `tabletop.json` can start the app at a preset: `"area_width": 192` (64, 96, 128, 192 or 256). It applies at the first start after you set or change it (Part D "What the app remembers"); to apply the same value again, delete `prefs.json`. The block size is then fitted to the table like area setup's auto-fit (min(table width, depth) / width × 0.95; 0.8 × 0.6 m without a detected table), so every preset covers the same part of the table. `"batch_columns": 2` sets how many 16-block section columns go into one mesh batch per side (default 2; 4 halves the draw calls but makes every block edit re-upload more). `"mesh_sections_per_job": 4` sets how many sections a meshing job takes in a row (default 4: up to 12 sections per frame on 3 workers; lower it if gliding costs frames). logcat (`adb logcat -s Unity | grep Tabletop`) then shows:

- `AREA_REQUEST width 128 -> 192 ...` and `AREA_RESIZED width 128 -> 192 (asked 192) block 2.97 mm in 640 ms` when a resize is sent and arrives.
- `AREA_LOADED width=192 region=224x128x224 load_s=1.8 faces=... batches=64 renderers=64 draw_calls~128 sections=706/1568 verts=... tris=... gpu_kb=...` once the new area is fully meshed and uploaded.
- `PERF width=192 frames=360 fps=72.0 frame_ms p50=13.9 p90=14.1 p99=16.2 max=22.0 over15ms=3 faces=... pending=0 batches=...` every 5 s: Unity's frame times (`Time.unscaledDeltaTime`) next to VrApi's own FPS lines.

## Part L: the operator panel: chat, console, players, world, server, marks

You are the server's operator without a game account. The operator panel lets you chat with Java players, run server commands and watch the server. Chat and the status work on any server; commands need a token (security, below).

**Server side (once):** set a token in the plugin's `config.yml` and the same one in `tabletop.json`, then restart or `/tabletop reload` and reconnect:

```yaml
auth:
  token: "pick-a-long-random-string"
```

```json
{"server": "ws://192.168.1.50:8765/tabletop", "token": "pick-a-long-random-string", "operator_name": "YourName",
 "quick_commands": ["time set day", "gamerule advance_time false"]}
```

`operator_name` is the name Java players see in chat (letters, digits and `_`, at most 16; without it your `skin_player`, else the server's `chat.default-name`). Java players see your lines as `[Giant] YourName: hello` (config.yml `chat.format`), never as a real player. `quick_commands` adds buttons to the Console tab after the server's `commands.quick`.

| Control | Action |
|---|---|
| **X** (left controller), tap | Open or close the operator panel. It floats beside your left hand like the block menu. |
| **X**, hold 0.6 s | Open the **Chat** tab with the input ready (from closed or from another tab). |
| **Menu button ≡** | While the operator panel is open: switch to the block menu. **X** while the block menu is open switches back. |
| **Right controller ray** on the panel | Hover; the **right trigger** clicks (tabs, buttons, keys, chips, rows). Editing, gliding, turning and scaling pause while the ray is on it, as with the block menu. |
| **Right thumbstick up/down** on the panel | Scroll the chat, the console output, the player list or the game rules (hold to keep scrolling). |
| **Right thumbstick click** on the panel | Console: accept the first suggestion (like Tab). |

**Typing.** The Chat and Console tabs (and Kick) have the same on-panel keyboard as the block menu's Search: click keys with the right trigger; **shift** makes the next letter a capital (twice = caps lock), **?123** switches to digits and the symbols commands need (`/ : . , - _ @ # ~ ^ [ ] { } = + * ! ? " '`), **del** removes the last character, **clear** empties the field, and the wide key sends (**send** in Chat, **run** in the Console, **kick** for a kick reason). The field shows the end of a long line.

The tabs:
- **Chat:** the last 200 lines: player chat (`<Alex> hi`), joins and quits (yellow), deaths (red), advancements (green), `/say` and plugin broadcasts (blue), whispers (pink, from `/tabletop tell <message>`, which every Java player may use) and your own lines (gold). Type on the panel keyboard and press **enter** or **Send**. When the server has `chat.send: false` the tab is read-only.
- **Console:** type a command (with or without `/`) and press enter. The output shows each command (`> /time set day`) with the server's answer under it, failures in red. While you type, up to 6 **suggestions** from the server appear above the field: click one (or the stick click / **tab**) to complete the word. **^ / v** walk the history (the last 100 commands, kept in the app's files folder as `history.json`, so they survive a restart). **@ pointer** runs the command at the block the right controller last aimed at (block editing's outline, so it needs editing allowed on the server, Part I; the block stays the pointer for a minute after the ray moves to the panel): `~ ~ ~` is that block, `~ ~1 ~` the cell on top, `@p` the nearest player to it. So aim at the terrain, then point at the panel and press enter: `summon minecraft:pig ~ ~1 ~`, `setblock ~ ~ ~ minecraft:gold_block`, `fill ~-2 ~ ~-2 ~2 ~ ~2 minecraft:glass`. The quick buttons run their command at once. `say` and `me` are sent as your chat.
- **Players:** everyone online (any world) with game mode, health, food and ping, refreshed every second. Select a player, then: **Here @** teleports them on top of the block you last aimed at (in its dimension), **Heal**, **Feed**, **Kick** (type a reason, enter kicks; **Cancel** backs out) and the four game modes.
- **World:** the region's world: time, day, weather, difficulty, tick state. **Time** day/noon/night/midnight, **Weather** clear/rain/thunder, **Bolt @** (lightning on the block you last aimed at), **Freeze/Unfreeze** and **Step 20** (the game's `/tick`), **Difficulty**, and every game rule the server has, as toggles (a lit toggle is on), with the server's own names (26.x: `advance_time`, `mob_griefing`...). Numeric rules are in the Console (`gamerule random_tick_speed 0`).
- **Server:** TPS (1, 5, 15 min), MSPT, memory, players, the tick state and, per world, loaded chunks, entities, players, the time and the weather, every 2 s.
- **Marks:** jump to another world, the spawn, typed coordinates or a shared bookmark; who you follow (Part N). The Players tab's **View** row follows the selected player or jumps to them once (Part N); the container view and the sign editor also open in this panel (Part N).

Without the token the command buttons are greyed and the note line says **needs auth.token**; chat, Players, World and Server still show their information. The line at the bottom of the panel says what happened last (the last command's answer, or why a button did nothing). The **Info** tab of the block menu has a `TALK` line: chat (as which name, or read-only), commands on or off, status.

**Outside the panel:** the last 5 chat lines float just above your left wrist and fade 10 s after they arrive (hidden while a panel is open). A player's chat also appears as a bubble above their figure on the table for about 6 s (up to 3 lines). Joins and deaths also show as short messages low in your view. Java players see your chat line above the giant's head for 5 s (`chat.bubble-seconds`).

**Security.** A headset command runs with operator rights, like the server console. Keep these in mind:
- Commands are off without `auth.token` (`commands.require-token: true` is the default). With a token, every headset that presents it may run commands.
- The link is plain `ws://`. The token and every command travel **unencrypted** on the LAN; anyone who can capture the traffic can replay the token. Use it on a network you trust. Beyond the LAN, put a TLS reverse proxy (`wss://`) in front (ARCHITECTURE.md §6).
- `commands.deny` refuses the dangerous roots by default (`stop`, `restart`, `reload`, `op`, `deop`, `ban`, `ban-ip`, `pardon`, `pardon-ip`, `whitelist`, `save-off`, `debug`, `jfr`, `perf`, `transfer`), also behind aliases, `minecraft:` prefixes and `execute ... run`. A non-empty `commands.allow` lets only its commands run. Plugin commands that run other commands (and datapack functions) can't be looked into.
- Every command is logged (`CMD session=... /line ok (ms)`), and ops see `[Tabletop: /line]` in game (`commands.notify-ops`).
- Limits: 2 commands and 2 chat lines per second per headset (bursts of 5), 256 characters.

## Part M: using blocks, items and mobs

Act on the world like a creative player's right click: open doors, flip levers, press buttons, use bone meal, buckets, flint and steel, axes, hoes, shovels, shears and spawn eggs, strike lightning, and hit, carry, name, freeze or remove mobs. The plugin allows it by default (config.yml `interact.enabled: true`); the block menu's Info tab has a `HANDS` line saying whether the server granted it.

The **left trigger** does what vanilla's right click does, in the same order:
1. the ray hits a **mob or player** first: the selected item is used on it (shears, a food it breeds with, a spawn egg, a lead, the name tag, the lightning wand); with a block selected, its **info card** opens;
2. the block is one a **hand uses**: doors, trapdoors and fence gates open and close (iron ones too, `interact.iron-doors`), levers flip, buttons press (and pop back up after 1 or 1.5 s), note blocks go up a note, repeaters change delay, comparators mode, daylight detectors invert, bells ring, cakes lose a bite, a full composter empties, a jukebox ejects its disc, lit candles and campfires go out;
3. an **item** is selected: it is used on the face you point at;
4. otherwise the selected **block is placed** (Part I).

So with stone selected, pointing at a door opens it; to place stone against the door (or a chest), hold the **left grip** while you pull the trigger: that's sneaking, which skips 1 and 2. In `tabletop.json`, `"left_trigger": "place"` swaps them: the trigger always places (or uses the selected item) and the grip + trigger does the right-click order.

| Control | Action |
|---|---|
| **Left trigger** | The right-click order above. Placing and item uses repeat while you hold it; doors, levers and mobs act once per pull. |
| **Left grip + left trigger** | Sneak: place the block or use the item, never the block's own use or a mob. |
| **Right trigger** on a mob or player | Hit it (5 damage, a knockback away from you; 4 hits a second while held). Players only when the server has `interact.harm-players: true`. |
| **Right grip** on a mob or player (hold) | Pick it up: after a quarter second a mob is lifted and hangs a block above where you point; a yellow see-through box shows where it will land. Let go to put it down there. Players aren't lifted; they are moved when you let go. Pressing the **left grip** too lets go (both grips drag the table, as before). |
| **B** on a mob | Pick its spawn egg into the selection and the hotbar. |
| **Right trigger** on the info card | Kill, Remove, Freeze / Unfreeze, Glow, Close. |

**What the outline says** (it tells you what the left trigger will do): **dark** with the white ghost block = place, **cyan** = use the block, **orange** = use the item, **yellow box** around a mob or player = act on it (its name shows under the controller). The ray hits mobs by their box (small ones at least half a block), and whatever is nearer, mob or block, wins.

**Items.** The block menu (Part J) has two more tabs: **Use** (flint and steel, fire charge, bone meal, bucket, water / lava / powder snow bucket, axe, honeycomb, hoe, shovel, shears, name tag, lead, wheat, carrot, seeds, and **Lightning**, the wand) and **Eggs** (a spawn egg for every mob the server lets you spawn). Items go into the hotbar like blocks; the selected item floats above the right controller as its icon. Search finds items too. What each does:
- **Bone meal** grows crops, saplings, grass and flowers. **Flint and steel / fire charge** light TNT (it's primed, 4 s), candles, candle cakes and campfires, else light a fire where you point. **Buckets** pour water (it waterlogs a slab or stairs you point at), lava or powder snow into the cell in front of the face; the empty **bucket** aims at water and lava sources (its ray stops at a still source) and picks them up, or the water out of a waterlogged block.
- **Axe**: strip logs and wood, scrape copper back one stage, remove wax. **Honeycomb**: wax copper. **Hoe**: dirt and grass to farmland. **Shovel**: grass to a path, put out a campfire. **Shears**: carve a pumpkin (on a sheep: shear it), stop a vine growing.
- **Spawn eggs**: the mob appears on the face you point at (on a spawner: it spawns that mob); on a mob of the same kind: a baby. **Lightning**: a bolt where you point (or only its flash, `interact.lightning: effect`).
- **Name tag**: select it on the Use tab and type the name on the keyboard that appears under the grid, then use it on a mob. **Lead**: ties the mob to the nearest fence (within 7 blocks), or unties it. **Wheat, carrot, seeds**: the animals that eat them fall in love (a baby grows up faster).

**The info card** floats above the mob (or player) with its type and name, health, owner, how long until a baby grows up, and whether it is frozen, glowing, on a lead or riding. Point the right ray at a button: **Kill**, **Remove** (gone without dying), **Freeze / Unfreeze** (it stands still and ignores everything; it stays frozen across restarts until unfrozen, and `/tabletop unfreeze-all` on the server wakes every mob a headset froze), **Glow**, **Close**. It closes by itself when the mob is gone or after 30 s, and with **B**.

Nothing changes on the table until the server answers (0.1–0.2 s). When it refuses, a short message says why (`Can't: the axe does nothing to stone`, `Can't: fire is off on this server (interact.fire)`). Chests, barrels, shulker boxes, furnaces and other containers open the container view, and signs the sign editor, in the operator panel (Part N); hold the left grip to place against them.

Server options (config.yml `interact`): `enabled`, `require-token`, `max-per-second` (10), `entities` (mobs at all), `harm-players` (false), `iron-doors`, `fire`, `tnt`, `fluids`, `lightning` (true, effect, false), `attack-damage` (5), `deny-spawn` (wither and ender dragon) and `max-spawns-per-minute` (60). Everything stays inside the area like block editing, and protection plugins aren't consulted; fire and lava can spread beyond the area. Each use is logged as `INTERACT session=... item block x,y,z face=... -> new state`, each mob action as `ENTITY session=... op type uuid`.

## Part N: Marks, follow, containers and signs

The operator panel (X, Part L) also moves the view around the world and opens chests and signs. Everything here works without the token, except bookmarks on a server with `marks.require-token: true`.

**Marks tab.** The top line says where the view is (`Overworld  105 64 -35`) and who you follow.
- **World** row: one button per world the server has (Overworld, Nether, End, and any other). The lit one is where the view is. A button moves the view to that world's spawn; the width and the margins stay the same, the height shrinks to fit the Nether and the End (they are 256 high). Another world loads in full, so it takes a moment.
- **Spawn** jumps to this world's spawn. **Stop follow** stops following (below).
- **Go to**: click the field, type `x z` or `x y z` on the keyboard (**?123** for digits and `-`; `~` means here, `~20 ~ ~-5` is 20 east and 5 north of the view centre), then **go** (the keyboard's enter) or **Go**. Without a height the view keeps its own.
- **Bookmarks**, shared with every headset on the server and kept by the server (`plugins/TabletopCraftAR/bookmarks.yml`): **Add mark** names the place under the view centre (`Mark 3`, or type your own) and **Save** (enter) stores it; select a row, then **Go** jumps there (in its world: a row in another world shows `[Nether]`) and **Delete** removes it. The right stick scrolls the list. A server can make them read-only (`marks.edit: false`): the buttons grey out.
- Every jump moves the shared area, so other headsets on the server move with you (the last request wins, as for gliding). In the same world only the new part is loaded; the view lands exactly on the target.

**Following a player.** In the Players tab select someone, then **Follow** (in the **View** row). The view glides after them as they walk, run, ride or fly, a little behind (about a second), and stays still while they wander a block or two or step up and down a few blocks; when they teleport far away, die and respawn, or go through a portal, the view jumps after them, into the Nether or the End too. **Go to** jumps to them once without following. Following stops when you push the left stick, open area setup (Y), use Reset view, jump somewhere from the Marks tab, press **Stop** (Players) or **Stop follow** (Marks), or when they leave the server; a message says why. The giant itself can't be followed: it is where you look at the table.

**Containers.** The left trigger on a chest, trapped chest, copper chest, barrel, shulker box, hopper, dispenser, dropper, furnace, smoker, blast furnace, brewing stand or crafter (the outline is cyan) opens it in the operator panel: the title (`Large Chest` for a double chest, which is one view of 54 slots), the slots with their item icons and counts, in rows of 9 (3×3 for dispensers, droppers and crafters, one row for hoppers, furnaces and brewing stands, whose empty slots say what they take: input, fuel, output; bottle, ingredient, fuel). Java players see the lid open while you look. The view updates by itself when a Java player, a hopper or a command changes it.

| Control | Action |
|---|---|
| **Right trigger** on a slot | Select it (the top line says what it holds). |
| **1 / 16 / 64** | How many items Take and Put move. |
| **Take** | Take that many out of the selected slot (they go nowhere, as a creative player's would). |
| **Put** | Put that many of your selected hotbar block or item into the slot: added to the same item (up to its stack size), or in place of another. |
| **Clear** | Empty the slot. |
| **Sort** | Merge and order the whole container (chests, barrels, shulker boxes, hoppers, dispensers, droppers). |
| **Close**, a tab, X or ≡ | Close the view (the lid closes). |

The server checks every change again: a furnace's fuel slot takes only fuel and its output slot nothing, a brewing stand's slots only what belongs there, a shulker box no shulker box, and when a Java player changed the slot since you looked the change is refused (`the slot changed meanwhile`); the note line says why. A server can make containers view-only or switch them off (`interact.containers: view` or `false`): the buttons grey out, or the left trigger says so.

**Signs.** The left trigger on a sign (standing, wall or hanging) opens the sign editor, filled with what the sign says now. **Front** / **Back** switch sides; select a line with the right trigger and type on the keyboard (**next**, the enter key, moves to the next line); the swatches set the text colour and **Glow** the glowing ink; **Save** sends the sides you changed, and the sign on the table (and in the game) shows the new text a moment later. A waxed sign can't be edited, and another plugin (a protection plugin) may refuse the change; the note line says so. `interact.signs: false` on the server switches the editor off.

To place a block against a chest or a sign instead, hold the left grip while you pull the trigger (Part M).

Logs: `JUMP`, `JUMP landed`, `FOLLOW`, `CONTAINER open`, `SIGN edit` (`adb logcat -s Unity | grep Tabletop`); on the server `MARK`, `CONTAINER`, `CONTAINER_DENIED`, `SIGN`, `SIGN_DENIED`, and `/tabletop status` adds an operator line.

## Logs

```
adb logcat -s Unity | findstr Tabletop
```

The Info tab (Part J) shows `LINK`, `RTT`, `WORLD`, `PACK`, `EDIT` (the selected block, or `off` when the server doesn't allow editing), `LIGHT` (`smooth`/`flat`, or `off` when the server sends no light; then `BIOMES n`, with `(no colormap)` if the pack lacks `colormap/grass.png`), `TABLE`, `POSES`, `AREA` (width, presets and the server max; Part K), the last server error, `BLOCKS n placeable` and the icon counts. Biome colours (grass, leaves, water per biome) need both biomes from the server and a pack with the colour maps; without them the old fixed tints are used. Logcat has `[Tabletop] block registry: N blocks in 6 categories`.

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| Compile errors in `TablePlacement.cs` / `TabletopSceneBuilder.cs` | MRUK/OVR API names differ in 85.0.0 (e.g. `SceneSettings`, `PlaneRect`, `RegisterSceneLoadedCallback`). Fix with the smallest change; the logic is in comments. |
| Build fails: `il2cpp --convert-to-cpp`, `ExitCode 139 (Segmentation fault)`, `Segmentation fault (core dumped)` | On Linux, il2cpp's own bundled .NET 7 runtime crashes now and then while it converts the assemblies; nothing in the project causes it, and the same build passes when run again. `TabletopBatch.BuildApk` builds again by itself (up to 3 builds, and only when every error is a segfault); in the Editor, just build again. A larger stack (`ulimit -s unlimited`) isn't the fix: the crashing thread uses a few KB of stack (TESTING.md section 5). |
| Setup card with `no answer within 5 s` / `Connection refused`, or `LINK connecting ...` forever | Wrong IP (use a `Headset URL` from the server log, or Info > Server > Find servers), PC firewall, network profile Public, AP isolation, or cleartext blocked. Check with `aapt2 dump xmltree <apk> --file AndroidManifest.xml`: there must be `usesCleartextTraffic=true` and no `networkSecurityConfig` (the scene builder turns off Meta's `enableNSCConfig`). Test from the PC with `tools/fake_quest.py --url ws://<PC IP>:8765/tabletop`. |
| Setup card "The server refused the token" (`LINK closed 4003`) | Token mismatch: `token` in tabletop.json must equal `auth.token` in config.yml. The app stops retrying; after fixing the file, Info > Server > Connect (or restarting the app) tries again. |
| Setup card "App and server don't match" (4001) | The plugin and the app are from different versions: install both from the same release. |
| Find servers finds nothing | UDP 8765 is blocked on the PC (Part A step 5), the headset and PC are on different subnets, the router has AP isolation, or config.yml `discovery.enabled` is false. The fallback scan only covers the headset's own /24 and TCP 8765. Type the address from the server log's `Headset URL` line instead. |
| Red "Offline: reconnecting..." badge on the table | The link dropped (Wi-Fi, server stopped or frozen). The app keeps reconnecting by itself; players and mobs come back with the first update. |
| The miniature comes back too big, too small or turned oddly | It's what `prefs.json` remembered. **Reset view** (Info tab) fits it to the table, north away from you; or delete `prefs.json` (Part D). |
| Pink/black blocks | Shader not found: make sure `Assets/Shaders/Blocks.shader` compiled, or assign the two materials on TabletopApp. |
| World appears but mirrored | The Z mirror is in TabletopRenderer (vertices) and PlayerMarkers; both must stay. |
| No table found | Redo Space Setup and mark the table; or hold A to place it manually. |
| Giant head is high up or far away | Expected in `floating` (the default): it is exactly where your headset is, scaled by the block size. Lean in over the table to bring it down, or use a larger block size. `/tabletop avatar placement ground` puts a body on the terrain instead (ARCHITECTURE 3.5). |
| Giant not visible from far away | Display entities are only sent within `entity-tracking-range.display` (spigot.yml, default 128 blocks horizontally) of a player. |
| Giant has the default skin | No `skin_player` in tabletop.json and no `avatar.skin-player`, a misspelt name (a `Skin lookup failed` message), or no internet on the server PC. Check `/tabletop avatar status` (`skin_resolved`). |
| `EDIT off (server)` (Info tab) | The plugin is older than the headset app, `edit.enabled: false`, or `edit.require-token: true` without an `auth.token`. Update the plugin or change config.yml `edit`, then `/tabletop reload` and reconnect. |
| Block menu says "waiting for the server's block list" | The plugin is older than the app (no `block_registry`). Update the plugin. |
| Menu icons are coloured cubes | No `pack.zip` (Part E), or the pack failed to open (Info tab: `icons flat`). |
| Old icons after changing `pack.zip` | Shouldn't happen (the cache folder is named after the pack's size and date); delete the app's `files/icons/` folder to force a redraw. |
| Blocks I break come back after a moment | The server refused the edit (a short message shows why) or never got it (link down: edits aren't queued). |
| Operator panel buttons greyed, "needs auth.token" | The server has no `auth.token` (commands need one by default), or `tabletop.json` has a different token (then the link would be refused with 4003). Set both (Part L) and reconnect. |
| A command says "refused: /... is in commands.deny" | The plugin's `commands.deny` (or a non-empty `commands.allow`) refuses it; edit config.yml and `/tabletop reload` if you really want it from the headset. |
| No chat on the wrist or in the Chat tab | The plugin is older than the app, or `chat.enabled: false`; the Info tab's `TALK` line says what the server granted. |
| Players are plain-coloured figures | They have no skin URL (offline-mode server) and there's no `pack.zip` to take the default skins from (Part E). |
