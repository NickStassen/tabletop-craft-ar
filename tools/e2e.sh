#!/usr/bin/env bash
# End-to-end test on a Linux box: Paper 26.3 headless (offline mode, TEST ONLY) + plugin + fake Quest +
# mineflayer second player. Needs: JDK 25 on PATH (Paper 26.x and the plugin build both require Java 25), Node 18+,
# Python 3.11+ with `pip install -r tools/requirements.txt`.
# mineflayer only speaks up to 26.1, so the TEST server also gets ViaVersion + ViaBackwards (pinned, sha512-checked);
# the plugin itself never needs them.
# Accepting the EULA here is for local testing only; eula.txt, the server and Via jars live in tools/run/ (git-ignored).
set -euo pipefail
cd "$(dirname "$0")"
ROOT=$(cd .. && pwd)
RUN=run/server
mkdir -p "$RUN/plugins/TabletopCraftAR"
# Preflight: a stray server on 25565/8765 makes this script hang waiting on the wrong server.
for port in 25565 8765; do
  if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then echo "port $port already in use; stop the other server first" >&2; exit 2; fi
done
# Paper 26.x refuses to start on Java < 25, and this script would then wait forever for "Done (".
jv=$(java -XshowSettings:properties -version 2>&1 | sed -n 's/^ *java.specification.version = //p')
if [ "${jv%%.*}" -lt 25 ] 2>/dev/null || [ -z "$jv" ]; then echo "java on PATH is ${jv:-missing}; Paper 26.x needs JDK 25 (set JAVA_HOME and PATH)" >&2; exit 2; fi
UA="tabletop-craft-ar-e2e/0.1"
MC=26.3      # Paper / Minecraft version; the jar is named after it, so changing this re-downloads
VIA=5.12.0   # ViaVersion + ViaBackwards release (Modrinth) that supports 26.3
PAPER="paper-$MC.jar"
# fetch URL ALGO HASH DEST: download, then verify before the jar can ever run.
fetch() {
  curl -sfL -A "$UA" -o "$4.part" "$1"
  echo "$3  $4.part" | "${2}sum" -c --quiet - || { echo "$2 mismatch for $1" >&2; rm -f "$4.part"; exit 3; }
  mv "$4.part" "$4"
}
if [ ! -f "$RUN/$PAPER" ]; then
  rm -f "$RUN"/paper*.jar
  read -r url sha < <(curl -sf -A "$UA" "https://fill.papermc.io/v3/projects/paper/versions/$MC/builds/latest" |
    python3 -c "import json,sys;d=json.load(sys.stdin)['downloads']['server:default'];print(d['url'],d['checksums']['sha256'])") || { echo "Paper $MC lookup failed" >&2; exit 3; }
  fetch "$url" sha256 "$sha" "$RUN/$PAPER"
fi
for via in ViaVersion ViaBackwards; do
  if [ ! -f "$RUN/plugins/$via-$VIA.jar" ]; then
    rm -f "$RUN/plugins/$via"-*.jar
    read -r url sha < <(curl -sf -A "$UA" "https://api.modrinth.com/v2/project/${via,,}/version/$VIA" |
      python3 -c "import json,sys;f=[f for f in json.load(sys.stdin)['files'] if f['primary']][0];print(f['url'],f['hashes']['sha512'])") || { echo "$via $VIA lookup failed" >&2; exit 3; }
    fetch "$url" sha512 "$sha" "$RUN/plugins/$via-$VIA.jar"
  fi
done
(cd "$ROOT/server-plugin" && ./gradlew -q build)
cp "$ROOT"/server-plugin/build/libs/tabletop-craft-ar-plugin-*.jar "$RUN/plugins/"
sed 's/avatar-state: false/avatar-state: true/' "$ROOT/server-plugin/src/main/resources/config.yml" > "$RUN/plugins/TabletopCraftAR/config.yml"
echo "eula=true" > "$RUN/eula.txt"
cat > "$RUN/server.properties" <<'PROPS'
online-mode=false
white-list=false
level-type=minecraft\:flat
generator-settings={"layers"\:[{"block"\:"minecraft\:bedrock","height"\:1},{"block"\:"minecraft\:dirt","height"\:2},{"block"\:"minecraft\:grass_block","height"\:1}],"biome"\:"minecraft\:plains"}
generate-structures=false
spawn-protection=0
spawn-monsters=false
view-distance=6
PROPS
rm -f "$RUN/stdin.fifo"; mkfifo "$RUN/stdin.fifo"
# --pid: the console feeder exits with this script, so cleanup never needs a pattern kill (which hit other servers).
(cd "$RUN" && tail --pid=$$ -f stdin.fifo | java -Xmx2G -jar "$PAPER" --nogui > server.log 2>&1) &
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
c stop; sleep 5; kill $SERVER 2>/dev/null || true
echo "fake_quest exit code: $RC (report: tools/run/fake_report.json)"
exit $RC
