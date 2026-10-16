# tabletop-craft-ar: overview

A live block world from a **Minecraft: Java Edition** server, shown as a miniature on a real table in **Meta Quest 3** passthrough AR. Java players walk around inside it in real time, and they see the headset user as a giant whose head and hands follow the headset and controllers.

> **NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.**
> This project ships no Mojang assets: textures and sounds come from a resource pack or client jar you supply.

This page has the details the [README](../README.md) leaves out: how the parts fit together, every feature with a link to its setup section, what has been verified on a headset, versions, and the server quick start. Known gaps are in [KNOWN_ISSUES.md](KNOWN_ISSUES.md).

Related: the idea write-up [VR-7 in NickStassen/ideas](https://github.com/NickStassen/ideas/blob/master/vr/07-minecraft-ar-tabletop.md) and the sister project [ironman-quest3-hud](https://github.com/NickStassen/ironman-quest3-hud), which has the Unity 6000.0.84f1 + Meta XR setup this project reuses.

## How it works

```
Java players (LAN) ──game protocol──▶ Paper 26.3 + tabletop plugin ◀──JSON over WebSocket (ws://host:8765/tabletop)─────▶ Quest 3 app
                                        │ chunk_snapshot / block_update / player_positions ──────────────────────▶ │ greedy-meshed miniature on the table
                                        │ ◀────────────────────────────────────────── pose (head + hands, 30 Hz)    │
                                        └ giant avatar: floating player-head + 2 hand ItemDisplays (or a Mannequin)
```

- **server-plugin/**: Paper plugin (Java 25, Gradle Kotlin DSL, Java-WebSocket 1.6.0 shaded). Streams a configurable box (default 32x64x32) and drives the giant.
- **quest-app/**: Unity 6000.0.84f1 project skeleton + C# scripts (link client, protocol, greedy mesher, resource-pack atlas, MRUK table placement, pose sender, player markers). Runs on a Quest 3 (first session 2026-10-13) and builds headless on Linux. See [QUEST_SETUP.md](QUEST_SETUP.md).
- **tools/**: Python fake-Quest client, protocol fixtures, the mineflayer second-player bot, and `e2e.sh`, which runs the whole server-side stack headless.
- **docs/**: [ARCHITECTURE](ARCHITECTURE.md), [PROTOCOL](PROTOCOL.md) (normative wire format, v1), [PLAN](PLAN.md), [RESEARCH](RESEARCH.md), [TESTING](TESTING.md), [QUEST_SETUP](QUEST_SETUP.md), [KNOWN_ISSUES](KNOWN_ISSUES.md).

## Features in detail

**The table world.** The plugin streams a box of the world (a region; config.yml `max_region` caps it at 352 x 384 x 352 by default) to the headset, which meshes it into a miniature with the textures and block models from your own client jar, biome colours, Minecraft-style smooth lighting, water and waterlogging, and animated textures. MRUK finds the table (mark it as a Table in Space Setup); both grips drag and turn the world, and the right stick zooms from a block on the desk to standing in the terrain. Area setup (Y) resizes and fits the region, and width presets show up to 256 blocks across. Setup: [Part G](QUEST_SETUP.md#part-g-setting-up-a-play-area-in-the-headset), [Part K](QUEST_SETUP.md#part-k-seeing-a-wider-area-width-presets), textures in [Part E](QUEST_SETUP.md#part-e-textures-resource-pack).

**Moving around.** The left stick glides the visible window through a larger region (left grip + stick goes up and down, right stick X turns), streamed as 16-block sections so it stays smooth. The operator panel's Marks save places, including in the Nether and the End, and a tap jumps there; follow keeps a player in view. Setup: [Part H](QUEST_SETUP.md#part-h-moving-around), [Part N](QUEST_SETUP.md#part-n-marks-follow-containers-and-signs).

**Building.** The right trigger breaks, the left trigger places and B picks the block you point at, with vanilla placement rules, two-block items (doors, beds), a placement preview and a block menu with a hotbar (≡). Edits are optimistic on the headset and confirmed by the server; Java players' changes show up live. Setup: [Part I](QUEST_SETUP.md#part-i-editing-blocks-in-the-headset), [Part J](QUEST_SETUP.md#part-j-the-block-menu-and-the-hotbar).

**Items and mobs.** The left trigger acts like vanilla's right click: open doors, flip levers, press buttons, use bone meal, buckets, flint and steel, tools, shears and spawn eggs, strike lightning, and hit, carry, name, freeze or remove mobs. Chests and signs can be edited from the headset. Setup: [Part M](QUEST_SETUP.md#part-m-using-blocks-items-and-mobs), [Part N](QUEST_SETUP.md#part-n-marks-follow-containers-and-signs).

**Power tools.** Hold B to undo the last edit group (and redo it from the Tools tab). The box wand marks two corners; then Fill, Walls, Hollow, Clear, Replace or Clone the box (up to 32768 blocks, built a slice per tick). Sphere, cylinder and disc brushes paint, replace, erase or smooth with a radius from 1 to 8. Setup: [Part O](QUEST_SETUP.md#part-o-power-tools-undo-boxes-and-brushes).

**The operator panel (X).** Chat with Java and Bedrock players (your lines show as `[Giant] YourName`), run console commands with a token, watch players, the world and the server, and keep Marks. Setup: [Part L](QUEST_SETUP.md#part-l-the-operator-panel-chat-console-players-world-server-marks).

**A living world.** Players appear as skinned, animated figures; mobs, items, horse gear and leads, particles, explosions, flowing water and lava, opening lids and ringing bells move on the table. Day and night and the weather (rain, snow, thunderstorms) follow the server. With `sounds.zip` on the headset the table sounds like the game, from where things happen on the miniature. Setup: [Part P](QUEST_SETUP.md#part-p-sound-and-getting-your-files-from-your-server).

**The giant.** Java players see the headset user as a floating player head and two hands that follow the headset and controllers (or as a Mannequin), wearing the skin you choose. Bedrock players see a large Mannequin. See [Does it work with Bedrock?](#does-it-work-with-bedrock).

**Files from your own install.** Since 1.1.0 the plugin builds `pack.zip`, `entity_models.json` and `sounds.zip` from the server PC's own Minecraft install (start Minecraft: Java Edition once there) and sends them to your paired headsets on the LAN, once (about 75 MB). They can still go on the headset by adb. Setup: [Store install](QUEST_SETUP.md#the-game-files), [Part E](QUEST_SETUP.md#part-e-textures-resource-pack).

**Easy setup (1.1.0).** The server starter (`start.bat`, `start.sh`) downloads Paper, the plugin and optionally Geyser, asks you to accept the EULA and starts the server; the app finds it on the Wi-Fi and pairs with a 6-digit code (`tabletop pair <code>`, `tabletop devices`, `tabletop unpair`). Setup: [QUEST_SETUP Store install](QUEST_SETUP.md#store-install-recommended).

## Status

What has been tested and verified, and where. Each entry is backed by the sessions recorded in [TESTING.md](TESTING.md).

| Part | State |
|---|---|
| Plugin: snapshot, block updates, players, subscribe, errors, timeouts | Tested end to end on a headless Paper 26.3 server (and earlier on 1.21.11); see [TESTING](TESTING.md) |
| Giant avatar following poses | Floating head + hands (default): positions and head rotation match the poses exactly on a 26.3 test server, and a mineflayer player receives the three displays (TESTING section 7); not yet seen by eye. Mannequin modes: on the first headset session a Java client on an online-mode server saw the giant follow the headset wearing the user's skin (exact placement); ground placement was tried and replaced as the default |
| Java players on the table (skinned, animated figures) | Working on a Quest 3 headset: a Java player's figure shows up on the miniature (see the "Both sides of the same world" clip below). Geometry, UVs, animation and skin rules are unit-tested, and skin download was checked in the Unity Editor |
| Quest C# protocol/mesher code | Compiled and unit-tested with .NET 8 outside Unity, plus a live session against the plugin |
| Block editing from the headset (break, place, pick) | Verified on a Quest 3 (the recordings below), and tested live against Paper 26.3 (unit tests, `Tests~ --live-edit`, `tools/edit_check.py`). See [TESTING](TESTING.md) "Block editing" and [QUEST_SETUP](QUEST_SETUP.md) Part I |
| Quest app in Unity | Compiles and builds an APK on Linux, headless (Unity 6000.0.84f1, Meta XR 85.0.0); see [TESTING](TESTING.md) section 5 |
| Quest app on a headset | First session on a Quest 3: passthrough, Wi-Fi link to Paper 26.3 (RTT 13-42 ms), a 64x96x64 area textured from the 26.3 client jar at a steady 72 FPS (~2.5 ms app GPU), the Java player on the table, and the giant (with the user's skin) seen from a Java client. MRUK table detection works (the desk marked as a Table in Space Setup). Area setup (Y) could not open on the headset before 0.3.1 (a button-mapping bug); see [TESTING](TESTING.md) "First headset session" |
| 0.2.0-0.3.1: block menu, width presets, fixes and polish | The block menu and hotbar, width presets up to 256, vanilla placement rules, two-block items, placement preview, grass/tint/mipmap fixes, remembered settings, headset-off pause, offline badge, LAN discovery. Verified on a Quest 3 (2026-10-14) |
| 0.4.0: fast gliding, chat and commands, block entities | Verified on a Quest 3 (2026-10-15); fixes from that session in 0.8.1 (about 60 fps at 256 wide, 72 at 128) |
| 0.5.0 / 0.5.1: hands, living world, operator and power tools, faster meshing | Verified on a Quest 3 (2026-10-15): items, mobs, night and weather, horse gear and leads, Marks, follow, chest and sign editing, wand fill and undo. Brushes and clone not tried yet |
| 0.6.0 / 0.7.0: sound, motion | Verified on a Quest 3 (2026-10-15): sound, opt-in asset delivery (41.5 MB pack.zip in 6 s, 67 MB sounds.zip in 8 s, scene reload clean), flowing fluids, particles, lids, bells, explosions |
| 1.1.0: server starter, pairing, automatic game files | Tested on a test server: `tools/pair_check.py` (with and without required pairing), and the app's own code paired, then downloaded and checked all three files from a server made by `start.sh`. Not yet checked on a headset; `start.ps1` not yet run on Windows |
| Bedrock players (Geyser + Floodgate) | Verified with Minecraft on a phone (2026-10-15): joins through Geyser, appears on the table with its skin, chats both ways with the headset, and sees the giant as a large Mannequin wearing the headset user's skin (Bedrock cannot show the floating head and hands) |

## Versions

The repo's `VERSION` file versions the plugin, the Quest app and the protocol together. From 1.0.0 versions are semantic: a plugin and an app with the same major version work together, new optional fields and messages come in minor versions, and a breaking change needs a new major version. Before 1.0 the project used 0.X versions with no backwards compatibility (plugin, app and tools of the same 0.X went together). What each version added is in the [PROTOCOL.md changelog](PROTOCOL.md) and on the [releases page](https://github.com/NickStassen/tabletop-craft-ar/releases); the Status table above lists what each feature set was verified on.

## Quick start (server)

The easy way is the server starter from the [latest release](https://github.com/NickStassen/tabletop-craft-ar/releases/latest) (`tabletop-server-starter-<version>.zip`, 1.1.0 and later): [QUEST_SETUP Store install](QUEST_SETUP.md#the-server-starter). By hand:

Requires JDK 25 (Paper 26.x needs Java 25; the Gradle 9.8.0 wrapper runs on it).

```bash
cd server-plugin && ./gradlew build          # -> build/libs/tabletop-craft-ar-plugin-<VERSION>.jar
```

Copy the jar into a Paper **26.3** server's `plugins/`, start the server once, then edit `plugins/TabletopCraftAR/config.yml` (region origin and size, port, optional token). A new 26.x server has `white-list=true` in `server.properties`, so Java players are rejected until you `whitelist add <name>` or set `white-list=false`. The log lists the addresses the headset can use (`Headset URL: ws://<lan-ip>:8765/tabletop`), and the headset can also find the server by itself (block menu > Info > Server > Find servers; UDP 8765). In game or in the console:

```
/tabletop status
/tabletop setorigin [x y z]        # region min corner (your position if omitted)
/tabletop setsize <dx> <dy> <dz>   # within config.yml max_region (default 352 x 384 x 352)
/tabletop avatar status|spawn|remove|blocksize <m>
/tabletop avatar placement floating|ground|exact   # head + hands at the headset/controllers (default), or a Mannequin
/tabletop avatar skin <java name>         # giant's skin (also tabletop.json skin_player / config avatar.skin-player)
/tabletop tell <message>          # any player: whisper to the headset (chat panel)
/tabletop unfreeze-all            # wake every mob the headset froze
/tabletop reload
```

Fake headset: `pip install -r tools/requirements.txt && python tools/fake_quest.py --url ws://SERVER:8765/tabletop --avatar -v`.

The headset side (building the APK, installing it, textures, controls) is in [QUEST_SETUP.md](QUEST_SETUP.md).

## Does it work with Bedrock?

Yes, for the players: add [GeyserMC](https://geysermc.org) and Floodgate to the Paper server, and Bedrock players on phones, consoles or Windows join it alongside Java players. They show up on the table, their chat reaches the headset, and they see the giant. Geyser can't show the floating head and hands, so for Bedrock players the plugin shows a giant Mannequin with its eyes where the head is. The server is still a Java server (Java game rules). To play a Bedrock world, convert it with [Chunker](https://chunker.app). The headset still loads its textures from the Java game files, which come with Minecraft for Windows (it has included both editions since 2022). A Bedrock Dedicated Server can't host the table. Setup: [QUEST_SETUP Part Q](QUEST_SETUP.md#part-q-bedrock-players-geyser-and-floodgate).

## More recordings

Recorded on the headset. The player name labels and screens in these recordings are blurred.

Building from the headset: aim with the right controller, and the triggers place and break blocks. The selected block (stone) floats above the controller, and a laser shows where it will go.

![Building on the miniature from the headset: a stone hut on the water, with the selected stone block floating above the controller](media/tabletop-building.gif)

Gliding into the terrain: a lush cave with glow berries and a lava fall. It's lit with Minecraft-style smooth lighting and biome colours, and the textures come from the user's own client jar.

![Inside the miniature: a lush cave with glow berries, moss and a lava fall](media/tabletop-cave.gif)

| | | |
|---|---|---|
| ![A stone hut built on the water from the headset](media/tabletop-building.jpg) | ![A lush cave and lava fall inside the miniature](media/tabletop-cave.jpg) | ![The forest world floating in a room, seen through the Quest 3](media/tabletop-forest-floating.jpg) |
| ![A Java player's avatar standing in a hut on the miniature, seen from the headset](media/tabletop-avatar-in-house.jpg) | ![The headset user as a giant head in the sky, seen from the Java client](media/tabletop-giant-java.jpg) | |

### Both sides of the same world

Picking a block from the block menu on the headset (the menu floats by the left hand, and the ray selects the block), then a Java player placing doors in the hut, with the changes showing up live on the miniature:

![Opening the block menu and picking an oak door on the headset, then a Java player placing doors in a hut that updates live on the miniature](media/tabletop-build-select.gif)

A Java player on the miniature, seen from the headset: the player's figure stands on the mountain next to a real terrarium, and a cat has come to look at the world.

![The miniature in a room with a cat sitting on a terrarium next to it, and the Java player's figure on the mountain](media/tabletop-cat-terrarium.gif)

The other direction: the headset user appears to Java players as a giant head that follows the headset, here floating outside and looking in through a window of the hut.

![The headset user as a giant head floating in the sky and peering in through a window, seen from the Java client](media/tabletop-giant-java.gif)

## Legal

**NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.**

Follow the [Minecraft EULA](https://www.minecraft.net/eula) and [usage guidelines](https://www.minecraft.net/usage-guidelines). Running a server means accepting the EULA yourself (`eula.txt`); this repo never commits `eula.txt`, the server jar, or game assets. The Quest app loads textures only from a resource pack or client jar the user already owns.

MIT licensed, see [LICENSE](../LICENSE).
