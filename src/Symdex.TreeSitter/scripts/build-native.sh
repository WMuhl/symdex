#!/bin/sh
# Compiles the vendored tree-sitter runtime + grammars (grammars/) into a single shared
# library named libsymdex_treesitter.{so,dylib}. Invoked as an MSBuild pre-build step on
# Linux/macOS; safe to re-run (skips work when nothing changed, see the .csproj target).
#
# Usage: build-native.sh <output-dir>
set -e

OUT_DIR="$1"
if [ -z "$OUT_DIR" ]; then
  echo "usage: build-native.sh <output-dir>" >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
GRAMMARS="$SCRIPT_DIR/../grammars"

CC="${CC:-}"
if [ -z "$CC" ] || ! command -v "$CC" >/dev/null 2>&1; then
  if command -v cc >/dev/null 2>&1; then
    CC=cc
  elif command -v clang >/dev/null 2>&1; then
    CC=clang
  elif command -v gcc >/dev/null 2>&1; then
    CC=gcc
  else
    echo "symdex: no C compiler found (cc/clang/gcc); skipping tree-sitter native build." >&2
    exit 0
  fi
fi

case "$(uname -s)" in
  Darwin) EXT=dylib; SHARED_FLAG=-dynamiclib ;;
  *) EXT=so; SHARED_FLAG=-shared ;;
esac

WORK_DIR=$(mktemp -d)
trap 'rm -rf "$WORK_DIR"' EXIT

OBJS=""
INDEX=0
compile() {
  src="$1"; shift
  INDEX=$((INDEX + 1))
  obj="$WORK_DIR/obj_$INDEX.o"
  "$CC" -std=c11 -O1 -fPIC -w -c "$src" "$@" -o "$obj"
  OBJS="$OBJS $obj"
}

compile "$GRAMMARS/core/lib/src/lib.c" \
  -I"$GRAMMARS/core/lib/src" -I"$GRAMMARS/core/lib/src/wasm" -I"$GRAMMARS/core/lib/include"

compile "$GRAMMARS/tree-sitter-c-sharp/src/parser.c" -I"$GRAMMARS/tree-sitter-c-sharp/src"
compile "$GRAMMARS/tree-sitter-c-sharp/src/scanner.c" -I"$GRAMMARS/tree-sitter-c-sharp/src"

compile "$GRAMMARS/tree-sitter-typescript/typescript/src/parser.c" -I"$GRAMMARS/tree-sitter-typescript/typescript/src"
compile "$GRAMMARS/tree-sitter-typescript/typescript/src/scanner.c" -I"$GRAMMARS/tree-sitter-typescript/typescript/src"

compile "$GRAMMARS/tree-sitter-typescript/tsx/src/parser.c" -I"$GRAMMARS/tree-sitter-typescript/tsx/src"
compile "$GRAMMARS/tree-sitter-typescript/tsx/src/scanner.c" -I"$GRAMMARS/tree-sitter-typescript/tsx/src"

mkdir -p "$OUT_DIR"
# shellcheck disable=SC2086
"$CC" $SHARED_FLAG -o "$OUT_DIR/libsymdex_treesitter.$EXT" $OBJS

echo "symdex: built native/libsymdex_treesitter.$EXT"
