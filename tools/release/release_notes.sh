#!/usr/bin/env bash
# Release notes for a version (docs/release-notes/README.md): docs/release-notes/vX.Y.Z.md when it exists, else the
# docs/PROTOCOL.md changelog entry for that version (every "- **vX.Y.Z..." line). Used by release.yml.
#
#   tools/release/release_notes.sh 1.2.2 --title   # GitHub release title: the file's "# " heading, else "v1.2.2"
#   tools/release/release_notes.sh 1.2.2 --body    # GitHub release text (Markdown): the file without its heading
#   tools/release/release_notes.sh 1.2.2 --short   # one line for the store channel: the heading, else the first
#                                                  # changelog line (at most 900 characters)
#   tools/release/release_notes.sh 1.2.2 --check   # exit 1 when the version has neither
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
V="${1:-}"
V="${V#v}"
MODE="${2:---body}"
[[ "$V" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "usage: $0 X.Y.Z [--title|--body|--short|--check]" >&2; exit 2; }
FILE="$ROOT/docs/release-notes/v$V.md"
RE="^- \*\*v${V//./\\.}[:* ]"

changelog() { grep -E "$RE" "$ROOT/docs/PROTOCOL.md" || true; }
heading() { if [ -f "$FILE" ]; then head -1 "$FILE" | sed -n 's/^# \{1,\}//p'; fi; }

if [ ! -f "$FILE" ] && [ -z "$(changelog)" ]; then
  echo "no release notes for v$V: add docs/release-notes/v$V.md or a '- **v$V: ...**' changelog line in docs/PROTOCOL.md" >&2
  exit 1
fi

case "$MODE" in
  --check) exit 0 ;;
  --title)
    t="$(heading)"
    echo "${t:-v$V}" ;;
  --body)
    if [ -f "$FILE" ]; then
      if [ -n "$(heading)" ]; then tail -n +2 "$FILE"; else cat "$FILE"; fi | sed '/./,$!d'
    else
      printf 'Changes in v%s, from the [protocol changelog](https://github.com/%s/blob/v%s/docs/PROTOCOL.md):\n\n' \
        "$V" "${GITHUB_REPOSITORY:-NickStassen/tabletop-craft-ar}" "$V"
      changelog
    fi ;;
  --short)
    t="$(heading)"
    if [ -z "$t" ]; then t="$(changelog | head -1 | sed 's/^- //; s/\*\*//g')"; fi
    printf '%s\n' "$t" | cut -c1-900 ;;
  *) echo "unknown mode $MODE (--title, --body, --short, --check)" >&2; exit 2 ;;
esac
