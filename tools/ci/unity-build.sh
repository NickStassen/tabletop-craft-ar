#!/usr/bin/env bash
# Builds the release-signed Quest APK headless in GameCI's Unity Docker image (release.yml, job "apk"; docs/RELEASING.md).
#
#   tools/ci/unity-build.sh build          # activate Unity, TabletopBatch.BuildReleaseApk, return the license
#   tools/ci/unity-build.sh check [APK]    # tools/release/upload_quest.sh --check-only with the image's build-tools
#   tools/ci/unity-build.sh scrub          # before caching quest-app/Library: drop Library/Bee and any file that holds
#                                          # a secret value (Unity writes the keystore passwords into Bee's files)
#
# Run tools/patch-meta-sdk-linux.sh first. Needs docker (and python3 for "build" and "scrub").
# Environment for "build":
#   Unity license, the GameCI way: UNITY_EMAIL + UNITY_PASSWORD + UNITY_SERIAL, where a Personal license's serial is
#   read from UNITY_LICENSE (the contents of Unity_lic.ulf). With UNITY_LICENSE alone the .ulf is tried as it is.
#   Release key: TABLETOP_KEYSTORE (path to the .jks on this machine), TABLETOP_KEYSTORE_PASS, TABLETOP_KEY_ALIAS,
#   TABLETOP_KEY_PASS; optional TABLETOP_BUILD (0-99). UNITY_IMAGE overrides the image, UNITY_BUILD_TIMEOUT (110m) bounds
#   the editor run so a hung build still returns the license before the job times out.
# Secrets reach the container as `docker run -e NAME` (docker copies the value from this environment, so it is never on
# a command line here) and are never printed. Unity's log goes to stdout and to quest-app/Logs/ci-unity-build.log.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
IMAGE="${UNITY_IMAGE:-unityci/editor:ubuntu-6000.0.84f1-android-3.2.2}"
APK_REL="quest-app/Builds/TabletopCraftAR.apk"
LOG_REL="quest-app/Logs/ci-unity-build.log"
P="${UNITY_BUILD_PROJECT:-/project}"   # the repo inside the container (overridable for a local test of this script)

fail() { echo "::error::$*" >&2; exit 1; }
gh_mask() { if [ -n "${GITHUB_ACTIONS:-}" ] && [ -n "$1" ]; then echo "::add-mask::$1"; fi; }

# ---------------------------------------------------------------- inside the container ----------------------------------
unity_bin() {
  local u="${UNITY_PATH:-/opt/unity}/Editor/Unity"   # GameCI images set UNITY_PATH=/opt/unity
  [ -x "$u" ] || fail "no Unity editor at $u (UNITY_PATH=${UNITY_PATH:-unset})"
  echo "$u"
}

# Prints the licensing lines of a Unity log. The log starts with the command line, one argument per line: the values
# after -serial, -username and -password are dropped (GitHub masks them too).
license_lines() {
  awk 'skip { skip = 0; next } /^-(serial|username|password)$/ { skip = 1; next } { print }' "$1" 2>/dev/null |
    grep -v -E '^[-/]' | grep -i -E 'licens|activat|entitlement|serial|seat|error' | tail -40 || true
}

RETURN_LICENSE=0
WROTE_ULF=""
inside_cleanup() {
  local rc=$?
  if [ "$RETURN_LICENSE" = 1 ]; then
    if timeout 10m "$UNITY" -batchmode -nographics -quit -returnlicense -username "$UNITY_EMAIL" -password "$UNITY_PASSWORD" \
         -projectPath /tmp/BlankProject -logFile /tmp/unity-return.log > /dev/null 2>&1; then
      echo "Unity license returned."
    else
      echo "::warning::Could not return the Unity license (it frees itself later; Unity ID > Subscriptions > Activations)."
      license_lines /tmp/unity-return.log
    fi
  fi
  if [ -n "$WROTE_ULF" ]; then rm -f "$WROTE_ULF"; fi
  # Files written by root in the container go back to the runner's user (actions/cache, the artifact upload).
  chown -R "${HOST_UID:-0}:${HOST_GID:-0}" "$P/quest-app" 2>/dev/null || true
  exit "$rc"
}

