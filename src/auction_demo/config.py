"""Runtime configuration.

All tunables live here so the demo can be reproduced from a single dataclass.
Values come from the measured plan in ``docs/04-asr-and-grammar.md``; change them
only together with a measurement in ``docs/08-measurements-appendix.md``.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path

# Repository root: <root>/src/auction_demo/config.py -> <root>
PROJECT_ROOT = Path(__file__).resolve().parents[2]


def _env_str(name: str, default: str) -> str:
    return os.environ.get(name, default)


def _env_int(name: str, default: int) -> int:
    raw = os.environ.get(name)
    return default if raw is None else int(raw)


def _env_bool(name: str, default: bool) -> bool:
    raw = os.environ.get(name)
    if raw is None:
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}


@dataclass(slots=True)
class Settings:
    """Immutable-ish configuration bundle shared by the server and the tools."""

    # --- model selection -------------------------------------------------
    # "small" | "small-lm-v1" | "pruned" | absolute path to a model directory
    model: str = field(default_factory=lambda: _env_str("AUCTION_MODEL", "small-lm-v1"))
    models_dir: Path = field(default_factory=lambda: PROJECT_ROOT / "models")

    # --- audio -----------------------------------------------------------
    sample_rate: int = 16_000
    frame_ms: int = 100
    pre_roll_ms: int = 250
    lead_silence_ms: int = 300
    tail_silence_ms: int = 400
    min_utterance_ms: int = 250
    min_rms: float = 0.004

    # --- decoding --------------------------------------------------------
    use_grammar: bool = field(default_factory=lambda: _env_bool("AUCTION_GRAMMAR", True))
    # Curated default: 6 000 phrases fits every priority layer (all 0-999 and
    # "N mil" forms, plus frames, idioms and intents) at 82 KB / 11 ms worst
    # chunk. The measured ceiling is 15 000.
    grammar_max_phrases: int = 6_000
    # Hard ceiling: above this the decoder latency collapses without accuracy gain.
    grammar_hard_limit: int = 15_000
    nbest: int = field(default_factory=lambda: _env_int("AUCTION_NBEST", 5))

    # --- server ----------------------------------------------------------
    host: str = field(default_factory=lambda: _env_str("AUCTION_HOST", "127.0.0.1"))
    port: int = field(default_factory=lambda: _env_int("AUCTION_PORT", 5000))

    # --- misc ------------------------------------------------------------
    debug: bool = field(default_factory=lambda: _env_bool("AUCTION_DEBUG", False))
    quiet: bool = field(default_factory=lambda: _env_bool("AUCTION_QUIET", False))

    # ------------------------------------------------------------------
    def resolve_model_path(self) -> Path:
        """Return the model directory for ``self.model``.

        Accepts a bare name resolved under ``models_dir`` or an absolute/relative
        path. Raises ``FileNotFoundError`` with an actionable message because the
        most common first-run failure is a missing model download.
        """
        candidate = Path(self.model).expanduser()
        if candidate.is_absolute() or os.sep in self.model:
            path = candidate
        else:
            path = self.models_dir / self.model

        if not path.is_dir():
            raise FileNotFoundError(
                f"Vosk model not found at {path}.\n"
                f"Download it with: uv run python -m tools.fetch_models {self.model}"
            )
        return path

    def frame_bytes(self) -> int:
        """Bytes per 100 ms frame of 16-bit mono PCM."""
        return int(self.sample_rate * self.frame_ms / 1000) * 2

    def pre_roll_bytes(self) -> int:
        return int(self.sample_rate * self.pre_roll_ms / 1000) * 2

    def lead_silence_bytes(self) -> int:
        return int(self.sample_rate * self.lead_silence_ms / 1000) * 2

    def tail_silence_bytes(self) -> int:
        return int(self.sample_rate * self.tail_silence_ms / 1000) * 2


def load_settings(**overrides) -> Settings:
    """Build a ``Settings`` instance, applying explicit overrides last."""
    settings = Settings()
    for key, value in overrides.items():
        if not hasattr(settings, key):
            raise TypeError(f"Unknown setting: {key}")
        setattr(settings, key, value)
    return settings
