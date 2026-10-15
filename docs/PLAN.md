# PLAN.md — phases and exit criteria

See [ARCHITECTURE.md](ARCHITECTURE.md) for the design and [PROTOCOL.md](PROTOCOL.md) for the wire format. Each phase is done only when **all** of its exit criteria pass.

Phases 0-3 were built and checked on Paper 1.21.11 / Java 21. The server pin has since moved to Paper 26.3 / Java 25 (ARCHITECTURE §2); the versions in those phases are kept as written.

## Phase 0 — Repo and scaffolding (box)
- A private repo whose default branch is `master`, with `server-plugin/` (Gradle, Java 21, Paper 1.21.11 API, Shadow + relocation), `quest-app/` (scripts + setup doc), `tools/` (fake-quest client), `docs/`, and a CLAUDE.md that cross-links ironman-quest3-hud and VR-7.
- **Exit:** `./gradlew build` produces a shaded jar whose Java-WebSocket classes live under the relocated package (`unzip -l` shows no `org/java_websocket/`). CI runs the build and the unit tests.

## Phase 1 — Server link + export (box, headless)
- LinkServer, hello/welcome, subscribe, chunk_snapshot, ping/pong, error.
- Unit tests: RLE encode and decode round trip, the yzx index math, palette `palette_add` semantics, and the golden JSON fixtures.
- **Exit:** a headless Paper 1.21.11 server loads the plugin. The fake client connects and receives a 32x64x32 snapshot whose decoded block at 3+ known coordinates matches `/setblock`-placed markers, and the RLE total equals the volume. The snapshot builds in under 50 ms of main-thread time (timings log).

## Phase 2 — Live updates (box)
- block_update (events + 1 s diff safety net), player_positions.
- **Exit:** a mineflayer bot (offline-mode server on the box) walks into the region. The fake client sees its positions at about 10 Hz to within 0.1 block. The bot or `/setblock` breaks and places blocks, and each change shows up in a `block_update` within 2 ticks; `/fill` of 1000 blocks shows up within 1 s.

## Phase 3 — Giant avatar (box)
- avatar_config/avatar_state, Mannequin + hand ItemDisplays, scale clamp.
- **Exit:** the fake client sends a scripted 30 Hz pose loop (circle + nodding). The mineflayer bot's `bot.entities` contains a `mannequin` entity whose position follows the script to within 1 block and whose yaw changes. The hand displays move. The scale attribute reads back the configured value. Disconnecting removes all avatar entities within 11 s. No errors in the server log over 5 min of poses.

## Phase 4 — Quest app MVP (dev PC + headset)
- Unity project from setup steps, passthrough, MRUK table placement, LinkClient, greedy mesher with the flat-color fallback, then the resource-pack atlas, player markers, and the pose sender.
- **Exit:** on the Quest 3, the region appears on the real table within 10 s of launch. A Java player on the LAN moving and breaking blocks is reflected on the table, the Java player sees the giant following the headset user's head and hands, Quest framerate holds at 72 Hz or above with a 32x64x32 region, and the RTT logged by ping is under 150 ms p95.

## Phase 5 — Polish and scale
- Larger regions (64x64), pinch-to-pan / `follow`, biome tint, non-cube block models, an optional binary chunk encoding (protocol v2) if snapshot size or parse time is a measured problem, an all-display-entity articulated avatar (arms follow the hands).
- **Exit:** a 64x128x64 region at 72 Hz, a snapshot parse under 100 ms on the Quest, and documented measurements.

## Phase 6 — Tabletop interaction
- C→S `block_edit` (protocol v2): pinch to break or place from the tabletop; the server validates the permission and range, and it happens as the avatar entity.
- **Exit:** edits from the Quest appear for Java players and round-trip back to the table, and permission checks have tests.

## Phase 7 — Real-player mode (optional)
- Instead of the plugin avatar, a bridge (mineflayer, or MCProtocolLib on the PC) logs in as a real account with proper Microsoft auth, so it works on servers without the plugin. Evaluate effort vs Phase 3.
- **Exit:** a design doc plus a prototype joining a vanilla LAN world; no auth bypass.

## Phase 8 — Public server
- `wss://` behind a reverse proxy, per-user tokens via `/tabletop link`, rate limits, region permissions, and a usage-guidelines review (no Mojang assets shipped, the name doesn't start with or imply the game's brand, non-commercial).
- **Exit:** a security checklist passes, and the repo flips to public after a personal-details scrub.

