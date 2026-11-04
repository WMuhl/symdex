#!/bin/sh
# Downloads a tree-sitter release tag and copies the files symdex vendors into
# grammars/<name>/. Does not edit grammars/GRAMMARS.md — update that manifest
# with the tag, commit SHA, and file list printed at the end.
#
# Usage: vendor-grammar.sh <core|c-sharp|typescript|python> <tag>
set -e

if [ $# -ne 2 ]; then
  echo "usage: vendor-grammar.sh <core|c-sharp|typescript|python> <tag>" >&2
  exit 1
fi

NAME="$1"
TAG="$2"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
GRAMMARS="$(cd "$SCRIPT_DIR/../grammars" && pwd)"

case "$NAME" in
  core)
    REPO="tree-sitter/tree-sitter"
    DEST="core"
    PATHS="LICENSE lib/include/tree_sitter/api.h lib/src"
    ;;
  c-sharp)
    REPO="tree-sitter/tree-sitter-c-sharp"
    DEST="tree-sitter-c-sharp"
    PATHS="LICENSE src/parser.c src/scanner.c src/node-types.json src/tree_sitter/parser.h src/tree_sitter/array.h src/tree_sitter/alloc.h"
    ;;
  typescript)
    REPO="tree-sitter/tree-sitter-typescript"
    DEST="tree-sitter-typescript"
    PATHS="LICENSE common/scanner.h typescript/src tsx/src"
    ;;
  python)
    REPO="tree-sitter/tree-sitter-python"
    DEST="tree-sitter-python"
    PATHS="LICENSE src/parser.c src/scanner.c src/node-types.json src/tree_sitter/parser.h src/tree_sitter/array.h src/tree_sitter/alloc.h"
    ;;
  *)
    echo "unknown grammar '$NAME'" >&2
    exit 1
    ;;
esac

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "symdex: downloading https://github.com/$REPO/archive/refs/tags/$TAG.tar.gz"
curl -fsSL "https://github.com/$REPO/archive/refs/tags/$TAG.tar.gz" -o "$WORK/grammar.tar.gz"
tar -xzf "$WORK/grammar.tar.gz" -C "$WORK"
set -- "$WORK"/*/
SRC="$1"
if [ ! -d "$SRC" ]; then
  echo "expected one top-level directory in the tag archive" >&2
  exit 1
fi

DEST_ROOT="$GRAMMARS/$DEST"
rm -rf "$DEST_ROOT"
mkdir -p "$DEST_ROOT"

for rel in $PATHS; do
  if [ ! -e "$SRC/$rel" ]; then
    echo "archive is missing $rel" >&2
    exit 1
  fi
  mkdir -p "$DEST_ROOT/$(dirname "$rel")"
  cp -R "$SRC/$rel" "$DEST_ROOT/$rel"
done

if [ "$NAME" = "core" ]; then
  rm -f \
    "$DEST_ROOT/lib/src/unicode/LICENSE" \
    "$DEST_ROOT/lib/src/unicode/README.md" \
    "$DEST_ROOT/lib/src/unicode/ICU_SHA"
fi

COMMIT="$(git ls-remote "https://github.com/$REPO.git" "refs/tags/${TAG}^{}" | awk '{print $1}')"
if [ -z "$COMMIT" ]; then
  COMMIT="$(git ls-remote "https://github.com/$REPO.git" "refs/tags/${TAG}" | awk '{print $1}')"
fi

echo ""
echo "symdex: copied files into grammars/$DEST"
echo "  repo:    https://github.com/$REPO"
echo "  tag:     $TAG"
echo "  commit:  ${COMMIT:-"(could not resolve)"}"
find "$DEST_ROOT" -type f | sed "s|^$DEST_ROOT/|    |"

if [ -f "$GRAMMARS/core/lib/include/tree_sitter/api.h" ]; then
  MAX="$(sed -n 's/^#define TREE_SITTER_LANGUAGE_VERSION \([0-9]*\).*/\1/p' "$GRAMMARS/core/lib/include/tree_sitter/api.h" | head -n 1)"
  MIN="$(sed -n 's/^#define TREE_SITTER_MIN_COMPATIBLE_LANGUAGE_VERSION \([0-9]*\).*/\1/p' "$GRAMMARS/core/lib/include/tree_sitter/api.h" | head -n 1)"
  echo "  core ABI: $MIN..$MAX"
  find "$DEST_ROOT" -name parser.c | while IFS= read -r parser; do
    VER="$(sed -n 's/^#define LANGUAGE_VERSION \([0-9]*\).*/\1/p' "$parser" | head -n 1)"
    REL="${parser#"$DEST_ROOT/"}"
    if [ -n "$VER" ] && [ -n "$MAX" ] && { [ "$VER" -gt "$MAX" ] || [ "$VER" -lt "$MIN" ]; }; then
      echo "  WARNING: LANGUAGE_VERSION $VER ($REL) is outside core ABI $MIN..$MAX"
    else
      echo "  LANGUAGE_VERSION $VER ($REL)"
    fi
  done
fi

echo ""
echo "symdex: update grammars/GRAMMARS.md with this tag, commit, and file list before committing."
