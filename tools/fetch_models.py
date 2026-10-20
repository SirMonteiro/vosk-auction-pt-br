"""Fetch Vosk models into ``models/`` and record provenance.

The demo never downloads at server startup: an explicit step keeps offline runs
working and makes the first-run failure mode obvious.

Usage::

    uv run python -m tools.fetch_models small
    uv run python -m tools.fetch_models pruned --force
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
import urllib.request
import zipfile
from datetime import UTC, datetime
from pathlib import Path

from auction_demo.config import PROJECT_ROOT

#: Model catalogue. ``md5`` values come from the official model list and are
#: verified after download; a mismatch aborts and deletes the archive.
MODELS: dict[str, dict[str, str | int | bool]] = {
    "small": {
        "name": "vosk-model-small-pt-0.3",
        "url": "https://alphacephei.com/vosk/models/vosk-model-small-pt-0.3.zip",
        "md5": "458c69371c5a0b9ab6ee8fa417bf89da",
        "size_bytes": 32_453_112,
        "dynamic_graph": True,
    },
    "pruned": {
        "name": "vosk-model-pt-fb-v0.1.1-pruned",
        "url": "https://alphacephei.com/vosk/models/vosk-model-pt-fb-v0.1.1-pruned.zip",
        "md5": "1623ac7ab970fc234a56dd1782261300",
        "size_bytes": 1_223_922_169,
        "dynamic_graph": False,
    },
}

MODELS_DIR = PROJECT_ROOT / "models"


def _md5(path: Path, chunk_size: int = 1 << 20) -> str:
    digest = hashlib.md5()  # noqa: S324 - verifying a published checksum, not security
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(chunk_size), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _download(url: str, destination: Path) -> None:
    """Download with a throttled progress line on stderr (one update per 5%)."""
    state = {"last": -1}

    def hook(block_num: int, block_size: int, total_size: int) -> None:
        if total_size <= 0:
            return
        downloaded = min(block_num * block_size, total_size)
        percent = int(downloaded * 100 / total_size)
        if percent // 5 != state["last"]:
            state["last"] = percent // 5
            sys.stderr.write(f"\r  {percent:3d}%  {downloaded / 1e6:7.1f} MB")
            sys.stderr.flush()

    urllib.request.urlretrieve(url, destination, reporthook=hook)  # noqa: S310 - fixed https URL
    sys.stderr.write("\n")


def fetch(key: str, force: bool = False) -> Path:
    """Download, verify and unpack one model. Returns its directory."""
    if key not in MODELS:
        raise SystemExit(f"Unknown model {key!r}. Available: {', '.join(MODELS)}")

    spec = MODELS[key]
    name = str(spec["name"])
    target = MODELS_DIR / key

    if target.is_dir() and not force:
        print(f"{name} already present at {target}")
        return target

    if target.exists() and force:
        shutil.rmtree(target)

    MODELS_DIR.mkdir(parents=True, exist_ok=True)
    archive = MODELS_DIR / f"{key}.zip"

    print(f"Downloading {name} ({int(spec['size_bytes']) / 1e6:.0f} MB)...")
    _download(str(spec["url"]), archive)

    actual = _md5(archive)
    if actual != spec["md5"]:
        archive.unlink(missing_ok=True)
        raise SystemExit(f"md5 mismatch for {name}: expected {spec['md5']}, got {actual}")

    print("Checksum OK. Unpacking...")
    with zipfile.ZipFile(archive) as bundle:
        bundle.extractall(MODELS_DIR)
    archive.unlink()

    unpacked = MODELS_DIR / name
    if unpacked != target:
        unpacked.rename(target)

    manifest = {
        "name": name,
        "url": spec["url"],
        "md5": spec["md5"],
        "size_bytes": spec["size_bytes"],
        "dynamic_graph": spec["dynamic_graph"],
        "fetched_at": datetime.now(UTC).isoformat(timespec="seconds"),
    }
    (target / "manifest.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    print(f"Ready: {target}")
    return target


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Fetch Vosk pt-BR models for the demo")
    parser.add_argument("models", nargs="*", default=["small"], help="small, pruned")
    parser.add_argument("--force", action="store_true", help="re-download even if present")
    args = parser.parse_args(argv)

    for key in args.models:
        fetch(key, force=args.force)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