activate() {
  mkdir -p /tmp/BlankProject/Assets
  if [ -n "${UNITY_SERIAL:-}" ] && [ -n "${UNITY_EMAIL:-}" ] && [ -n "${UNITY_PASSWORD:-}" ]; then
    local attempt delay="${UNITY_ACTIVATION_RETRY_DELAY:-15}"
    for attempt in 1 2 3 4; do
      rm -f /tmp/unity-activate.log
      if timeout 10m "$UNITY" -batchmode -nographics -quit -serial "$UNITY_SERIAL" -username "$UNITY_EMAIL" \
           -password "$UNITY_PASSWORD" -projectPath /tmp/BlankProject -logFile /tmp/unity-activate.log > /dev/null 2>&1; then
        RETURN_LICENSE=1
        echo "Unity activated (serial, attempt $attempt)."
        return 0
      fi
      echo "::warning::Unity activation failed (attempt $attempt):"
      license_lines /tmp/unity-activate.log
      [ "$attempt" = 4 ] || { sleep "$delay"; delay=$((delay * 2)); }
    done
    fail "Unity activation failed 4 times: check UNITY_EMAIL, UNITY_PASSWORD and UNITY_LICENSE (release environment secrets) and the seat count at Unity ID > Subscriptions"
  elif [ -n "${UNITY_LICENSE:-}" ]; then
    # Without the email and password only the .ulf itself can be tried. A Personal license file is bound to the machine
    # that activated it, so expect Unity to refuse it here; GameCI's way is the serial plus UNITY_EMAIL/UNITY_PASSWORD.
    echo "::warning::UNITY_EMAIL/UNITY_PASSWORD are not set: trying UNITY_LICENSE as a license file on its own."
    WROTE_ULF="$HOME/.local/share/unity3d/Unity/Unity_lic.ulf"
    mkdir -p "$(dirname "$WROTE_ULF")"
    printf '%s' "$UNITY_LICENSE" | tr -d '\r' > "$WROTE_ULF"
    rm -f /tmp/unity-activate.log
    if ! timeout 10m "$UNITY" -batchmode -nographics -quit -manualLicenseFile "$WROTE_ULF" \
         -logFile /tmp/unity-activate.log > /dev/null 2>&1; then
      echo "Unity did not take the license file (-manualLicenseFile):"
      license_lines /tmp/unity-activate.log
    fi
  else
    fail "no Unity license: set UNITY_LICENSE, UNITY_EMAIL and UNITY_PASSWORD (release environment secrets)"
  fi
}

inside_build() {
  UNITY="$(unity_bin)"
  trap inside_cleanup EXIT
  echo "Unity: $UNITY"
  activate
  mkdir -p "$(dirname "$P/$LOG_REL")"
  rm -f "$P/$APK_REL"
  echo "::group::Unity build log (TabletopBatch.BuildReleaseApk)"
  set +e
  # Bounded well inside the job's timeout, so a hung editor still ends here and the license is returned.
  timeout --kill-after=2m "${UNITY_BUILD_TIMEOUT:-110m}" \
    "$UNITY" -batchmode -nographics -quit -projectPath "$P/quest-app" -buildTarget Android \
    -executeMethod TabletopCraft.EditorTools.TabletopBatch.BuildReleaseApk -logFile - 2>&1 | tee "$P/$LOG_REL"
  local rc=${PIPESTATUS[0]}
  set -e
  echo "::endgroup::"
  grep -E '^\[Tabletop\]|error CS|Shader error|BuildFailedException|Build completed with a result' "$P/$LOG_REL" | tail -30 || true
  if [ "$rc" != 0 ]; then
    if grep -q -i -E 'No valid Unity Editor license|license is not (active|valid)|com.unity.editor.headless.*not.*(found|valid)' "$P/$LOG_REL"; then
      fail "Unity has no valid license in the container: set UNITY_EMAIL and UNITY_PASSWORD next to UNITY_LICENSE (release environment)"
    fi
    [ "$rc" != 124 ] || fail "the Unity build ran out of time (${UNITY_BUILD_TIMEOUT:-110m}); the log is above"
    fail "the Unity build failed (exit $rc); the log is above (group 'Unity build log')"
  fi
  [ -f "$P/$APK_REL" ] || fail "Unity exited 0 but there is no $APK_REL"
  echo "Built $APK_REL ($(( $(wc -c < "$P/$APK_REL") / 1048576 )) MB)."
}

inside_check() {
  local apk="$1" sdk="${ANDROID_HOME:-}" jdk="${JAVA_HOME:-}" engine
  engine="$(dirname "$(unity_bin)")/Data/PlaybackEngines/AndroidPlayer"
  [ -n "$sdk" ] && [ -d "$sdk/build-tools" ] || sdk="$engine/SDK"
  [ -n "$jdk" ] && [ -x "$jdk/bin/java" ] || jdk="$engine/OpenJDK"
  [ -d "$sdk/build-tools" ] || fail "no Android build-tools in the image ($sdk)"
  export ANDROID_HOME="$sdk" JAVA_HOME="$jdk" PATH="$jdk/bin:$PATH"
  echo "build-tools: $(ls "$sdk/build-tools" | sort -V | tail -1), java: $("$jdk/bin/java" -version 2>&1 | head -1)"
  bash "$P/tools/release/upload_quest.sh" --check-only --apk "$P/$apk"
}

if [ "${1:-}" = "--inside" ]; then
  shift
  case "${1:-}" in
    build) inside_build ;;
    check) inside_check "$2" ;;
    *) fail "unknown inside command ${1:-}" ;;
  esac
  exit 0
