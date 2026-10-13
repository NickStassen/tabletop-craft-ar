# RESEARCH.md — findings, sources, confidence, risks

Research date: **2026-10-13**. Confidence: **H** = confirmed in official docs or source; **M** = confirmed in secondary sources, or inferred from official docs; **L** = plausible and unverified, so test it.

## 1. Server side (Paper)

### 1.1 Display entities (Block/Item/TextDisplay)
- `Display#setTransformation(Transformation)` takes a `Vector3f` scale with no documented upper limit, so any size of giant can be made from displays. Smoothing comes from `setInterpolationDelay/Duration` (transform) and `setTeleportDuration(0–59 ticks)` (position). **H.** [Paper docs: Display entities](https://docs.papermc.io/paper/dev/display-entities/), [Display javadoc](https://jd.papermc.io/paper/26.2/org/bukkit/entity/Display.html), [wiki: display entity format](https://minecraft.wiki/w/Entity_format/display_entity) (all checked 2026-10-13).
- View range is a multiplier (`viewRange × entityDistanceScaling × 64` blocks), not blocks, so set it above 1 for a giant. **M.** [Paper #11619](https://github.com/PaperMC/Paper/issues/11619).
- Displays can't show a full player skin model. They only offer a `player_head` item (head texture) or blocks and items. A full skinned body made of displays needs a resource-pack model (Phase 5). **H.**

### 1.2 Mannequin (player-model NPC, vanilla since 1.21.9)
- The `Mannequin` interface (extends `LivingEntity`) has `setProfile(ResolvableProfile)`, `setSkinParts`, `setImmovable`, and descriptions. It renders a player model with a profile skin, and `minecraft:profile` overrides allow a texture or model (`wide`/`slim`). **H.** [Mannequin javadoc 1.21.9](https://jd.papermc.io/paper/1.21.9/org/bukkit/entity/Mannequin.html), [Paper source](https://github.com/PaperMC/Paper/blob/76d2ac75/paper-api/src/main/java/org/bukkit/entity/Mannequin.java), [1.21.9 Pre-Release 1 notes](https://feedback.minecraft.net/hc/en-us/articles/39609762119053-Minecraft-Java-Edition-1-21-9-Pre-Release-1).
- Scaling: as a `LivingEntity` it inherits `Attributable`, so the `scale` attribute (added in 1.20.5, range 0.0625–16; Bukkit name `Attribute.SCALE` since 1.21.2+, earlier `GENERIC_SCALE`) should apply. **M**: the attribute range is well known, but scaling a Mannequin specifically wasn't confirmed in docs, so the build worker must verify it on a live server (Phase 3 exit criterion).
- Arms can't be posed to arbitrary angles (poses are limited to standing, crouching, swimming, fall_flying and sleeping), so hands use separate displays. **H** (pose list from the release notes).

### 1.3 NPC options compared → **pick Mannequin + ItemDisplay hands**
| option | skin | scale | deps | fragility | verdict |
|---|---|---|---|---|---|
| Mannequin (vanilla) | full player skin | scale attribute ≤16 | none | low (public API) | **v1** |
| Citizens2 | full | via the scale attribute / its own trait | big plugin, its own update cadence | medium | skip for v1 |
| Packet player NPC (PacketEvents/ProtocolLib) | full | needs the attribute packet | extra plugin, breaks across MC versions | high | skip |
| Armor stand + head | head only | can't scale above marker/small | none | low | worse than ItemDisplay |
| Display-entity rig | head texture only (or a resource-pack model) | unlimited | none | low | fallback / Phase 5 |
Confidence in the recommendation: **M-H**.

### 1.4 Chunk export
- Minecraft stores sections of 16³ blocks as a block-state palette plus packed indices; the ordering inside a section is yzx (x fastest). The protocol uses the same ordering, with RLE instead of bit packing because JSON doesn't benefit from bit packing. **H.** [wiki: Chunk format](https://minecraft.wiki/w/Chunk_format).
- `World#getChunkAt(...).getChunkSnapshot()` must be called on the main thread. The resulting `ChunkSnapshot` is a thread-safe immutable copy, so read it async with `getBlockData(x,y,z)`. **H** (Bukkit javadoc for `ChunkSnapshot`: "safe to use from other threads").
- Size: 32x32 at full height (−64…319 = 384) = 393,216 blocks. That's 786 KB as raw u16 and about 2.5 MB of RLE JSON in the worst case. Typical terrain is around 1–3 k runs per 64-high band, so 5–30 KB. **M** (arithmetic plus typical-terrain assumption, so measure it in Phase 1).

### 1.5 Paper version and Java
- Paper's current release is 26.3 (build 178, 2026-10-12). **Minecraft 26.1+ and Paper 26.1+ need Java 25.** Paper 1.21.11 (build 132, 2026-05-11) is the latest line on Java 21. **H.** [Paper downloads](https://papermc.io/downloads/paper), [Paper 26.1 news](https://papermc.io/news/26-1), [Paper Java install docs](https://docs.papermc.io/misc/java-install/), [Paper 1.21.11 news](https://papermc.io/news/1-21-11), [mcjars index](https://mcjars.com/paper).
- → **This contradicts the "Java 21" brief if the goal is the latest server.** Decision: pin 1.21.11 + Java 21 for v1, which matches the brief, and note that the Java client must join with 1.21.11 (or via ViaVersion). Moving to 26.x later is cheap.
- Also from the 1.21.11 news: starting with 26.x, Paper ships unobfuscated, and version strings no longer start with `1.`. **H.**

### 1.6 WebSocket inside a plugin
- `org.java-websocket:Java-WebSocket` **1.6.0** (2024-12-15) is the latest on Maven Central. It's MIT-licensed, has only slf4j-api as a dependency, and includes `WebSocketServer`. Its `send()` is thread-safe and callbacks run on its own threads. **H** for the version ([Maven Central](https://repo.maven.apache.org/maven2/org/java-websocket/Java-WebSocket/), [releases](https://github.com/TooTallNate/Java-WebSocket/releases)). **M** for the threading notes (README/wiki).
- Shade it with the Gradle Shadow plugin and `relocate("org.java_websocket", "<pkg>.libs.websocket")` so it can't clash with other plugins. Paper already provides slf4j, so exclude it or keep it compileOnly. Alternative: Paper's `paper-plugin.yml` loader with `MavenLibraryResolver` (no shading, but there's no relocation either). **M-H.**
- Javalin (Jetty) is far heavier, at about 5–10 MB shaded, and brings Jetty threads. Netty is on the server classpath but isn't API, so it's version-fragile. **Pick Java-WebSocket.** **M.**
- Bukkit rule: world and entity API only on the main thread. Use the `BukkitScheduler` (`runTask`) or a polled queue. **H** (Paper docs, scheduler).

## 2. Quest side (Unity)
- **Meta XR Core SDK**: the latest is 207.0 (2026-09-22), minimum Unity 6000.0.66f2, so 6000.0.84f1 is OK. **MRUK**: the latest is 205.0 (2026-07-22), and URP became the default for its materials (shaders split URP/BiRP). **H.** [Core SDK](https://developers.meta.com/vr/downloads/package/meta-xr-core-sdk/), [MRUK UPM](https://developers.meta.com/horizon/downloads/package/meta-xr-mr-utility-kit-upm/).
- The HUD repo pins Core + MRUK **85.0.0** on 6000.0.84f1 with BiRP. Decision: reuse 85.0.0 for a known-good setup, and bump to 205/207 only if needed (note the BiRP shader split if bumping). **M.**
- MRUK table detection: `MRUKAnchor.Label == SceneLabels.TABLE`. Tables can have both a `PlaneRect` and `VolumeBounds`. `MRUK.Instance.GetCurrentRoom()`, `SceneLoadedEvent`. It needs the user to have run Space Setup and the `com.oculus.permission.USE_SCENE` permission. **H.** [MRUK manage scene data (2026-10-08)](https://developers.meta.com/vr/documentation/unity/unity-mr-utility-kit-manage-scene-data/), [MRUKAnchor ref](https://developers.meta.com/horizon/reference/mruk/v83/class_meta_x_r_m_r_utility_kit_m_r_u_k_anchor/), [migration guide](https://developers.meta.com/horizon/documentation/unity/unity-scene-migrate-mruk/).
- Greedy meshing: the standard approach is the 0fps "Meshing in a Minecraft game" algorithm. Merged quads need repeating UVs, hence a `Texture2DArray` rather than a 2D atlas. **H** for the technique ([0fps 2012](https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/)), **M** for the Quest budget.
- Resource-pack loading: the pack format is `assets/minecraft/{blockstates,models/block,textures/block}`. Parsing models (`parent`, `#texture` variables) is well documented. **H.** [wiki: Resource pack](https://minecraft.wiki/w/Resource_pack), [wiki: Model](https://minecraft.wiki/w/Model).
- C# WebSocket: `System.Net.WebSockets.ClientWebSocket` works under IL2CPP on Android in Unity 2021+, and `endel/NativeWebSocket` is the common fallback (it needs a `DispatchMessageQueue()` pump on non-WebGL). **M** (community reports; not re-verified today, so compile-check it in Phase 4).

## 3. Legal (EULA / Usage Guidelines)
- Original plugins ("mods") may be distributed if they don't contain a substantial part of Mojang's code or content and don't distribute a modded version of the game. **H.** [Usage Guidelines](https://www.minecraft.net/en-us/usage-guidelines), [EULA](https://www.minecraft.net/en-us/eula).
- Textures and skins belong to Mojang, so **ship no vanilla textures.** The user supplies a resource pack or their own `client.jar`; the repo ships only a flat color table (our own data) and code. **H.**
- Name: the brand name must not be used in a way that implies officialness. Guidance from server hosts (Minehut MUG FAQ) says it shouldn't be the first word. `tabletop-craft-ar` avoids it entirely; describe the project as "for Minecraft: Java Edition" and add a "not affiliated with Mojang/Microsoft" line in the README. **H/M.** [Minehut MUG FAQ](https://minehut.wiki.gg/wiki/MUG_FAQ).
- Servers: allowed, but paid access has to be equal for everyone. Keep the project non-commercial. Real-player mode must use proper Microsoft auth, with no bypass. **H.**
- The headless test server needs `eula=true` in `eula.txt`, which accepts Mojang's EULA on the operator's behalf. **The build worker should note this.** Running it in offline mode for the mineflayer bot is fine on an isolated box/LAN. **M.**

## 4. Reused lessons (ironman-quest3-hud)
- Unity 6000.0.84f1 (66f2 won't launch on Ubuntu 26.04), Meta XR Core + MRUK 85.0.0, BiRP, the Project Setup Tool "Fix All", and building the scene from code via an editor menu item instead of committing hand-made scenes.
- Networking: LAN unicast, low-latency Wi-Fi lock, AP isolation and PMF off, firewall port open. Quest Wi-Fi can be bursty. A literature figure of about 63–106 ms PC↔Quest TCP end to end.
- Debug: `adb logcat -s Unity`, plus an on-headset status line.

## 5. Open risks
1. **The Mannequin scale attribute is unverified.** If it doesn't apply, fall back to a giant ItemDisplay head plus hands (Phase 3 tests this). *M impact.*
2. **The scale cap of 16 means about a 29-block giant** vs about 175 blocks for "true" scale at 1 cm/block. The avatar is visibly smaller than the real head-height mapping implies; consider a larger `block_size_m` or a display rig. *Design.*
3. **Version skew:** the server is pinned to 1.21.11 while the current game is 26.3. Java clients must pick 1.21.11 in the launcher. *Low.*
4. **MRUK needs Space Setup**, and a table may not be labeled. Manual placement is the fallback. *Low.*
5. **Pinned MRUK 85 vs current 205:** newer docs may describe APIs missing in 85. *Low-M.*
6. **ClientWebSocket on IL2CPP** stripping/TLS quirks. Plaintext `ws://` on LAN avoids the TLS issue; keep NativeWebSocket ready. *M.*
7. **Block-change coverage:** events miss some changes (WorldEdit, some physics). The 1 s diff safety net covers them. *Low.*
8. **Quest perf** for meshing and atlas building at bigger regions is unmeasured. Do the meshing off the main thread (`Task` + `Mesh.MeshData`). *M.*
9. **Skin lookup** by name needs internet from the server (Mojang API) and is rate-limited. Cache it, and allow `skin_url`. *Low.*
10. **Privacy:** the repo goes public, so keep usernames, IPs and hostnames in config examples generic.