## Status after the first headset sessions (2026-10-13)
- **Done:**
  - Phases 0–4 run on a Quest 3 against Paper 26.3.
  - From Phase 5: block models from the pack, biome colours, smooth lighting with AO, animated textures, waterlogging and fluids, gliding across the world with sectioned/delta streaming (v1.2), and areas up to 128×96×128 recommended.
  - Phase 6: `block_edit` from the headset (break, place, pick). It was done as an additive v1 message (`edit_v1`), not a v2 bump, and the plugin applies edits itself rather than as the avatar entity.
- **Also done (not in the original phases):**
  - The floating giant: a head and hands at the exact headset poses.
  - Skinned and animated player figures.
  - The eye-in-fluid effects: underwater fog and tint, the surface seen from below, lava and powder snow.
- **In progress:** entities (vehicles, mobs, dropped items, held items) and player actions (arm swing, digging cracks, break particles, hurt flash).
- **Built, waiting for headset measurements (2026-10-14):** area width presets 64–256 without reconnecting (resize deltas, re-keyed meshes, footprint kept) and mesh batching (roadmap item 7).

## Roadmap: "playing Minecraft as a god"
Ordered by how much each adds to the feeling of a living world on the table that you can act on.

**1. World manipulation.**
- (Partly done) Two-hand grab: holding both grips drags the table and turns it with the right hand. Pulling the hands apart to zoom around their midpoint and grabbing the terrain itself aren't built yet (zoom is the right stick).
- Follow a player: the area tracks them.

**2. A living world.**
- Day/night and weather from the server: sky light scaled by time, rain and snow particles, lightning flashes.
- Clouds at cloud height above the table.
- Particles: torches, lava, splashes, explosions.
- Block entities: sign text, banner patterns, item frames, chests opening.

**3. Presence in AR.**
- Hand tracking: pinch to break or place, fist to grab.
- Depth-API occlusion, so real hands hide the terrain.
- Working MRUK table detection; the log shows OpenXR binding warnings.
- A persistent spatial anchor. (0.3.1 remembers the placement relative to the MRUK table in `prefs.json`; without a table it still starts in front of you.)
- A diorama plinth and a soft shadow on the real table.
- (Done, not yet tried on the headset) Status text off the view: the full status is the block menu's Info tab, events are short toasts, area setup has a small panel on the left hand, and a small HUD is optional (`show_hud`).

**4. Sound.** Spatial audio from the miniature: blocks, footsteps, mobs, water, rain. It needs the user's sound assets from the launcher's asset index on the headset, plus server sound events.

**5. God powers.**
- (Done, not yet tried on the headset) A block palette, hotbar and creative inventory: the server's `block_registry` (PROTOCOL.md 4.24), the block menu on the left hand with categories, search and icons from the user's pack, a 9-slot hotbar (QUEST_SETUP.md Part J).
- Interacting with doors, levers, buttons and TNT.
- Undo.
- Lightning, time and weather gestures. (Buttons done, not yet tried on the headset: the operator panel's World tab, with lightning at the pointer; QUEST_SETUP.md Part L.)
- Picking up and moving mobs or players.
- Terraform brushes.
- Spawning mobs.
- (Done, not yet tried on the headset) Chat to and from players and server commands: `chat_v1`, `command_v1`, `status_v1` (PROTOCOL.md 4.26–4.28), the operator panel on X with Chat, Console, Players, World and Server tabs, chat on the wrist and bubbles over players, the giant's speech bubble for Java players (QUEST_SETUP.md Part L). Open: dictation in.

**6. Multiple headsets.**
- One giant per session (today one giant, owned by the last headset).
- Per-session regions (today the region is shared, last writer wins).
- Co-located shared anchors.
- Edit permissions per user.

**7. Scale.**
- (Done, not yet measured on the headset) Batch section meshes for 192+ wide areas: world-aligned batches of 2×2 section columns, 100 renderers / 200 draw calls at a 256-wide window instead of 1,223 / about 2,000 (ARCHITECTURE.md "Batching"). The visible width steps through 64/96/128/192/256 with a left stick click (QUEST_SETUP.md Part J, PROTOCOL.md 4.25). Next: the headset FPS per preset; with draw calls gone, the faces (about 700k at 256 with the pack and light) are the likely limit.
- Spread the main-thread model prewarm over frames (45–53 ms on a new area).
- A low-detail map skirt around the detailed box.

**Edge cases to keep covered:**
- **Area edges:** fluids at the cut edges and the region top.
- **Players:** riding, sleeping, swimming, gliding, invisible or in spectator mode, near the region edge, or in another dimension.
- **Edits:**
  - placing into players or water
  - orientation and two-block items
  - gravity blocks
  - griefing limits and undo
- **Streaming:** into ungenerated chunks, at build-height limits and the world border, and big diffs (explosions, floods) falling back to a snapshot.
- **Pause and reconnect** when the headset is taken off.
