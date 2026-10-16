#!/usr/bin/env bash
# Tabletop Craft AR server starter for Linux and macOS (Windows: start.bat). It downloads, never redistributes:
#   - Paper (the Minecraft server) for the supported game version, from PaperMC's own download service (checked by SHA-256);
#   - the newest TabletopCraftAR plugin release from GitHub (checked by SHA-256);
#   - optionally Geyser and Floodgate (Bedrock players) from GeyserMC's download service (checked by SHA-256);
# asks you to accept the Minecraft EULA (you must answer: nothing accepts it for you), writes sensible settings on the
# first run and starts the server in this window. Run it again to update Paper and the plugin. docs/QUEST_SETUP.md
# "Store install" has the whole setup.
#
#   ./start.sh [--dir DIR] [--port 25565] [--ws-port 8765] [--memory 4G] [--bedrock yes|no] [--no-start]
#              [--plugin-jar PATH] [--java PATH]
#
#   --dir        the server folder (default ~/TabletopCraftServer)
#   --port       the Minecraft port for Java players (first run; default 25565)
#   --ws-port    the headset link and LAN discovery port, TCP and UDP (first run; default 8765)
#   --bedrock    install Geyser + Floodgate without asking (yes) or skip them (no)
#   --plugin-jar use this plugin jar instead of the newest release (for testing a build)
#   --no-start   install or update, but don't start the server
set -euo pipefail

REPO="NickStassen/tabletop-craft-ar"
DEFAULT_MC="26.3"          # the game version the plugin supports when its jar doesn't say
JAVA_MAJOR=25
UA="tabletop-craft-ar-starter/1.1 (+https://github.com/$REPO)"
DIR="$HOME/TabletopCraftServer"
PORT=25565
WS_PORT=8765
MEMORY=4G
BEDROCK=""
START=1
PLUGIN_JAR=""
JAVA="${JAVA:-java}"

while [ $# -gt 0 ]; do
  case "$1" in
    --dir) DIR="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --ws-port) WS_PORT="$2"; shift 2 ;;
    --memory) MEMORY="$2"; shift 2 ;;
    --bedrock) BEDROCK="$2"; shift 2 ;;
    --plugin-jar) PLUGIN_JAR="$2"; shift 2 ;;
    --java) JAVA="$2"; shift 2 ;;
    --no-start) START=0; shift ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown option $1 (--help)" >&2; exit 2 ;;
  esac
done

say() { printf '%s\n' "$*"; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
ask() {   # ask "question" default(y|n) -> 0 for yes
  local q="$1" def="$2" a
  if [ "$def" = y ]; then printf '%s [Y/n] ' "$q"; else printf '%s [y/N] ' "$q"; fi
  if ! read -r a; then a=""; fi
  a="$(printf '%s' "$a" | tr '[:upper:]' '[:lower:]')"
  [ -z "$a" ] && a="$def"
  case "$a" in y|yes) return 0 ;; *) return 1 ;; esac
}
sha256_of() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
fetch() { curl -fsSL --retry 2 -A "$UA" "$@"; }
# download URL SHA256 DEST: to a temp file, checked, then moved into place
download() {
  local url="$1" sha="$2" dest="$3" tmp
  tmp="$dest.part"
  fetch -o "$tmp" "$url" || { rm -f "$tmp"; die "download failed: $url"; }
  if [ -n "$sha" ] && [ "$(sha256_of "$tmp")" != "$sha" ]; then rm -f "$tmp"; die "checksum mismatch for $url"; fi
  mv -f "$tmp" "$dest"
}
# json_field JSON KEY: the first "KEY":"value" (string values only; good enough for these compact APIs)
json_field() { printf '%s' "$1" | grep -o "\"$2\" *: *\"[^\"]*\"" | head -1 | sed 's/.*: *"\(.*\)"/\1/'; }
json_number() { printf '%s' "$1" | grep -o "\"$2\" *: *[0-9][0-9]*" | head -1 | sed 's/.*: *//'; }
# split TEXT at every occurrence of SEP (a literal), one piece per line (portable: awk, not GNU sed's \n)
split_at() { printf '%s' "$1" | awk -v sep="$2" '{ n = split($0, p, sep); for (i = 1; i <= n; i++) print (i > 1 ? sep : "") p[i] }'; }

command -v curl >/dev/null || die "curl is needed (install it with your package manager)"

# ---- Java 25 ----
if ! command -v "$JAVA" >/dev/null; then
  die "Java $JAVA_MAJOR is needed. Install a Java $JAVA_MAJOR runtime (for example Eclipse Temurin: https://adoptium.net/temurin/releases/?version=$JAVA_MAJOR), then run this again."
