#!/usr/bin/env bash
# End-to-end test on a Linux box: Paper 1.21.11 headless (offline mode, TEST ONLY) + plugin + fake Quest +
# mineflayer second player. Needs: JDK 21 on PATH, Node 18+, Python 3.11+ with `pip install -r tools/requirements.txt`.
# Accepting the EULA here is for local testing only; eula.txt and the server jar live in tools/run/ (git-ignored).
set -euo pipefail
cd "$(dirname "$0")"
ROOT=$(cd .. && pwd)
RUN=run/server
mkdir -p "$RUN/plugins/TabletopCraftAR"
# Preflight: a stray server on 25565/8765 makes this script hang waiting on the wrong server.
for port in 25565 8765; do
  if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then echo "port $port already in use; stop the other server first" >&2; exit 2; fi
done
UA="tabletop-craft-ar-e2e/0.1"
if [ ! -f "$RUN/paper.jar" ]; then
  url=$(curl -s -A "$UA" https://fill.papermc.io/v3/projects/paper/versions/1.21.11/builds/latest | python3 -c "import json,sys;print(json.load(sys.stdin)['downloads']['server:default']['url'])")
  curl -sL -A "$UA" -o "$RUN/paper.jar" "$url"
fi
(cd "$ROOT/server-plugin" && ./gradlew -q build)
cp "$ROOT"/server-plugin/build/libs/tabletop-craft-ar-plugin-*.jar "$RUN/plugins/"
sed 's/avatar-state: false/avatar-state: true/' "$ROOT/server-plugin/src/main/resources/config.yml" > "$RUN/plugins/TabletopCraftAR/config.yml"
echo "eula=true" > "$RUN/eula.txt"
cat > "$RUN/server.properties" <<'PROPS'
online-mode=false
level-type=minecraft\:flat
generate-structures=false
spawn-protection=0
spawn-monsters=false
view-distance=6
PROPS
rm -f "$RUN/stdin.fifo"; mkfifo "$RUN/stdin.fifo"
(cd "$RUN" && tail -f stdin.fifo | java -Xmx2G -jar paper.jar --nogui > server.log 2>&1) &
SERVER=$!
c() { echo "$*" > "$RUN/stdin.fifo"; sleep 0.5; }
until grep -q "Done (" "$RUN/server.log" 2>/dev/null; do sleep 1; done
c gamerule random_tick_speed 0
c tabletop setorigin 0 -64 0
c fill 0 -60 0 31 -60 31 minecraft:stone
c setblock 5 -59 5 minecraft:gold_block
c "setblock 10 -59 20 minecraft:oak_stairs[facing=east]"
c setblock 20 -59 20 minecraft:torch
(cd bot && npm install --silent >/dev/null 2>&1 || true)
python3 fake_quest.py --duration 25 --avatar \
  --expect 7,-59,7=minecraft:diamond_block --expect 5,-59,5=minecraft:air --expect 10,-59,20=minecraft:oak_stairs \
  --expect 20,-59,20=minecraft:torch --expect-count minecraft:stone=1024 --expect-count minecraft:bedrock=1024 \
  --expect-count minecraft:cobblestone=1 --report run/fake_report.json > run/fake.out 2>&1 &
FAKE=$!
sleep 3
(cd bot && BOT_MS=20000 node second_player.js > ../run/bot.out 2>&1) &
sleep 3
c gamemode creative TestWalker
c tp TestWalker 4.5 -59 4.5
c give TestWalker minecraft:cobblestone 4
sleep 3
c setblock 7 -59 7 minecraft:diamond_block
c setblock 5 -59 5 minecraft:air
sleep 3
c tabletop avatar status
RC=0; wait $FAKE || RC=$?
sleep 2
SERVER_FIFO="$RUN/stdin.fifo" python3 consistency_check.py | tee run/consistency.out
python3 error_paths.py | tee run/errors.out
grep -E "ENTITY_SPAWN|PLACED|SUMMARY" run/bot.out || true
grep -E "AVATAR_" "$RUN/server.log" | tail -4 || true
c stop; sleep 5; kill $SERVER 2>/dev/null || true; pkill -f "tail -f stdin.fifo" || true
echo "fake_quest exit code: $RC (report: tools/run/fake_report.json)"
exit $RC
