#!/usr/bin/env bash
# Downloads Meta's ovr-platform-util for Linux (Meta marks the Linux build experimental) into DIR and prints its path.
# The URL is the one on Meta's "Platform command line utility" page (developers.meta.com/horizon/resources/
# publish-reference-platform-command-line-utility/); it always serves the newest version.
#
#   OVR_PLATFORM_UTIL="$(tools/release/fetch_ovr_platform_util.sh "$RUNNER_TEMP/ovr")"
set -euo pipefail

DIR="${1:-${TMPDIR:-/tmp}/ovr-platform-util}"
URL="https://www.oculus.com/download_app/?id=5159709737372459&access_token=OC%7C5159709737372459%7C"

mkdir -p "$DIR"
curl -fsSL --retry 3 --retry-delay 5 -o "$DIR/ovr-platform-util" "$URL"
if [ "$(head -c 4 "$DIR/ovr-platform-util" | od -An -c | tr -d ' \n')" != '177ELF' ]; then
  echo "::error::the download from Meta is not a Linux executable" >&2
  exit 1
fi
chmod +x "$DIR/ovr-platform-util"
{
  "$DIR/ovr-platform-util" version | head -1
  echo "sha256 $(sha256sum "$DIR/ovr-platform-util" | cut -d' ' -f1)"
} >&2
echo "$DIR/ovr-platform-util"
