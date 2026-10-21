#!/usr/bin/env bash
# Drive the package's real VoskSpeechEngine over WAV files, without Unity or a microphone.
#
# This isolates capture bugs from ASR bugs: if the engine decodes the same WAV that the
# microphone path produced silence for, the fault is in capture; if it also decodes nothing,
# the fault is in the engine.
#
#   tools/check_csharp_asr.sh <modelDir> <wav> [wav...]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_ROOT="$ROOT/.toolchain/dotnet"
CSC="$DOTNET_ROOT/sdk/8.0.426/Roslyn/bincore/csc.dll"
REF="$DOTNET_ROOT/packs/NETStandard.Library.Ref/2.1.0/ref/netstandard2.1"
BUILD="$ROOT/.toolchain/build/asr"
PKG="$ROOT/Packages/com.auction.voice"

export HOME="${HOME_OVERRIDE:-$ROOT/.toolchain/home}"
mkdir -p "$BUILD"

refs=()
for dll in "$REF"/*.dll; do
    refs+=("-r:$dll")
done

sources=()
while IFS= read -r file; do
    sources+=("$file")
done < <(find "$PKG/Runtime/Core" "$PKG/Runtime/NLU" "$PKG/Runtime/ASR" "$PKG/Runtime/Audio/PcmAudio.cs" -name '*.cs' | sort)
sources+=("$PKG/Runtime/Diagnostics/IAuctionLogger.cs")

"$DOTNET_ROOT/dotnet" exec "$CSC" \
    -nologo -langversion:9.0 -target:exe -warn:0 \
    -out:"$BUILD/AsrProbe.dll" -nostdlib+ \
    "${refs[@]}" "${sources[@]}" "$ROOT/tools/csharp-headless/AsrProbe.cs"

cat > "$BUILD/AsrProbe.runtimeconfig.json" <<JSON
{
  "runtimeOptions": {
    "tfm": "net8.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" }
  }
}
JSON

cp -f "$ROOT"/.venv/lib/python3.12/site-packages/vosk/libvosk.so "$BUILD/" 2>/dev/null || true
LD_LIBRARY_PATH="$BUILD:${LD_LIBRARY_PATH:-}" "$DOTNET_ROOT/dotnet" exec "$BUILD/AsrProbe.dll" "$@"
