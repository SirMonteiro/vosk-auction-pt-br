#!/usr/bin/env bash
# Bootstrap the OpenGrm/OpenFST toolchain used to rebuild a Vosk language model.
#
# Only these versions compile on this machine (GCC 16):
#   openfst      1.8.4   (1.8.3 and 1.7.9 fail on std::unique_ptr)
#   opengrm      1.3.17  (1.3.4 and 1.3.7 use FLAGS_* and -std=c++11)
#
# Everything lands in auction-vosk-demo/.toolchain so the build survives reboots
# (/tmp is tmpfs on this machine) and never needs root.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLCHAIN="$ROOT/.toolchain"
OPENFST_VERSION=1.8.4
OPENGRM_VERSION=1.3.17
JOBS="${JOBS:-$(nproc)}"

CXXFLAGS="-O2 -std=c++17"

if [ -x "$TOOLCHAIN/bin/ngramcount" ] && [ -x "$TOOLCHAIN/bin/fstsymbols" ]; then
  echo "toolchain already present at $TOOLCHAIN"
  exit 0
fi

mkdir -p "$TOOLCHAIN/src"
cd "$TOOLCHAIN/src"

echo "==> openfst $OPENFST_VERSION"
curl -sL -o "openfst-$OPENFST_VERSION.tar.gz" \
  "https://www.openfst.org/twiki/pub/FST/FstDownload/openfst-$OPENFST_VERSION.tar.gz"
rm -rf "openfst-$OPENFST_VERSION"
tar xzf "openfst-$OPENFST_VERSION.tar.gz"
cd "openfst-$OPENFST_VERSION"
  ./configure --prefix="$TOOLCHAIN" --enable-far --enable-ngram-fsts CXXFLAGS="$CXXFLAGS"
  make -j"$JOBS"
  make install
cd ..

echo "==> opengrm-ngram $OPENGRM_VERSION"
curl -sL -o "ngram-$OPENGRM_VERSION.tar.gz" \
  "https://www.opengrm.org/twiki/pub/GRM/NGramDownload/ngram-$OPENGRM_VERSION.tar.gz"
rm -rf "ngram-$OPENGRM_VERSION"
tar xzf "ngram-$OPENGRM_VERSION.tar.gz"
cd "ngram-$OPENGRM_VERSION"
  CXXFLAGS="$CXXFLAGS" \
    CPPFLAGS="-I$TOOLCHAIN/include" \
    LDFLAGS="-L$TOOLCHAIN/lib" \
    ./configure --prefix="$TOOLCHAIN"
  make -j"$JOBS"
  make install
cd ..

echo
echo "toolchain ready: $TOOLCHAIN"
echo "export PATH=$TOOLCHAIN/bin:\$PATH"
echo "export LD_LIBRARY_PATH=$TOOLCHAIN/lib:$TOOLCHAIN/lib/fst"
