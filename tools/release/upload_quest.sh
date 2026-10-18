#!/usr/bin/env bash
# Checks a release-signed Quest APK and uploads it to a Meta Horizon release channel with Meta's ovr-platform-util
# (docs/RELEASING.md). Linux and macOS (Windows: Git Bash or WSL).
#
#   tools/release/upload_quest.sh --check-only                 # only the packaging checks on the APK
#   tools/release/upload_quest.sh --dry-run                    # checks, then print the upload command (token masked)
#   tools/release/upload_quest.sh --channel BETA --notes "1.1.0: pairing, automatic game files"
#
# Options: --apk PATH (default quest-app/Builds/TabletopCraftAR.apk), --channel NAME (default BETA), --age-group GROUP
# (default TEENS_AND_ADULTS), --notes TEXT or --notes-file FILE (default: the PROTOCOL.md changelog line for VERSION),
# --symbols-dir DIR (il2cpp debug symbols, *.so), --dry-run, --check-only.
#
# Secrets never go on this script's command line or into the repo: TABLETOP_APP_ID and TABLETOP_UPLOAD_TOKEN (Developer
# Dashboard > the app > API, and "Get Oculus platform utility token" in your account settings) come from the
# environment or from ~/.config/tabletop-craft-ar/upload.env (KEY=value lines, chmod 600). ovr-platform-util itself only
# takes the token as an argument, so other users of this machine can see it in the process list while it runs.
# Tools: ovr-platform-util (on PATH or OVR_PLATFORM_UTIL), apksigner and aapt2 from Android build-tools (ANDROID_HOME,
# TABLETOP_BUILD_TOOLS, or the SDK inside Unity 6000.0.84f1).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APK="$ROOT/quest-app/Builds/TabletopCraftAR.apk"
CHANNEL=BETA
AGE=TEENS_AND_ADULTS
NOTES=""
NOTES_FILE=""
SYMBOLS=""
DRY=0
CHECK_ONLY=0
PACKAGE=com.tabletopcraft.ar

while [ $# -gt 0 ]; do
  case "$1" in
    --apk) APK="$2"; shift 2 ;;
    --channel) CHANNEL="$2"; shift 2 ;;
    --age-group) AGE="$2"; shift 2 ;;
    --notes) NOTES="$2"; shift 2 ;;
    --notes-file) NOTES_FILE="$2"; shift 2 ;;
    --symbols-dir) SYMBOLS="$2"; shift 2 ;;
    --dry-run) DRY=1; shift ;;
    --check-only) CHECK_ONLY=1; shift ;;
    -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
    *) echo "unknown option $1 (--help)" >&2; exit 2 ;;
  esac
done

fail() { echo "FAIL $*" >&2; exit 1; }
ok() { echo "ok   $*"; }

# ---- tools ----
newest() { ls -d "$@" 2>/dev/null | sort -V | tail -1; }
find_build_tools() {
  local d
  for d in "${TABLETOP_BUILD_TOOLS:-}" \
           "$( [ -n "${ANDROID_HOME:-}" ] && newest "$ANDROID_HOME"/build-tools/* )" \
           "$(newest "$HOME"/Unity/Hub/Editor/6000.0.84f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/build-tools/*)" \
           "$(newest /Applications/Unity/Hub/Editor/6000.0.84f1/PlaybackEngines/AndroidPlayer/SDK/build-tools/*)"; do
    if [ -n "$d" ] && [ -x "$d/apksigner" ] && [ -x "$d/aapt2" ]; then echo "$d"; return; fi
  done
}
BT="$(find_build_tools)"
[ -n "$BT" ] || fail "no Android build-tools with apksigner and aapt2: set ANDROID_HOME or TABLETOP_BUILD_TOOLS"

# ---- packaging checks (VRC.Quest.Packaging, docs/RELEASING.md "Checks") ----
[ -f "$APK" ] || fail "no APK at $APK (build it with TabletopBatch.BuildReleaseApk)"
SIZE=$(wc -c < "$APK" | tr -d ' ')
[ "$SIZE" -lt 1073741824 ] || fail "APK is $SIZE bytes; the store limit is 1 GB"
ok "APK $(basename "$APK"), $((SIZE / 1048576)) MB"

