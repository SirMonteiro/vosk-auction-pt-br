"""Rebuild a Vosk model's ``Gr.fst`` language model from a corpus.

The pipeline is the one documented by Vosk and verified on this machine::

    fstsymbols --save_osymbols=words.txt Gr.fst
    farcompilestrings --fst_type=compact --symbols=words.txt --keep_symbols text.txt \\
      | ngramcount --order=3 | ngrammake --method=kneser_ney \\
      | fstconvert --fst_type=ngram > Gr.new.fst

The model is copied, the original ``Gr.fst`` preserved as ``Gr.fst.base``, and a
``manifest.json`` records provenance so the rebuild can be reproduced or rolled
back. ``--dry-run`` stops before writing anything.

Usage::

    uv run python -m tools.build_graph --base small --out small-lm-v1
    uv run python -m tools.build_graph --base small --out small-lm-v1 --dry-run
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tempfile
from datetime import UTC, datetime
from pathlib import Path

from auction_demo.config import PROJECT_ROOT

TOOLS = ("fstsymbols", "farcompilestrings", "ngramcount", "ngrammake", "fstconvert")

#: OpenFst plugin directories that must be on LD_LIBRARY_PATH.
#: `lib` holds libfstfarscript (needed by farcompilestrings); `lib/fst` holds
#: ngram-fst.so, the plugin that reads `Gr.fst`'s "ngram" FST type.
#: Missing either produces a *misleading* error: "Bad label integer = a" or
#: "Unknown FST type ngram". The tool sets these itself rather than trusting the
#: caller's environment.
PLUGIN_LIB_DIRS = ("lib", "lib/fst")


def _toolchain_env() -> dict[str, str]:
    """Environment with the OpenFst plugin directories on LD_LIBRARY_PATH.

    Resolution order: the VOSK_TOOLCHAIN env var, the repo's ``.toolchain``
    directory (created by bootstrap_kaldi.sh), then the vendored copy under
    ``artifacts/``.

    ``uv run`` replaces PATH with the virtualenv's, so the ambient PATH cannot be
    relied upon for locating the binaries.
    """
    env = dict(os.environ)

    override = env.get("VOSK_TOOLCHAIN")
    candidates = [Path(override)] if override else []
    candidates.append(PROJECT_ROOT / ".toolchain")
    candidates.append(PROJECT_ROOT / "artifacts" / "of184" / "inst")

    for prefix in candidates:
        lib_dirs = [str(prefix / sub) for sub in PLUGIN_LIB_DIRS if (prefix / sub).is_dir()]
        if not lib_dirs:
            continue
        current = env.get("LD_LIBRARY_PATH", "")
        env["LD_LIBRARY_PATH"] = ":".join(lib_dirs + ([current] if current else []))

        bindir = prefix / "bin"
        if bindir.is_dir() and str(bindir) not in env.get("PATH", "").split(":"):
            env["PATH"] = f"{bindir}:{env.get('PATH', '')}"
        return env

    return env


def require_toolchain() -> None:
    """Verify the toolchain is reachable, using the environment this tool sets."""
    env = _toolchain_env()
    missing = [tool for tool in TOOLS if shutil.which(tool, path=env.get("PATH")) is None]
    if missing:
        raise SystemExit(
            "missing toolchain binaries: " + ", ".join(missing) + "\n"
            "Run tools/bootstrap_kaldi.sh (builds into .toolchain), or set "
            "VOSK_TOOLCHAIN to an existing openfst+opengrm install."
        )


def sha256(path: Path, chunk: int = 1 << 20) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(chunk), b""):
            digest.update(block)
    return digest.hexdigest()


def run(command: list[str], **kwargs) -> subprocess.CompletedProcess:
    """Run a toolchain command with the OpenFst plugin path preloaded."""
    kwargs.setdefault("capture_output", True)
    kwargs.setdefault("env", _toolchain_env())
    result = subprocess.run(command, **kwargs)
    if result.returncode != 0:
        raise SystemExit(
            f"command failed ({result.returncode}): {' '.join(command)}\n{result.stderr[-2000:]}"
        )
    return result


def build_graph(corpus: Path, source_gr_fst: Path, destination: Path, order: int = 3) -> None:
    """Compile ``corpus`` into an n-gram FST written to ``destination``.

    Symbols are read from ``source_gr_fst`` (the untouched base model) so the
    destination copy may already have its ``Gr.fst`` renamed aside.

    ``farcompilestrings`` requires the corpus as a **file argument**: feeding the
    same data on stdin fails with "Read from a file instead of stdin".
    """
    with tempfile.TemporaryDirectory() as tmp:
        raw_symbols = Path(tmp) / "words-raw.txt"
        run(["fstsymbols", f"--save_osymbols={raw_symbols}", str(source_gr_fst)])

        # ``fstsymbols`` emits special entries (<eps>, !SIL, [unk]) whose labels
        # ``farcompilestrings`` cannot map back. They are removed from the
        # symbol table handed to it.
        symbols = Path(tmp) / "words.txt"
        skipped = {"<eps>", "!SIL", "[unk]"}
        kept = [
            line
            for line in raw_symbols.read_text(encoding="utf-8").splitlines()
            if line.split("\t")[0] not in skipped
        ]
        if not kept:
            raise SystemExit("no usable symbols found in the base Gr.fst")
        symbols.write_text("\n".join(kept) + "\n", encoding="utf-8")

        # `--keep_symbols` is a BOOLEAN flag taking no value: passing the corpus
        # after it as a separate argument silently eats it as the flag's value,
        # and the output path is then read as the corpus. Every stage reads and
        # writes files given positionally; stdin requires --generate_keys.
        compact = Path(tmp) / "compact.far"
        run(
            [
                "farcompilestrings",
                "--fst_type=compact",
                f"--symbols={symbols}",
                "--keep_symbols=true",
                str(corpus),
                str(compact),
            ]
        )
        counts = Path(tmp) / "counts.fst"
        run(["ngramcount", f"--order={order}", str(compact), str(counts)])
        model = Path(tmp) / "model.fst"
        run(["ngrammake", "--method=kneser_ney", str(counts), str(model)])
        run(["fstconvert", "--fst_type=ngram", str(model), str(destination)])


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Rebuild a Vosk model's language model")
    parser.add_argument("--base", default="small", help="source model directory under models/")
    parser.add_argument("--out", default="small-lm-v1", help="destination model name")
    parser.add_argument("--corpus", default="data/lm/text.txt", help="LM corpus")
    parser.add_argument("--order", type=int, default=3, help="n-gram order")
    parser.add_argument("--dry-run", action="store_true", help="build the FST but do not install it")
    args = parser.parse_args(argv)

    require_toolchain()

    base = PROJECT_ROOT / "models" / args.base
    target = PROJECT_ROOT / "models" / args.out
    corpus = PROJECT_ROOT / args.corpus

    if not base.is_dir():
        raise SystemExit(f"base model not found: {base}")
    if not corpus.is_file():
        raise SystemExit(
            f"corpus not found: {corpus}\n"
            "Build it with: uv run python -m tools.build_lm_corpus --out data/lm/text.txt"
        )
    if target.exists():
        raise SystemExit(f"refusing to overwrite {target}; delete it first")

    print(f"base   {base}")
    print(f"out    {target}")
    print(f"corpus {corpus} ({corpus.stat().st_size / 1e6:.1f} MB)")

    if args.dry_run:
        print("dry run: stopping before any file is written")
        return 0

    shutil.copytree(base, target)
    shutil.move(str(target / "Gr.fst"), str(target / "Gr.fst.base"))

    print("compiling FST...")
    build_graph(corpus, base / "Gr.fst", target / "Gr.fst", order=args.order)

    manifest = {
        "name": args.out,
        "base": args.base,
        "base_Gr_fst_bytes": (target / "Gr.fst.base").stat().st_size,
        "Gr_fst_bytes": (target / "Gr.fst").stat().st_size,
        "corpus": args.corpus,
        "corpus_sha256": sha256(corpus),
        "ngram_order": args.order,
        "method": "kneser_ney",
        "built_at": datetime.now(UTC).isoformat(timespec="seconds"),
    }
    (target / "manifest.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )

    print(f"\nGr.fst {manifest['base_Gr_fst_bytes']} -> {manifest['Gr_fst_bytes']} bytes")
    print(f"manifest: {target / 'manifest.json'}")
    print(f"\nEvaluate it with: uv run python tools/test_e2e.py --model {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
