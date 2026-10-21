#!/usr/bin/env bash
# Compile and run the Unity package's C# sources and NUnit tests without Unity or .NET SDK.
#
# The authoring workstation has no Unity editor and no `dotnet` CLI. This script compiles
# the package against the netstandard2.1 reference pack (Unity 2022.3's exact API profile)
# with the Roslyn compiler shipped inside a locally installed SDK, then runs the package's
# real NUnit test sources through a tiny reflection runner.
#
# Requirements: a .NET SDK unpacked at .toolchain/dotnet (see docs/09-unity-package.md).
#
#   tools/verify_csharp.sh            # compile + run all tests
#   tools/verify_csharp.sh --compile  # compile only (fast syntax gate)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_ROOT="$ROOT/.toolchain/dotnet"
CSC="$DOTNET_ROOT/sdk/8.0.426/Roslyn/bincore/csc.dll"
REF="$DOTNET_ROOT/packs/NETStandard.Library.Ref/2.1.0/ref/netstandard2.1"
BUILD="$ROOT/.toolchain/build"
PKG="$ROOT/Packages/com.auction.voice"
HEADLESS="$ROOT/tools/csharp-headless"

if [[ ! -f "$CSC" ]]; then
    echo "error: .NET SDK not found at $DOTNET_ROOT" >&2
    echo "Install it with:" >&2
    echo "  mkdir -p .toolchain && cd .toolchain" >&2
    echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir \"\$PWD/dotnet\" --no-path" >&2
    exit 2
fi

export HOME="${HOME_OVERRIDE:-$ROOT/.toolchain/home}"
mkdir -p "$HOME" "$BUILD"

refs=()
for dll in "$REF"/*.dll; do
    refs+=("-r:$dll")
done

sources=()
while IFS= read -r file; do
    sources+=("$file")
done < <(find "$PKG/Runtime" "$PKG/Samples~" -name '*.cs' | sort)

# The package's Unity-facing files need engine types. Compile them against the stubs so
# syntax and typing errors are caught here rather than in the Unity editor.
echo "== compiling package (netstandard2.1, C# 9, Unity stubs) =="
"$DOTNET_ROOT/dotnet" exec "$CSC" \
    -nologo -langversion:9.0 -target:library -warn:4 \
    -define:AUCTION_VOICE_INPUT_SYSTEM \
    -out:"$BUILD/AuctionVoice.dll" -nostdlib+ \
    "${refs[@]}" "$HEADLESS/UnityStubs.cs" "${sources[@]}"

if [[ "${1:-}" == "--compile" ]]; then
    echo "ok: $BUILD/AuctionVoice.dll"
    exit 0
fi

echo "== compiling tests + runner =="
test_sources=()
while IFS= read -r file; do
    test_sources+=("$file")
done < <(find "$PKG/Tests/Editor" "$HEADLESS" -name '*.cs' ! -name 'AsrProbe.cs' | sort)

"$DOTNET_ROOT/dotnet" exec "$CSC" \
    -nologo -langversion:9.0 -target:exe -warn:0 \
    -out:"$BUILD/AuctionVoice.Tests.dll" -nostdlib+ \
    "${refs[@]}" \
    -r:"$BUILD/AuctionVoice.dll" \
    "${test_sources[@]}"

RUNTIME="$DOTNET_ROOT/shared/Microsoft.NETCore.App/8.0.32"
if [[ ! -d "$RUNTIME" ]]; then
    echo "error: .NET runtime not found at $RUNTIME" >&2
    exit 2
fi

cat > "$BUILD/AuctionVoice.Tests.runtimeconfig.json" <<JSON
{
  "runtimeOptions": {
    "tfm": "net8.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" }
  }
}
JSON

echo "== running tests =="
# `dotnet exec` needs a managed entry assembly; the CLI's own apphost works for that.
"$DOTNET_ROOT/dotnet" exec "$BUILD/AuctionVoice.Tests.dll" "$@"