SIGN="$("$BT/apksigner" verify --verbose --print-certs "$APK" 2>&1)" || { echo "$SIGN" | tail -5 >&2; fail "apksigner could not verify the APK"; }
grep -q "Verified using v2 scheme (APK Signature Scheme v2): true" <<< "$SIGN" || fail "not signed with APK Signature Scheme v2"
ok "signature scheme v2"
DN="$(grep -m1 "certificate DN:" <<< "$SIGN" | sed 's/.*certificate DN: //')"
case "$DN" in
  *"CN=Android Debug"*) fail "signed with the Android debug key ($DN): build with BuildReleaseApk and the TABLETOP_KEYSTORE variables" ;;
esac
ok "release certificate: $DN"
echo "     SHA-256 $(grep -m1 "certificate SHA-256 digest:" <<< "$SIGN" | sed 's/.*digest: //')"

BADGING="$("$BT/aapt2" dump badging "$APK" 2>/dev/null)" || fail "aapt2 could not read the APK"
pkg_line="$(grep -m1 "^package:" <<< "$BADGING")"
grep -q "name='$PACKAGE'" <<< "$pkg_line" || fail "package is not $PACKAGE: $pkg_line"
VCODE="$(echo "$pkg_line" | sed -n "s/.*versionCode='\([0-9]*\)'.*/\1/p")"
VNAME="$(echo "$pkg_line" | sed -n "s/.*versionName='\([^']*\)'.*/\1/p")"
ok "package $PACKAGE $VNAME (versionCode $VCODE)"
[ "$VNAME" = "$(tr -d '[:space:]' < "$ROOT/VERSION")" ] || echo "WARN versionName $VNAME is not VERSION $(cat "$ROOT/VERSION")"
if grep -q "application-debuggable" <<< "$BADGING"; then fail "the APK is debuggable (a development build)"; fi
ok "not debuggable"
grep -qE "^(minSdkVersion|sdkVersion):'32'" <<< "$BADGING" || fail "minSdkVersion must be 32: $(grep -m1 -E '^(minSdkVersion|sdkVersion)' <<< "$BADGING" || true)"
grep -q "^targetSdkVersion:'34'" <<< "$BADGING" || fail "targetSdkVersion must be 34: $(grep -m1 '^targetSdkVersion' <<< "$BADGING" || true)"
ok "minSdk 32, targetSdk 34"
NATIVE="$(grep -m1 "^native-code:" <<< "$BADGING" || true)"
[ "$NATIVE" = "native-code: 'arm64-v8a'" ] || fail "native code must be arm64-v8a only: ${NATIVE:-none}"
ok "arm64-v8a only"
if [ "$CHECK_ONLY" = 1 ]; then echo "Checks passed."; exit 0; fi

# ---- secrets ----
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

if [ -n "$NOTES_FILE" ]; then NOTES="$(cat "$NOTES_FILE")"; fi
if [ -z "$NOTES" ]; then
  NOTES="$(grep -m1 -F -- "- **v$VNAME" "$ROOT/docs/PROTOCOL.md" | sed 's/^- //; s/\*\*//g' | cut -c1-900 || true)"
  [ -n "$NOTES" ] || NOTES="Tabletop Craft AR $VNAME"
fi

OVR="${OVR_PLATFORM_UTIL:-$(command -v ovr-platform-util || true)}"
CMD=("${OVR:-ovr-platform-util}" upload-quest-build --app-id "$TABLETOP_APP_ID" --token "$TABLETOP_UPLOAD_TOKEN"
     --apk "$APK" --channel "$CHANNEL" --age-group "$AGE" --notes "$NOTES")
if [ -n "$SYMBOLS" ]; then
  [ -d "$SYMBOLS" ] || fail "--symbols-dir $SYMBOLS is not a folder"
  CMD+=(--debug-symbols-dir "$SYMBOLS" --debug-symbols-pattern '*.so')
fi
if [ -n "${CI:-}" ]; then CMD+=(--disable-progress-bar); fi   # CI logs (release.yml): no progress bar redraws

shown=()
for a in "${CMD[@]}"; do
  if [ "$a" = "$TABLETOP_UPLOAD_TOKEN" ]; then shown+=("<token>"); else shown+=("$(printf %q "$a")"); fi
done
echo "Upload: ${shown[*]}"
if [ "$DRY" = 1 ]; then echo "Dry run: nothing uploaded."; exit 0; fi
[ -n "$OVR" ] || fail "ovr-platform-util not found: install it (docs/RELEASING.md) or set OVR_PLATFORM_UTIL"
"${CMD[@]}"
echo "Uploaded $VNAME (versionCode $VCODE) to $CHANNEL. Check the build in the Developer Dashboard > Release channels."
