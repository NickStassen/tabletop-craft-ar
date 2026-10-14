# ARCHITECTURE.md — tabletop-craft-ar

A live Java-edition block world shown as a miniature on a real table in Quest 3 passthrough AR. Java players see the headset user as a giant inside the world.

Related: idea file [VR-7](https://github.com/NickStassen/ideas/blob/master/vr/07-minecraft-ar-tabletop.md) and sister project [ironman-quest3-hud](https://github.com/NickStassen/ironman-quest3-hud), which has the Unity/Meta XR setup this project reuses. The wire format is in [PROTOCOL.md](PROTOCOL.md), the phases in [PLAN.md](PLAN.md), and the sources in [RESEARCH.md](RESEARCH.md).

## 1. Components

```
 Java players (LAN) ──MC protocol──▶ ┌────────────────────────────┐
                                    │ Paper server 26.3          │
                                    │  └ tabletop plugin (Java25)│
                                    │     ├ RegionExporter        │
                                    │     ├ BlockChangeTracker    │
                                    │     ├ PlayerBroadcaster     │
                                    │     ├ GiantAvatar           │
                                    │     └ LinkServer (WS :8765) │
                                    └──────────────┬─────────────┘
                                         JSON / WebSocket (PROTOCOL v1)
                                                   │
                       ┌───────────────────────────┴─────────────┐
                       │ Quest 3 Unity app        │ tools/fake-quest│
                       │  passthrough + MRUK table│ (Python, CI/box) │
                       │  greedy mesher + atlas   │                  │
                       └──────────────────────────┴─────────────────┘
```

The Quest has no Minecraft account and never speaks the game protocol in v1.

## 2. Version pins (v1)

| thing | pin | why |
|---|---|---|
| Server | **Paper 26.3** (build 181, stable) | Current release; needs **Java 25** (RESEARCH §1.5). It has Mannequin (1.21.9+) and display entities. v1 was first built and tested on Paper 1.21.11, the last Java 21 line. |
| Plugin build | Gradle 9.8.0 wrapper (Kotlin DSL), Java toolchain 25, `io.papermc.paper:paper-api:26.3.build.181-stable` (compileOnly), Shadow 9.6.1 | Gradle 8.14 can't run on Java 25, so a single JDK 25 builds and runs everything. |
| WebSocket server | `org.java-websocket:Java-WebSocket:1.6.0`, shaded and relocated to `<pkg>.libs.websocket` | |
| JSON | Gson, already on Paper's classpath (compileOnly). Don't shade it. | |
| Second player bot | mineflayer 4.39.0, `version: "26.1"` | mineflayer supports game versions up to 26.1, so the e2e test server adds ViaVersion + ViaBackwards 5.12.0 (test only). The bot sends `tick_end` each tick, which 26.x requires before the next movement packet. |
| Unity | 6000.0.84f1, same as the HUD repo | proven on the dev PC |
| Meta XR | Core SDK + MRUK at the **same version as the HUD repo (85.0.0)** first; bump only if a needed API is missing | known-good setup |
| C# WebSocket | `System.Net.WebSockets.ClientWebSocket` (built into .NET under IL2CPP) behind an `ILinkTransport` interface. NativeWebSocket is the fallback. | no extra package |

Upgrade path: the move from Paper 1.21.11 to 26.3 was the API bump, a Java 25 toolchain and Gradle 9.8.0 / Shadow 9.6.1. No plugin source or protocol changes were needed.

## 3. Server plugin

### 3.1 Threading rules (hard)
- The Java-WebSocket server runs its own selector/worker threads. **No Bukkit API calls on those threads.** Every inbound message is parsed there (pure JSON → record) and then handed over:
  - poses go into an `AtomicReference<Pose>` latest-value slot that the main-thread tick task reads (so stale poses are dropped for free);
  - `subscribe`, `avatar_config` and similar go into a `ConcurrentLinkedQueue<Command>` that the main thread drains each tick.
- Outbound: main-thread code builds a payload object and calls `conn.send(String)` (Java-WebSocket's `send` is thread-safe). JSON serialization of large snapshots happens **off** the main thread.
- One repeating task, `runTaskTimer(plugin, 1, 1)`, drives the tick work: drain commands, apply the latest pose to the avatar, flush the batched block changes, and send player positions every 2 ticks.

### 3.2 RegionExporter (chunk_snapshot)
1. On the main thread, for each chunk that overlaps the region, take `chunk.getChunkSnapshot(false,false,false)`. Paper copies the section palettes here; that's cheap (about 2 chunks × 4 for 32x32). If a chunk isn't loaded, load it with `world.getChunkAtAsync(x,z)` first.
2. Async (`runTaskAsynchronously`): walk the region in yzx order with `snapshot.getBlockData(lx, y, lz)`, build the palette and RLE (PROTOCOL §6), serialize the JSON and send it. `ChunkSnapshot` is immutable and documented as safe to use off-thread.
3. Snapshot ids are per connection. Block changes that arrive while a snapshot is being built are buffered and replayed against the new `snapshot_id`.

### 3.3 BlockChangeTracker (block_update)
- Event listeners at MONITOR priority with ignoreCancelled: `BlockPlaceEvent`, `BlockBreakEvent`, `BlockPhysicsEvent` (sampled), `BlockFromToEvent`, `BlockBurnEvent`, `BlockFadeEvent`, `BlockFormEvent`, `BlockSpreadEvent`, `LeavesDecayEvent`, `EntityExplodeEvent`, `BlockExplodeEvent`, `BlockPistonExtendEvent`/`RetractEvent`, `EntityChangeBlockEvent`. Events fire **before** the change, so record only the *position* and read the state at end of tick (in the next tick task, 1 tick later).
- Safety net: every 20 ticks, re-snapshot the region's chunks async and diff them against the last sent state. That catches `/fill`, `/setblock` and WorldEdit. It's cheap at this size (65k comparisons).
- Coalesce by position. Emit one `block_update` per tick; past 4096 changes, send a new snapshot instead.

### 3.4 PlayerBroadcaster
Every 2 ticks: for online players in the region's world within the region's XZ bounds plus 16, send pos/yaw/pitch/sneaking. The skin URL comes from `player.getPlayerProfile().getTextures().getSkin()` and goes out once per UUID.

### 3.5 GiantAvatar — v1 design (decision)
**Use a `Mannequin` entity (Paper ≥1.21.9) for the body and head, scaled with the `SCALE` attribute, plus two `ItemDisplay` entities for the hands.**

- Body: `world.spawn(loc, Mannequin.class, m -> { m.setProfile(ResolvableProfile...); m.setImmovable(true); m.setAI(false); m.setGravity(false); m.setInvulnerable(true); m.setPersistent(false); m.getAttribute(Attribute.SCALE).setBaseValue(s); })`. It renders with the real player model and skin, and the server does the networking to all Java clients, so no NMS or packets are needed.
- Head/body orientation: every tick `m.setRotation(yaw, pitch)` from the head quaternion, plus `teleport` for position. Body yaw follows head yaw with a lag.
- Hands: two `ItemDisplay`s (e.g. a `player_head` item with the same profile, or a plain block) with `setTeleportDuration(2)` and `setInterpolationDuration(2)` for smoothing, `Transformation` scale ≈ `s/2`, and billboard FIXED, positioned at the hand poses mapped into the world. The Mannequin's own arms can't be posed freely; that's accepted for v1 (PLAN Phase 5 explores an all-display "rig").
- Scale math: the "true" giant scale is `s_true = (user_height_m / block_size_m) / 1.8` (a scale-1 player is 1.8 blocks tall). At 1 cm/block a 1.75 m person would be about 175 blocks tall, which is **far above** the attribute cap (16 → about 29 blocks). v1 clamps the scale to `max_scale`, and positions are still mapped exactly, so the giant's head sits where the user's head is above the table. The body is then just shorter than "true" (it floats or its feet sit on the terrain). `avatar_state.note` reports the clamp.
- Lifecycle: spawn on `avatar_config{enabled:true}`, remove on disable, disconnect (+10 s) or plugin disable. Mark the entities non-persistent so a crash never leaves giants behind.
- Fallback if Mannequin misbehaves: a giant `ItemDisplay` player head (`Transformation` scale works for any size) plus the hand displays. No Citizens or ProtocolLib in v1 (RESEARCH §1.3).

### 3.6 LinkServer
`WebSocketServer` subclass bound to `config.bind` (default `0.0.0.0:8765`), `setReuseAddr(true)`. It's started in `onEnable` on its own thread and stopped in `onDisable` with `stop(1000)` (close code 4005 to clients). One `Session` object per connection: state machine `AWAIT_HELLO → READY → CLOSED`.

## 4. Quest app

- Base: copy the HUD repo's Unity 6000.0.84f1 + Meta XR Core/MRUK setup and its Project Setup Tool flow. Built-in render pipeline. OpenXR or the Oculus XR plugin, as the HUD repo did. Android IL2CPP ARM64.
- Scene: an `OVRCameraRig` with passthrough enabled (`OVRPassthroughLayer`, underlay), the MRUK prefab with **Load Scene on Startup**, and the `USE_SCENE` permission.
- Placement: after `MRUK.Instance.SceneLoadedEvent`, find the anchors labeled `TABLE`. Pick the nearest to the head and use `PlaneRect` (or the top of `VolumeBounds`). `TabletopAnchor` goes to the plane's center, rotated so its local +Y is the table normal. Fallbacks: no scene or no table → the user places the world with a controller raycast against `EnvironmentRaycastManager` or the floor at 0.75 m. A grip on both controllers moves, rotates or scales it.
- Rendering:
  - **Mesher:** a greedy mesher per 16x16x16 sub-volume (so `block_update` only rebuilds one sub-mesh). It culls faces against neighbors, and faces only merge when they have the same texture, tint and texture rotation. Greedy UVs are in block units from the section-local corner, in vanilla's per-face orientation (grass fringe up on every side), so merged quads tile exactly like single blocks. Only full cubes and fluids go through the greedy path. Everything else is drawn from its block model (below). Without a pack there are no models, and non-cube states fall back to placeholders from a name-based rules table (a cross quad or a half block). 32x64x32 makes at most 8 sub-meshes.
  - **Block models** (`World/BlockModelBaker.cs`, `World/BlockModels.cs`, `World/ResourcePackAtlas.Models.cs`): with a pack, each palette state's model is baked on the main thread the first time the renderer prewarms it (`Atlas.Face`). The baker handles blockstate `variants` (property match, the first entry of a weighted list, `x`/`y` rotation, `uvlock`) and `multipart` (every part whose `when` matches, including `OR`/`AND` and `a|b`), the model `parent` chain with `#texture` variables (26.x `{"sprite", "force_translucent"}` objects too), and elements with `from`/`to`, per-face `uv` (default from the bounds), `rotation`, `cullface` and `tintindex`. Element rotation can be axis/angle/origin/`rescale` or 26.x `x`/`y`/`z` angles. The result is a list of quads in block units with UVs inside the texture layer (0..1, so no shader change), plus a 16x16 coverage mask per side. Classification (`BlockRules.SetShape`) then follows the model, not the name. Six whole boundary faces with cullfaces (or an opaque shell with hidden inner faces, like powder snow) make a full cube. It stays greedy, with its x/y-rotated textures and a `FaceKey.UvMap` for rotated faces such as sideways logs. Cut-out or translucent cubes (copper grates, frosted ice) become `TransparentCube` so they don't hide neighbours. Anything else becomes `BlockShape.Model`, and `BuildSpecials` emits its quads on the worker threads (`BlockModelMesher`). A face with a cullface is dropped when the neighbour is an opaque cube, when the neighbour's model covers that side (snow layers, slabs and stairs side by side), or, for translucent faces, when the neighbour is the same block (joined glass panes). Opaque and cut-out faces go to the alpha-tested submesh; faces with partially transparent textures or `force_translucent` go to the blended one. Inside-out helper elements (`from > to`) are kept only for see-through textures (spawner cages). Block entities have no model geometry in vanilla, so chests, banners, heads, shulker boxes, decorated pots, conduits, end portals and copper golem statues get simple boxes from their particle texture (banners use the matching wool, heads a flat colour), and bells get a body under their frame. Redstone dust and stems take their state colour, not the biome tint. Workers only read baked data. A worker that meets a palette entry the main thread hasn't prewarmed yet (a race with `block_update`) gets an uncached flat colour, and that section is meshed again after the next prewarm.
  - **Atlas:** runtime-built from a **user-supplied** resource-pack zip (or the user's own `client.jar`) copied to the headset's app storage (`/sdcard/Android/data/<pkg>/files/pack.zip`). Read `assets/minecraft/blockstates/*.json` → `models/block/*.json` (resolve the `parent` chains and `textures` variables) → PNGs, then pack them with `Texture2D.PackTextures` and point clamp filtering. Greedy-merged faces need tiling, so use a `Texture2DArray` (one 16x16 layer per texture) with UVs in block units and wrap/repeat instead of a 2D atlas. That avoids atlas bleeding on merged quads. Biome tint (grass, leaves, water) is a fixed per-texture color in v1. Without a pack, the app colors faces from a built-in flat color table, so it works with zero Mojang assets.
- Players: capsule or box markers with name labels, lerped at 10 Hz.
- Network: `LinkClient` on a background task with `ClientWebSocket`, an inbound `ConcurrentQueue<string>` drained on the main thread, a 30 Hz pose sender from `Update` (head = center eye, hands = controller anchors or hand-tracking wrists), and auto-reconnect with backoff. Hold the Wi-Fi low-latency lock as in the HUD repo.

## 5. Networking/latency notes (from ironman-quest3-hud)
- LAN unicast only. Router AP isolation must be off, and the plugin port (8765/tcp) has to be open on the server PC's firewall.
- Measured in the literature: about 63–106 ms end to end PC↔Quest over TCP. That's fine for a giant whose motion is interpolated over 2 ticks (100 ms).
- Quest Wi-Fi can deliver traffic in bursts; the server's latest-pose-per-tick slot absorbs that.
- The protocol's `ping`/`pong` gives RTT; the Quest logs it to logcat (`adb logcat -s Unity`).

## 6. Security (LAN v1, public later)
Shared token in `hello`, bind address configurable, per-connection rate limit on poses. Public-server phase: TLS via a reverse proxy (`wss://`), per-user tokens bound to a Java account via an in-game `/tabletop link` command, and the region subscribe restricted to permitted areas.
