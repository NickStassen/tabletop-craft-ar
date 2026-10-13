# tabletop-craft-ar

A live block world from a **Minecraft: Java Edition** server, shown as a miniature on a real table in **Meta Quest 3** passthrough AR. Java players walk around inside it in real time, and they see the headset user as a giant whose head and hands follow the headset and controllers.

> Not an official Minecraft product. Not approved by or associated with Mojang or Microsoft. This project ships no Mojang assets: textures come from a resource pack you supply.

Related: the idea write-up [VR-7 in NickStassen/ideas](https://github.com/NickStassen/ideas/blob/master/vr/07-minecraft-ar-tabletop.md) and the sister project [ironman-quest3-hud](https://github.com/NickStassen/ironman-quest3-hud), which has the Unity 6000.0.84f1 + Meta XR setup this project reuses.

## How it works

```
Java players (LAN) ──game protocol──▶ Paper 1.21.11 + tabletop plugin ◀──JSON over WebSocket (ws://host:8765/tabletop)──▶ Quest 3 app
                                        │ chunk_snapshot / block_update / player_positions ──────────────────────▶ │ greedy-meshed miniature on the table
                                        │ ◀────────────────────────────────────────── pose (head + hands, 30 Hz)    │
                                        └ giant avatar: Mannequin (SCALE attribute) + 2 ItemDisplay hands
```

- **server-plugin/**: Paper plugin (Java 21, Gradle Kotlin DSL, Java-WebSocket 1.6.0 shaded). Streams a configurable box (default 32x64x32) and drives the giant.
- **quest-app/**: Unity 6000.0.84f1 project skeleton + C# scripts (link client, protocol, greedy mesher, resource-pack atlas, MRUK table placement, pose sender, player markers). Not yet opened in a real Editor. See [docs/QUEST_SETUP.md](docs/QUEST_SETUP.md).
- **tools/**: Python fake-Quest client, protocol fixtures, the mineflayer second-player bot, and `e2e.sh`, which runs the whole server-side stack headless.
- **docs/**: [ARCHITECTURE](docs/ARCHITECTURE.md), [PROTOCOL](docs/PROTOCOL.md) (normative wire format, v1), [PLAN](docs/PLAN.md), [RESEARCH](docs/RESEARCH.md), [TESTING](docs/TESTING.md), [QUEST_SETUP](docs/QUEST_SETUP.md).

## Status

| Part | State |
|---|---|
| Plugin: snapshot, block updates, players, subscribe, errors, timeouts | Tested end to end on a headless Paper 1.21.11 server (see [TESTING](docs/TESTING.md)) |
| Giant avatar (Mannequin scale 16 + hands) following fake poses | Tested; a mineflayer player receives the entity spawns and movement |
| Quest C# protocol/mesher code | Compiled and unit-tested with .NET 8 outside Unity, plus a live session against the plugin |
| Quest app in Unity / on a headset | **Not tested yet.** Expect compile fixes on first open |

## Quick start (server)

Requires JDK 21.

```bash
cd server-plugin && ./gradlew build          # -> build/libs/tabletop-craft-ar-plugin-0.1.0.jar
```

Copy the jar into a Paper **1.21.11** server's `plugins/`, start the server once, then edit `plugins/TabletopCraftAR/config.yml` (region origin and size, port, optional token). In game or in the console:

```
/tabletop status
/tabletop setorigin [x y z]        # region min corner (your position if omitted)
/tabletop setsize <dx> <dy> <dz>   # max 64 x 384 x 64
/tabletop avatar status|spawn|remove|blocksize <m>
/tabletop reload
```

Fake headset: `pip install -r tools/requirements.txt && python tools/fake_quest.py --url ws://SERVER:8765/tabletop --avatar -v`.

## Legal

Follow the [Minecraft EULA](https://www.minecraft.net/eula) and [usage guidelines](https://www.minecraft.net/usage-guidelines). Running a server means accepting the EULA yourself (`eula.txt`); this repo never commits `eula.txt`, the server jar, or game assets. The Quest app loads textures only from a resource pack or client jar the user already owns.
