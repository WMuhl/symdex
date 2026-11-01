#!/usr/bin/env bash
# Installs a locally packed symdex nupkg and checks that the Roslyn feeder and the
# tree-sitter native library both work from the dotnet tool layout.
set -eu

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

script_dir=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
root=$(CDPATH= cd -- "$script_dir/.." && pwd)

fail() {
  echo "smoke: $*" >&2
  exit 1
}

nupkg=${1:-}
if [ -z "$nupkg" ]; then
  newest=""
  for dir in "$root/artifacts/package" "$root/src/Symdex.Cli/bin/Release"; do
    if [ -d "$dir" ]; then
      for candidate in "$dir"/symdex.*.nupkg; do
        [ -f "$candidate" ] || continue
        case "$candidate" in
          *.symbols.nupkg) continue ;;
        esac
        if [ -z "$newest" ] || [ "$candidate" -nt "$newest" ]; then
          newest=$candidate
        fi
      done
    fi
  done
  nupkg=$newest
fi
[ -n "$nupkg" ] && [ -f "$nupkg" ] || fail "no symdex nupkg found; pass a path or pack into artifacts/package"
nupkg=$(CDPATH= cd -- "$(dirname "$nupkg")" && pwd)/$(basename "$nupkg")

base=$(basename "$nupkg")
case "$base" in
  symdex.*.nupkg) ;;
  *) fail "expected symdex.<version>.nupkg, got $base" ;;
esac
version=${base#symdex.}
version=${version%.nupkg}
source_dir=$(CDPATH= cd -- "$(dirname "$nupkg")" && pwd)

case "$(uname -m)" in
  x86_64|amd64) rid_arch=x64 ;;
  aarch64|arm64) rid_arch=arm64 ;;
  *) fail "unsupported architecture $(uname -m)" ;;
esac
case "$(uname -s)" in
  Linux)
    native_name=libsymdex_treesitter.so
    rid="linux-$rid_arch"
    ;;
  Darwin)
    native_name=libsymdex_treesitter.dylib
    rid="osx-$rid_arch"
    ;;
  *) fail "unsupported OS $(uname -s)" ;;
esac

work=$(mktemp -d)
cleanup() {
  rm -rf "$work"
}
trap cleanup EXIT

cat > "$work/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$work/packages" />
  </config>
  <packageSources>
    <clear />
    <add key="local" value="$source_dir" />
  </packageSources>
</configuration>
EOF

echo "smoke: installing symdex $version from $nupkg"
dotnet tool install symdex --tool-path "$work/tool" --configfile "$work/nuget.config" --version "$version"
exe="$work/tool/symdex"
[ -x "$exe" ] || fail "symdex was not installed into $work/tool"

native=$(find "$work/tool" -type f -path "*/runtimes/$rid/native/$native_name" | head -n 1)
[ -n "$native" ] || fail "$rid native library $native_name missing from the tool install"
echo "smoke: native $native"

cp -a "$root/tests/fixtures/sample-repo" "$work/sample-repo"
cp -a "$root/tests/fixtures/python-sample" "$work/python-sample"

echo "smoke: restoring sample repo"
dotnet restore "$work/sample-repo/SampleRepo.sln" --verbosity minimal

stdout="$work/stdout.txt"
stderr="$work/stderr.txt"

run_symdex() {
  set +e
  "$exe" "$@" >"$stdout" 2>"$stderr"
  code=$?
  set -e
  if [ "$code" -ne 0 ]; then
    echo "smoke: command failed ($code): $*" >&2
    echo "STDOUT:" >&2
    cat "$stdout" >&2
    echo "STDERR:" >&2
    cat "$stderr" >&2
    exit 1
  fi
  if grep -q 'symdex:' "$stderr"; then
    echo "smoke: symdex warning (Roslyn or native fallback) from: $*" >&2
    cat "$stderr" >&2
    echo "STDOUT:" >&2
    cat "$stdout" >&2
    exit 1
  fi
}

echo "smoke: refs IPaymentProcessor"
run_symdex refs IPaymentProcessor --json --repo "$work/sample-repo"
if ! grep -E '"kind"[[:space:]]*:[[:space:]]*"implementation"' "$stdout" | grep -q 'SampleLib/PaymentProcessor\.cs'; then
  echo "smoke: refs JSON has no Roslyn implementation of IPaymentProcessor" >&2
  cat "$stdout" >&2
  echo "STDERR:" >&2
  cat "$stderr" >&2
  exit 1
fi
cat "$stdout"
if [ -s "$stderr" ]; then
  cat "$stderr"
fi

echo "smoke: outline math.ts"
run_symdex outline "$work/sample-repo/ts/math.ts" --repo "$work/sample-repo"
grep -q 'add' "$stdout" || fail "outline of math.ts did not contain add"
cat "$stdout"

echo "smoke: outline calc.py"
run_symdex outline "$work/python-sample/calc.py" --repo "$work/python-sample"
grep -q 'Calculator' "$stdout" || fail "outline of calc.py did not contain Calculator"
cat "$stdout"

echo "smoke: ok"