fi
jv="$("$JAVA" -version 2>&1 | head -1 | sed 's/.*version "\([0-9]*\).*/\1/')"
if [ -z "$jv" ] || [ "$jv" -lt "$JAVA_MAJOR" ] 2>/dev/null; then
  die "Java $JAVA_MAJOR or newer is needed, found: $("$JAVA" -version 2>&1 | head -1). Get it from https://adoptium.net/temurin/releases/?version=$JAVA_MAJOR (or pass --java PATH)."
fi
say "Java $jv: ok"

mkdir -p "$DIR/plugins"
DIR="$(cd "$DIR" && pwd)"
cd "$DIR"
CONF="$DIR/tabletop-starter.conf"
[ -f "$CONF" ] && . "$CONF"
FIRST=0; [ -f server.properties ] || FIRST=1

# ---- the plugin (newest GitHub release, or --plugin-jar) ----
if [ -n "$PLUGIN_JAR" ]; then
  [ -f "$PLUGIN_JAR" ] || die "--plugin-jar $PLUGIN_JAR not found"
  name="$(basename "$PLUGIN_JAR")"
  case "$name" in tabletop-craft-ar-plugin-*.jar) ;; *) name="tabletop-craft-ar-plugin-local.jar" ;; esac
  rm -f plugins/tabletop-craft-ar-plugin-*.jar
  cp "$PLUGIN_JAR" "plugins/$name"
  say "Plugin: $name (from $PLUGIN_JAR)"