fi

# ---------------------------------------------------------------- on the runner -----------------------------------------
docker_run() {
  docker run --rm -v "$ROOT:/project" -w /project -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" "$@"
}

# A Personal license's serial, as GameCI reads it: <DeveloperData Value="base64"/> in the .ulf, minus 4 leading bytes.
serial_from_license() {
  python3 -I -c '
import base64, os, sys
lic = os.environ.get("UNITY_LICENSE", "")
key = "<DeveloperData Value=\""
i = lic.find(key)
if i < 0:
    sys.exit(1)
i += len(key)
sys.stdout.write(base64.b64decode(lic[i:lic.find("\"", i)]).decode("latin-1")[4:])
'
}

host_build() {
  command -v docker > /dev/null || fail "docker is not installed"
  [ -d "$ROOT/quest-app/Packages/com.meta.xr.sdk.core" ] || fail "run tools/patch-meta-sdk-linux.sh first"
  local ks="${TABLETOP_KEYSTORE:-}" n
  [ -n "$ks" ] && [ -f "$ks" ] || fail "TABLETOP_KEYSTORE must name the release keystore file"
  for n in TABLETOP_KEYSTORE_PASS TABLETOP_KEY_ALIAS TABLETOP_KEY_PASS; do
    [ -n "${!n:-}" ] || fail "$n is not set"
  done
  if [ -z "${UNITY_SERIAL:-}" ] && [ -n "${UNITY_LICENSE:-}" ] && [ -n "${UNITY_EMAIL:-}" ] && [ -n "${UNITY_PASSWORD:-}" ]; then
    UNITY_SERIAL="$(serial_from_license)" || fail "UNITY_LICENSE has no <DeveloperData> serial (is it the contents of Unity_lic.ulf?)"
    export UNITY_SERIAL
  fi
  if [ -n "${UNITY_SERIAL:-}" ]; then
    gh_mask "$UNITY_SERIAL"
    gh_mask "${UNITY_SERIAL:0:${#UNITY_SERIAL}-4}XXXX"   # Unity logs the serial with its last 4 characters hidden
  fi
  local args=(-v "$(cd "$(dirname "$ks")" && pwd):/keystore:ro" -e "TABLETOP_KEYSTORE=/keystore/$(basename "$ks")")
  for n in UNITY_LICENSE UNITY_SERIAL UNITY_EMAIL UNITY_PASSWORD TABLETOP_KEYSTORE_PASS TABLETOP_KEY_ALIAS TABLETOP_KEY_PASS TABLETOP_BUILD; do
    if [ -n "${!n:-}" ]; then export "${n?}"; args+=(-e "$n"); fi
  done
  echo "Image: $IMAGE"
  docker_run "${args[@]}" "$IMAGE" bash /project/tools/ci/unity-build.sh --inside build
}

host_check() {
  local apk="${1:-$APK_REL}" abs
  abs="$(cd "$(dirname "$apk")" && pwd)/$(basename "$apk")"
  case "$abs" in "$ROOT"/*) ;; *) fail "the APK must be inside the repo ($apk)" ;; esac
  docker_run "$IMAGE" bash /project/tools/ci/unity-build.sh --inside check "${abs#"$ROOT"/}"
}

# Unity writes the keystore passwords in plain text into Library/Bee (the Gradle project's build.gradle, Bee's input
# data and logs). A cache saved from master can be restored by any pull request's workflow, so Bee never goes into the
# cache, and any other file holding a secret's value is deleted too (names printed, never values).
host_scrub() {
  local lib="$ROOT/quest-app/Library"
  [ -d "$lib" ] || { echo "No $lib, nothing to scrub."; return 0; }
  rm -rf "$lib/Bee"
  python3 -I - "$lib" <<'EOF'
import os, sys
names = ("TABLETOP_KEYSTORE_PASS", "TABLETOP_KEY_PASS", "UNITY_PASSWORD", "UNITY_SERIAL", "UNITY_EMAIL")
needles = [os.environ[n].encode() for n in names if len(os.environ.get(n, "")) >= 4]
root, removed, scanned = sys.argv[1], [], 0
for d, _, files in os.walk(root):
    for f in files:
        p = os.path.join(d, f)
        try:
            with open(p, "rb") as fh:
                data = fh.read()
        except OSError:
            continue
        scanned += 1
        if any(n in data for n in needles):
            os.remove(p)
            removed.append(os.path.relpath(p, root))
print(f"Scrubbed Library: Bee removed, {scanned} files scanned for {len(needles)} secret values, {len(removed)} removed")
for r in removed:
    print("  removed", r)
EOF
}

case "${1:-}" in
  build) host_build ;;
  check) host_check "${2:-}" ;;
  scrub) host_scrub ;;
  -h|--help|"") sed -n '2,17p' "$0" ;;
  *) fail "unknown command $1 (build, check, scrub)" ;;
esac
