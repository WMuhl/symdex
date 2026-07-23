#!/bin/sh
# Triggers the first index build for the current repo. The index self-manages after this -
# every symdex verb re-hashes changed files and refreshes the database inline - so this script
# only needs to run once (e.g. right after the skill is installed) and is safe to re-run.
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SKILL_DIR="$(dirname "$SCRIPT_DIR")"
SKILL_PARENT="$(dirname "$SKILL_DIR")"

find_binary() {
  for candidate in "$SKILL_PARENT/symdex" "$SKILL_PARENT/symdex.exe" "$SKILL_DIR/symdex" "$SKILL_DIR/symdex.exe"; do
    if [ -x "$candidate" ]; then
      echo "$candidate"
      return 0
    fi
  done

  if command -v symdex >/dev/null 2>&1; then
    command -v symdex
    return 0
  fi

  return 1
}

SYMDEX_BIN="$(find_binary)" || {
  echo "ensure-index: could not find the symdex binary next to the skill or on PATH; skipping." >&2
  exit 0
}

"$SYMDEX_BIN" find --limit 1 x >/dev/null 2>&1 || true

exit 0
