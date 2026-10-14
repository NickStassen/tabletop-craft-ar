# Quest setup: server on a Windows PC + Quest 3 app

Versions follow [ironman-quest3-hud's SETUP.md](https://github.com/NickStassen/ironman-quest3-hud/blob/master/docs/SETUP.md): Unity **6000.0.84f1**, Meta XR Core SDK + MRUK **85.0.0**, OpenXR 1.15.1, built-in render pipeline, Android IL2CPP ARM64. The Quest app code has **not** been opened in Unity yet, so budget time for compile fixes (see Troubleshooting).

## Part A: run the server (Windows)

1. **JDK 25**: install Eclipse Temurin 25 (MSI from adoptium.net, tick "Set JAVA_HOME" and "Add to PATH"). Check in a new terminal: `java -version` shows 25. Paper 26.x and the plugin build both need it.
2. **Paper 26.3**: make a folder such as `C:\mc\tabletop`. Download the latest 26.3 build from https://papermc.io/downloads/paper (pick version 26.3; the plugin is built against build 181), save it as `paper.jar` there.
3. **Plugin**: in the repo, `cd server-plugin` and run `gradlew.bat build` (or grab the `tabletop-plugin` artifact from the latest GitHub Actions run). Copy `build\libs\tabletop-craft-ar-plugin-0.1.0.jar` into `C:\mc\tabletop\plugins\`.
4. **First start**: in `C:\mc\tabletop`, run `java -Xmx4G -jar paper.jar --nogui`. It stops and asks you to accept the EULA: read https://aka.ms/MinecraftEULA, set `eula=true` in `eula.txt`, start again. Wait for `Done (...)! ` and `[TabletopCraftAR] Link listening on ws://0.0.0.0:8765/tabletop`.
   **Whitelist**: a new 26.x server has `white-list=true` in `server.properties`, so every Java player not on the list is rejected, including you on `localhost`. On a LAN test server, stop the server, set `white-list=false` and start it again, or run `whitelist add <name>` in the console for each player.
5. **Firewall**: allow Java through Windows Defender Firewall on **Private** networks when prompted, or open the ports explicitly in an admin PowerShell:
   `New-NetFirewallRule -DisplayName "tabletop ws" -Direction Inbound -Protocol TCP -LocalPort 8765 -Action Allow -Profile Private`
   `New-NetFirewallRule -DisplayName "mc 25565" -Direction Inbound -Protocol TCP -LocalPort 25565 -Action Allow -Profile Private`
   Make sure the PC's Wi-Fi/Ethernet network profile is **Private**, and that the router has AP/client isolation **off**.
6. **PC IP**: `ipconfig` and note the IPv4 address of the LAN adapter, e.g. `192.168.1.50`.
7. **Pick the region**: join the server from the Java launcher (**version 26.3**, the "Latest release", multiplayer, `localhost`). In the server console: `op <yourname>`. Stand at the north-west bottom corner of what you want on the table and run `/tabletop setorigin`, then `/tabletop setsize 32 64 32` (or smaller to start, e.g. `32 32 32`). `/tabletop status` confirms.
8. **Smoke test without the headset** (optional): `pip install -r tools/requirements.txt`, then `python tools/fake_quest.py --url ws://127.0.0.1:8765/tabletop --avatar -v --duration 15`. In game you should see the giant circling the middle of the region.

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

On Linux, Fix All logs an `ArgumentException: Invalid path` from the Meta XR Simulator download. The Simulator doesn't exist for Linux, so ignore it. The server side works on Linux unchanged (JDK 25, `./gradlew build`). On a firewalled box, open TCP 8765 to the LAN (e.g. `sudo ufw allow from 192.168.1.0/24 to any port 8765 proto tcp`).

## Part C: headset

1. Developer Mode on (Meta Horizon phone app), USB debugging allowed, latest OS. `adb devices` lists the headset (Unity bundles adb under `Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools`).
2. **Space Setup** (Settings > Physical space > Space setup): scan the room and **mark the table** as a Table (furniture). The app looks for MRUK `TABLE` anchors; without one it places the world 0.5 m in front of you.
3. **Build And Run** (File > Build Profiles > Android > Run Device: Quest 3). Accept the **spatial data** permission on first launch.

## Part D: point the app at the server

The app reads `tabletop.json` from its files folder (`/sdcard/Android/data/<package>/files/`). Create it on the PC:

```json
{"server": "ws://192.168.1.50:8765/tabletop", "token": "", "block_size_m": 0.01, "skin_player": "YourJavaName"}
```

and push it (after the first launch has created the folder):

```
adb push tabletop.json /sdcard/Android/data/com.tabletopcraft.ar/files/tabletop.json
adb shell am force-stop com.tabletopcraft.ar
```

Then relaunch the app. Alternatively set **ServerUrl** on the TabletopApp component before building. `block_size_m` 0.01 makes a 32-block region 32 cm wide. The token must match `auth.token` in the plugin config (empty = off).

**Giant skin.** `skin_player` is the Java account whose skin the giant wears. Leave it out to use the plugin's `avatar.skin-player` (config.yml), or the default skin if that's empty too. The plugin looks the name up on Mojang's servers in the background, so the PC running Paper needs internet access. Then `/tabletop avatar status` shows `"skin_resolved":true`, and the server log has `SKIN resolved <name>`. This works with `online-mode=true` (recommended) and also on an offline-mode test server. If the lookup fails (wrong name, no internet), the headset gets a `skin_failed` error. Java clients may still show the skin, because they resolve the name themselves. You can also change the skin from the console with `/tabletop avatar skin <name>`.

**Java players' skins on the table.** Each player's skin comes from their game profile, which only an `online-mode=true` server has. On an offline-mode server, everyone gets the default skin the game would show for their UUID (Steve, Alex and the others), read from your `pack.zip` (Part E). Without a pack they get plain colours.

Optional lighting keys (the server sends light and biomes when its config has `stream.light` / `stream.biomes` on, the default): `"smooth_lighting": false` gives one light value per face and no ambient occlusion (about 15-18% fewer faces than smooth lighting on real terrain), and `"brightness"` is Minecraft's Brightness slider, 0 (Moody) to 1 (Bright), default 1 (Bright, so caves stay readable over passthrough). `"min_light"` (default 0.2) is how bright light level 0 is drawn, 0..1; everything brighter is rescaled up to full daylight, so a cave reads as dark but visible and the seabed 10 blocks down at about half of daylight. A negative value gives vanilla's own curve (level 0 = 0.15 at Bright).

## Part E: textures (resource pack)

Without a pack the app uses flat colours. To use textures you already own:

1. Copy your client jar, e.g. `%APPDATA%\.minecraft\versions\26.3\26.3.jar` (Linux: `~/.minecraft/versions/26.3/26.3.jar`), or any Java resource pack `.zip`, to a temp folder and rename it `pack.zip` (a jar is a zip). The atlas loader was checked offline against the 26.3 jar; on the headset it's still untested.
2. `adb push pack.zip /sdcard/Android/data/com.tabletopcraft.ar/files/pack.zip`, then restart the app. The status text shows `PACK yes`.

Don't commit or redistribute the jar or pack.

## Part F: test with a second Java player

1. On another PC on the same LAN (or a second launcher profile on the same PC), start Minecraft **26.3** -> Multiplayer -> Direct Connection -> `192.168.1.50`. The player must be whitelisted, or `white-list=false` (Part A step 4).
2. Walk around inside the region: a small Minecraft figure with your skin and name walks on the table. Its legs and arms swing with your speed, its head turns and tilts with your view, the body follows like in the game, and it crouches when you sneak. The skin appears once it has downloaded (a second or so). Until then the figure shows the default skin. Break and place blocks: the miniature updates within a second (events are applied next tick, a full diff runs every second).
3. Look for the giant (up to 28.8 blocks tall, scale 16, wearing the `skin_player` skin). With the default `avatar.placement: ground`, it stands on the terrain inside the region, below where the headset is. When you stand back from the table it waits at the region's edge, facing where you look. Its head turns with yours, and the two white blocks move like your controllers or hands relative to your head, scaled up to its size. For the old behaviour, where the head is exactly where the headset is above the table, run `/tabletop avatar placement exact`. At 1 cm/block that puts it far off and high up.

Headset controls: **A** (right) = re-place the world at the right controller; hold **both grips** and move the right hand = drag/rotate; **right thumbstick up/down** = scale the miniature (also changes the giant's mapping).

## Part G: setting up a play area in the headset

The server picks the starting region (config.yml `region`). You can change it from the headset at any time. The new area is shared: every connected headset switches to it, and the last request wins.

1. Press **Y** (left controller) to enter area setup. A cyan wireframe box shows the area you're about to request, drawn at the current scale relative to the terrain on the table. The status panel shows its size, how many blocks it contains, the estimated face/vertex count, and the block size it would use.
2. Choose where the area is centred, using **X** (left) to cycle through these modes:
   - **CurrentOrigin**: the area currently shown.
   - **Player**: an online player. Push the **right stick left/right** to step through players; their feet end up about a third of the way up the box.
   - **Coordinates**: hold the **right grip** and move the controller to nudge the box by whole blocks (vertical moves shift it up or down).
3. Size the area:
   - **Left stick**: width (left/right) and depth (up/down), in 8-block steps.
   - **Right stick up/down**: height, in 16-block steps.
   - Or point at one of the yellow handle spheres (+X face, +Z face, top) and hold the **right trigger** to drag that face. The box grows symmetrically around its centre.

   Sizes are clamped to the server's limits (by default 128×384×128 and at most 2,097,152 blocks).
4. Block size: **auto-fit** is on by default. It sets block size = min(table width/X, table depth/Z) × 0.95, so the area fills the table detected by MRUK; without a detected table it assumes a 0.8 × 0.6 m table. Click the **right stick** to switch to keeping your manual scale; outside setup, the right stick still scales the model.
5. Press **A** to confirm. The server answers with `region_changed` and a new snapshot, and the terrain reloads in the background ("N sections pending" in the status panel). Tap **B** to cancel.
6. If the estimate is over the headset budget (about 250k faces by default), the box turns red and A won't confirm. **Hold B for 1 s** to override the warning. If the server rejects the request (`region_too_large`), the panel shows the server's limits.

Recommended sizes (estimates; see TESTING.md "Real terrain"):
- 64×96×64 is a comfortable area to start with.
- 128×96×128 is the recommended maximum for normal terrain.
- Taller areas work, but snapshots and load times grow.

## Logs

```
adb logcat -s Unity | findstr Tabletop
```

The floating status text shows `LINK`, `RTT`, `WORLD`, `PACK`, `LIGHT` (`smooth`/`flat`, or `off` when the server sends no light; then `BIOMES n`, with `(no colormap)` if the pack lacks `colormap/grass.png`), `TABLE`, `POSES` and the last server error. Biome colours (grass, leaves, water per biome) need both biomes from the server and a pack with the colour maps; without them the old fixed tints are used.

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| Compile errors in `TablePlacement.cs` / `TabletopSceneBuilder.cs` | MRUK/OVR API names differ in 85.0.0 (e.g. `SceneSettings`, `PlaneRect`, `RegisterSceneLoadedCallback`). Fix with the smallest change; the logic is in comments. |
| `LINK connecting ...` forever | Wrong IP, PC firewall, network profile Public, AP isolation, or cleartext blocked. Check with `aapt2 dump xmltree <apk> --file AndroidManifest.xml`: there must be `usesCleartextTraffic=true` and no `networkSecurityConfig` (the scene builder turns off Meta's `enableNSCConfig`). Test from the PC with `tools/fake_quest.py --url ws://<PC IP>:8765/tabletop`. |
| `LINK closed 4003` | Token mismatch. |
| Pink/black blocks | Shader not found: make sure `Assets/Shaders/Blocks.shader` compiled, or assign the two materials on TabletopApp. |
| World appears but mirrored | The Z mirror is in TabletopRenderer (vertices) and PlayerMarkers; both must stay. |
| No table found | Redo Space Setup and mark the table; or press A to place it manually. |
| Giant floats high or is far away | `avatar.placement` is `exact` (see ARCHITECTURE 3.5): positions are mapped exactly and the body is clamped to scale 16. Use `/tabletop avatar placement ground`. |
| Giant has the default skin | No `skin_player` in tabletop.json and no `avatar.skin-player`, a misspelt name (`skin_failed` in the status text), or no internet on the server PC. Check `/tabletop avatar status` (`skin_resolved`). |
| Players are plain-coloured figures | They have no skin URL (offline-mode server) and there's no `pack.zip` to take the default skins from (Part E). |