else
  rel="$(fetch -H "Accept: application/vnd.github+json" "https://api.github.com/repos/$REPO/releases/latest")" \
    || die "can't reach GitHub to find the plugin (https://github.com/$REPO/releases)"
  # the release asset tabletop-craft-ar-plugin-<version>.jar: its download URL and SHA-256 (GitHub's "digest")
  url=""; sha=""
  if command -v python3 >/dev/null; then
    read -r url sha < <(printf '%s' "$rel" | python3 -c '
import json, re, sys
for a in json.load(sys.stdin).get("assets", []):
    if re.fullmatch(r"tabletop-craft-ar-plugin-[0-9][0-9.]*\.jar", a.get("name", "")):
        print(a["browser_download_url"], (a.get("digest") or "").replace("sha256:", "") or "-")
        break' || true)
    [ "$sha" = "-" ] && sha=""
  else   # no python3: one asset object per line, then the plugin jar's line
    line="$(split_at "$rel" '{"url":"https://api.github.com/repos/' | grep -m1 '/tabletop-craft-ar-plugin-[0-9][0-9.]*\.jar"' || true)"
    url="$(printf '%s' "$line" | grep -o '"browser_download_url" *: *"[^"]*/tabletop-craft-ar-plugin-[0-9][0-9.]*\.jar"' | head -1 | sed 's/.*: *"\(.*\)"/\1/')"
    sha="$(json_field "$line" digest | sed 's/^sha256://')"
  fi
  [ -n "$url" ] || die "the newest release has no tabletop-craft-ar-plugin-<version>.jar (https://github.com/$REPO/releases)"
  name="${url##*/}"
  if [ -f "plugins/$name" ] && { [ -z "$sha" ] || [ "$(sha256_of "plugins/$name")" = "$sha" ]; }; then
    say "Plugin: $name (up to date)"
  else
    say "Plugin: downloading $name ..."
    download "$url" "$sha" "plugins/$name.new"
    rm -f plugins/tabletop-craft-ar-plugin-*.jar
    mv "plugins/$name.new" "plugins/$name"
  fi
fi
PLUGIN="plugins/$name"

# ---- the game version the plugin supports (its plugin.yml api-version), then Paper for it ----
MC="$DEFAULT_MC"
if command -v unzip >/dev/null; then
  v="$(unzip -p "$PLUGIN" plugin.yml 2>/dev/null | sed -n "s/^api-version: *'\{0,1\}\([0-9.]*\)'\{0,1\}.*/\1/p" | head -1)"
  [ -n "$v" ] && MC="$v"
fi
builds="$(fetch "https://fill.papermc.io/v3/projects/paper/versions/$MC/builds")" || die "can't reach PaperMC's download service for Minecraft $MC"
stable="$(split_at "$builds" '{"id":' | grep -m1 '"channel":"STABLE"' || true)"
[ -n "$stable" ] || die "PaperMC has no stable build for Minecraft $MC yet"
pbuild="$(json_number "$stable" id)"
purl="$(json_field "$stable" url)"
psha="$(json_field "$stable" sha256)"
if [ -f paper.jar ] && [ "$(sha256_of paper.jar)" = "$psha" ]; then
  say "Paper $MC build $pbuild: up to date"
else
  say "Paper $MC build $pbuild: downloading ..."
  download "$purl" "$psha" paper.jar
fi

# ---- Geyser + Floodgate (optional) ----
if [ -z "$BEDROCK" ]; then
  BEDROCK="${STARTER_BEDROCK:-}"
  if [ -z "$BEDROCK" ]; then
    if ask "Let Bedrock players (phones, consoles, Windows) join too? Installs Geyser and Floodgate from GeyserMC" n; then BEDROCK=yes; else BEDROCK=no; fi
  fi
fi
if [ "$BEDROCK" = yes ]; then
  for p in geyser floodgate; do
    info="$(fetch "https://download.geysermc.org/v2/projects/$p/versions/latest/builds/latest")" || die "can't reach GeyserMC's download service"
    gv="$(json_field "$info" version)"; gb="$(json_number "$info" build)"
    gsha="$(printf '%s' "$info" | grep -o '"spigot" *: *{[^}]*}' | grep -o '"sha256" *: *"[^"]*"' | sed 's/.*: *"\(.*\)"/\1/')"
    gfile="plugins/$( [ $p = geyser ] && echo Geyser-Spigot.jar || echo floodgate-spigot.jar )"
    if [ -f "$gfile" ] && [ "$(sha256_of "$gfile")" = "$gsha" ]; then say "$p $gv build $gb: up to date"
    else say "$p $gv build $gb: downloading ..."; download "https://download.geysermc.org/v2/projects/$p/versions/$gv/builds/$gb/downloads/spigot" "$gsha" "$gfile"; fi
  done
fi
printf 'STARTER_BEDROCK=%s\n' "$BEDROCK" > "$CONF"

# ---- the Minecraft EULA: only you can accept it ----
if ! grep -qs '^eula=true' eula.txt; then
  say ""
  say "Minecraft's End User License Agreement applies to running a server: https://aka.ms/MinecraftEULA"
  if ask "Have you read and do you accept the Minecraft EULA?" n; then
    printf '# Accepted by the user in the Tabletop Craft AR starter on %s\neula=true\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > eula.txt
  else
    say "Not accepted: the server can't start. Run this again when you want to accept it."
    exit 1
  fi
fi

# ---- first run: server and plugin settings ----
if [ "$FIRST" = 1 ]; then
  open=false
  say ""
  say "Java players: by default only players you add to the whitelist can join (type: whitelist add <name>)."
  if ask "Let any Java player who can reach this PC join instead (no whitelist)?" n; then open=true; fi
  {
    printf 'motd=Tabletop Craft AR\n'
    printf 'server-port=%s\n' "$PORT"
    if [ "$open" = true ]; then printf 'white-list=false\nenforce-whitelist=false\n'; else printf 'white-list=true\nenforce-whitelist=true\n'; fi
  } > server.properties
  mkdir -p plugins/TabletopCraftAR
  if [ ! -f plugins/TabletopCraftAR/config.yml ]; then
    # The plugin's own defaults, with: headsets must be paired first, and the chosen link port.
    if command -v unzip >/dev/null && unzip -p "$PLUGIN" config.yml > plugins/TabletopCraftAR/config.yml 2>/dev/null && [ -s plugins/TabletopCraftAR/config.yml ]; then
      sed -i.bak -e 's/^  require-pairing: false$/  require-pairing: true/' -e "s/^  port: 8765\$/  port: $WS_PORT/" plugins/TabletopCraftAR/config.yml
      rm -f plugins/TabletopCraftAR/config.yml.bak
    else
      printf 'auth:\n  require-pairing: true\nbind:\n  port: %s\ndiscovery:\n  port: %s\n' "$WS_PORT" "$WS_PORT" > plugins/TabletopCraftAR/config.yml
    fi
  fi
fi

# ---- what to do next ----
ip=""
if command -v ip >/dev/null; then ip="$(ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.*src \([0-9.]*\).*/\1/p' | head -1)"; fi
if [ -z "$ip" ] && command -v ipconfig >/dev/null; then ip="$(ipconfig getifaddr en0 2>/dev/null || true)"; fi
wsp="$(sed -n 's/^  port: \([0-9]*\)$/\1/p' plugins/TabletopCraftAR/config.yml 2>/dev/null | head -1)"; wsp="${wsp:-$WS_PORT}"
say ""
say "Server folder: $DIR"
say "This PC on your network: ${ip:-<its LAN address>}  (Java players: ${ip:-<address>}:$(sed -n 's/^server-port=//p' server.properties))"
say "Headsets find it by themselves on the same Wi-Fi (TCP and UDP $wsp must be open in this PC's firewall:"
say "  e.g. sudo ufw allow $wsp  or  sudo firewall-cmd --add-port=$wsp/tcp --add-port=$wsp/udp)."
say "Start Minecraft: Java Edition $MC once in the launcher on this PC: the server sends its textures and sounds to"
say "your paired headsets. When the headset shows a 6-digit code, type in this window:  tabletop pair <code>"
say ""
[ "$START" = 1 ] || { say "Installed (--no-start)."; exit 0; }
say "Starting the server (type stop to end it) ..."
exec "$JAVA" -Xms1G "-Xmx$MEMORY" -jar paper.jar --nogui
