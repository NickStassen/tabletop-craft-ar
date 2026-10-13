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
