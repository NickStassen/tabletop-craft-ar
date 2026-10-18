#!/usr/bin/env bash
# Checks that VERSION, the docs/PROTOCOL.md version line and (when given) the release tag agree, and that the version
# has release notes (tools/release/release_notes.sh); prints the version. release.yml's first job runs it; run it
# yourself before tagging.
#
#   tools/release/check_version.sh           # VERSION and PROTOCOL.md
#   tools/release/check_version.sh v1.2.2    # and the tag
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TAG="${1:-}"
bad=0
err() {   # message [file]: a GitHub annotation in Actions, a FAIL line elsewhere
  if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::error${2:+ file=$2}::$1" >&2; else echo "FAIL $1" >&2; fi
  bad=1
}

V="$(tr -d '[:space:]' < "$ROOT/VERSION")"
[[ "$V" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || err "VERSION must be X.Y.Z, it is '$V'" VERSION
P="$(head -1 "$ROOT/docs/PROTOCOL.md" | sed -n 's/.*[ ,]v\([0-9][0-9.]*\)[[:space:]]*$/\1/p')"
[ "$P" = "$V" ] || err "the docs/PROTOCOL.md version line says v${P:-?}, VERSION says $V: update the header" docs/PROTOCOL.md
if [ -n "$TAG" ] && [ "$TAG" != "v$V" ]; then
  err "tag $TAG does not match VERSION $V: the tag for this commit must be v$V"
fi
if [ "$bad" = 0 ]; then
  notes="$("$ROOT/tools/release/release_notes.sh" "$V" --check 2>&1)" || err "$notes" docs/PROTOCOL.md
fi
[ "$bad" = 0 ] || exit 1
echo "$V"
