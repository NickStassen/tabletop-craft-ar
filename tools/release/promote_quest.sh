#!/usr/bin/env bash
# Moves the build a Meta Horizon release channel serves to another channel (default ALPHA to BETA), after checking that
# the source channel serves the expected version (docs/RELEASING.md "Promoting a release"). Linux and macOS.
#
#   tools/release/promote_quest.sh 1.2.2 --dry-run     # show what the channels serve and check ALPHA; change nothing
#   tools/release/promote_quest.sh 1.2.2               # ALPHA's build (it must be 1.2.2) to BETA
#
# Options: --from CHANNEL (default ALPHA), --to CHANNEL (default BETA), --age-group GROUP (default TEENS_AND_ADULTS),
# --dry-run. Credentials as for upload_quest.sh: TABLETOP_APP_ID and TABLETOP_UPLOAD_TOKEN from the environment or
# ~/.config/tabletop-craft-ar/upload.env (chmod 600), never as options. ovr-platform-util (on PATH or OVR_PLATFORM_UTIL)
# only takes the token as an argument, so it shows in this machine's process list while it runs; it is never printed.
set -euo pipefail

VERSION_WANTED=""
FROM=ALPHA
TO=BETA
AGE=TEENS_AND_ADULTS
DRY=0
while [ $# -gt 0 ]; do
  case "$1" in
    --from) FROM="$2"; shift 2 ;;
    --to) TO="$2"; shift 2 ;;
    --age-group) AGE="$2"; shift 2 ;;
    --dry-run) DRY=1; shift ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    -*) echo "unknown option $1 (--help)" >&2; exit 2 ;;
    *) VERSION_WANTED="${1#v}"; shift ;;
  esac
done

fail() { echo "FAIL $*" >&2; exit 1; }
[[ "$VERSION_WANTED" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "give the version to promote, like 1.2.2 (got '${VERSION_WANTED}')"

ENVFILE="$HOME/.config/tabletop-craft-ar/upload.env"
if [ -f "$ENVFILE" ] && { [ -z "${TABLETOP_APP_ID:-}" ] || [ -z "${TABLETOP_UPLOAD_TOKEN:-}" ]; }; then
  perm="$(stat -c %a "$ENVFILE" 2>/dev/null || stat -f %Lp "$ENVFILE")"
  [ "$perm" = 600 ] || [ "$perm" = 400 ] || fail "$ENVFILE must be chmod 600 (it is $perm)"
  while IFS='=' read -r k v; do
    case "$k" in
      TABLETOP_APP_ID) [ -n "${TABLETOP_APP_ID:-}" ] || TABLETOP_APP_ID="$v" ;;
      TABLETOP_UPLOAD_TOKEN) [ -n "${TABLETOP_UPLOAD_TOKEN:-}" ] || TABLETOP_UPLOAD_TOKEN="$v" ;;
    esac
  done < <(grep -E '^(TABLETOP_APP_ID|TABLETOP_UPLOAD_TOKEN)=' "$ENVFILE" || true)
fi
[ -n "${TABLETOP_APP_ID:-}" ] || fail "TABLETOP_APP_ID is not set (environment or $ENVFILE)"
[ -n "${TABLETOP_UPLOAD_TOKEN:-}" ] || fail "TABLETOP_UPLOAD_TOKEN is not set (environment or $ENVFILE)"
OVR="${OVR_PLATFORM_UTIL:-$(command -v ovr-platform-util || true)}"
[ -n "$OVR" ] || fail "ovr-platform-util not found: install it (docs/RELEASING.md) or set OVR_PLATFORM_UTIL"

# get-release-channel-data prints a table: " Channel Name | Channel ID | Version Code | Version Name".
TABLE=""
read_channels() {
  local out
  out="$("$OVR" get-release-channel-data --app-id "$TABLETOP_APP_ID" --token "$TABLETOP_UPLOAD_TOKEN" 2>&1)" ||
    { printf '%s\n' "$out" | tail -5 >&2; fail "get-release-channel-data failed (token or app ID?)"; }
  TABLE="$(printf '%s\n' "$out" | sed 's/\x1b\[[0-9;]*m//g' | grep '|' || true)"
  [ -n "$TABLE" ] || { printf '%s\n' "$out" | tail -5 >&2; fail "get-release-channel-data printed no channel table"; }
}
# "<versionName> <versionCode>" served by channel $1 (names compared without case), empty if there's no such channel.
served() {
  printf '%s\n' "$TABLE" | awk -F'|' -v ch="$1" 'NF >= 4 {
    n = $1; gsub(/^[ \t]+|[ \t]+$/, "", n)
    if (toupper(n) == toupper(ch)) { c = $3; v = $4; gsub(/[ \tv]/, "", c); gsub(/^[ \t]+|[ \t]+$/, "", v); print v " " c; exit }
  }'
}

read_channels
echo "Release channels:"
printf '%s\n' "$TABLE"
src="$(served "$FROM")"
dst="$(served "$TO")"
[ -n "$src" ] || fail "no release channel named $FROM"
[ -n "$dst" ] || fail "no release channel named $TO"
[ "${src%% *}" = "$VERSION_WANTED" ] ||
  fail "$FROM serves ${src%% *} (versionCode ${src##* }), not $VERSION_WANTED: release v$VERSION_WANTED to $FROM first (release.yml)"
echo "ok   $FROM serves $VERSION_WANTED (versionCode ${src##* }); $TO serves ${dst%% *} (versionCode ${dst##* })"
if [ "${dst%% *}" = "$VERSION_WANTED" ] && [ "${dst##* }" = "${src##* }" ]; then
  echo "$TO already serves $VERSION_WANTED: nothing to do."
  exit 0
fi
if [ "$DRY" = 1 ]; then
  echo "Dry run: would run set-release-channel-build --source-channel $FROM --destination-channel $TO --age-group $AGE"
  exit 0
fi

"$OVR" set-release-channel-build --app-id "$TABLETOP_APP_ID" --token "$TABLETOP_UPLOAD_TOKEN" \
  --source-channel "$FROM" --destination-channel "$TO" --age-group "$AGE"

for attempt in 1 2 3 4 5 6; do
  read_channels
  dst="$(served "$TO")"
  if [ "${dst%% *}" = "$VERSION_WANTED" ]; then
    echo "ok   $TO serves $VERSION_WANTED (versionCode ${dst##* })"
    exit 0
  fi
  [ "$attempt" = 6 ] || sleep 10
done
printf '%s\n' "$TABLE"
fail "$TO still serves ${dst%% *} after set-release-channel-build: check the Developer Dashboard > Release channels"
