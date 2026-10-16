# Known issues

Things that don't work yet, or work differently from the game, in tabletop-craft-ar 1.1. Gaps against the protocol spec are listed separately in [TESTING.md](TESTING.md) under "Spec deviations"; what has been verified is in the Status table in [OVERVIEW.md](OVERVIEW.md#status).

- **Bedrock players see the giant as a whole player.** Bedrock clients can't show the item displays the floating head and hands are made of, so Bedrock players see a large Mannequin wearing the headset user's skin, turning with the headset, with no moving hands. Java players see the floating head and hands.
- **Pistons jump instead of sliding.** Pushed blocks appear in their new spot after the 2-tick move.
- **Not drawn yet:**
  - map art in item frames
  - the item in suspicious sand and gravel
  - banners don't sway
  - the enchanting-table book stays closed
  - sign text shows without styles or per-line colours
- **Sounds:**
  - Mob sound variants (moody cows, big pigs, wolf moods) use the base mob's sounds.
  - Sounds played by other server plugins aren't heard.
  - There's no music and no biome or underwater ambience.
- **Chat:** `/msg` and `/tellraw` don't reach the headset's chat. Bukkit fires no event for them.
- **The surface skirt is off by default.** It's the low-detail ground shown ahead of the detail while you glide fast (`"surface_skirt": true` in tabletop.json); it's still being checked on the headset.
- **Very wide areas are heavy.** 256 wide runs at about 60 fps standing still and about 45-50 fps while gliding; 128 wide and below hold 72 fps (Quest 3). Growing the area's margin on the first glide can cause one long frame.
- **Setup (1.1.0):**
  - The pairing card, the download toast and the reload after the game files haven't been checked on a headset yet.
  - The Windows starter (`start.bat`/`start.ps1`) hasn't been run on Windows yet.
  - Only the official launcher's folder is found automatically. For Prism, MultiMC or CurseForge, point the plugin at the folder with `/tabletop assets dir <folder>`.
  - The store invite link isn't published yet, so the app is sideloaded (Developer Mode).
- **One region per server.** It's shared by all headsets, and the last change wins. There's one giant, owned by the last headset to connect.
- **Realms and singleplayer Bedrock worlds** can't be used. Convert them to Java with Chunker and run them on Paper.
