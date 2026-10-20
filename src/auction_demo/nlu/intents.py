"""Intent detection.

Order is fixed and matters: ``dou-lhe uma`` must be recognised as the
auctioneer's chant before ``uma`` can become a bid, and ``sem lance`` must be a
PASS before any phonetic repair can turn ``sem`` into ``cem``.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

from auction_demo.nlu.lexicon import (
    CHANT_PHRASES,
    CORRECT_PHRASES,
    DELTA_MARKERS,
    FILLERS,
    MULTIPLIER_WORDS,
    PASS_PHRASES,
    QUERY_PHRASES,
)
from auction_demo.nlu.numbers import strip_accents


@dataclass(slots=True)
class Intent:
    """A detected intent plus the span that triggered it."""

    name: str
    matched: str
    remainder: str
    multiplier: float | None = None


def normalize(text: str) -> str:
    """Lowercase, collapse whitespace, normalise hyphens, keep digits."""
    lowered = text.replace("-", " ").replace("—", " ").strip().lower()
    return re.sub(r"\s+", " ", lowered)


def _phrase_pattern(phrases: frozenset[str]) -> re.Pattern[str]:
    """Compile an alternation, longest first so ``sem lance`` beats ``lance``."""
    ordered = sorted(phrases, key=len, reverse=True)
    return re.compile(r"(?<!\w)(" + "|".join(re.escape(p) for p in ordered) + r")(?!\w)")


_PASS_RE = _phrase_pattern(PASS_PHRASES)
_CORRECT_RE = _phrase_pattern(CORRECT_PHRASES)
_QUERY_RE = _phrase_pattern(QUERY_PHRASES)
_CHANT_RE = _phrase_pattern(CHANT_PHRASES)
_FILLER_RE = _phrase_pattern(FILLERS)

_LOT_RE = re.compile(
    r"(?<!\w)(?:lote|item)\s+(\d+|um|uma|dois|duas|três|tres|quatro|cinco|seis|sete|oito|nove|dez)(?!\w)"
)
_ORDINAL_LOT_RE = re.compile(r"(?<!\w)(primeiro|segundo|terceiro|quarto|quinto)\s+lote(?!\w)")

_ORDINALS = {"primeiro": 1, "segundo": 2, "terceiro": 3, "quarto": 4, "quinto": 5}
_WORD_NUMBERS = {
    "um": 1,
    "uma": 1,
    "dois": 2,
    "duas": 2,
    "três": 3,
    "tres": 3,
    "quatro": 4,
    "cinco": 5,
    "seis": 6,
    "sete": 7,
    "oito": 8,
    "nove": 9,
    "dez": 10,
}

_MULTIPLIER_RE = re.compile(
    r"(?<!\w)(" + "|".join(sorted(MULTIPLIER_WORDS, key=len, reverse=True)) + r")(?!\w)"
)


def _strip_match(text: str, match: re.Match[str]) -> str:
    return (text[: match.start()] + " " + text[match.end() :]).strip()


def detect(text: str) -> Intent:
    """Detect the highest-priority intent in ``text``.

    Returns ``Intent("BID", "", text)`` when nothing else matches, leaving the
    numeric work to the parser.
    """
    normalized = normalize(text)

    # 1. CHANT ------------------------------------------------------------
    match = _CHANT_RE.search(normalized)
    if match:
        return Intent("CHANT", match.group(1), _strip_match(normalized, match))

    # 2. CORRECT ----------------------------------------------------------
    match = _CORRECT_RE.search(normalized)
    if match:
        return Intent("CORRECT", match.group(1), _strip_match(normalized, match))

    # 3. PASS -------------------------------------------------------------
    match = _PASS_RE.search(normalized)
    if match:
        return Intent("PASS", match.group(1), _strip_match(normalized, match))

    # 4. QUERY ------------------------------------------------------------
    match = _QUERY_RE.search(normalized)
    if match:
        return Intent("QUERY", match.group(1), _strip_match(normalized, match))

    # 5. LOT --------------------------------------------------------------
    match = _LOT_RE.search(normalized)
    if match:
        token = match.group(1)
        number = int(token) if token.isdigit() else _WORD_NUMBERS.get(token, 0)
        return Intent("LOT", match.group(0), _strip_match(normalized, match), multiplier=float(number))

    match = _ORDINAL_LOT_RE.search(normalized)
    if match:
        return Intent(
            "LOT",
            match.group(0),
            _strip_match(normalized, match),
            multiplier=float(_ORDINALS[match.group(1)]),
        )

    # 6. MULTIPLY ---------------------------------------------------------
    match = _MULTIPLIER_RE.search(normalized)
    if match:
        factor = MULTIPLIER_WORDS[match.group(1)]
        return Intent("MULTIPLY", match.group(1), _strip_match(normalized, match), multiplier=factor)

    # 7. BID (numbers are the parser's job) -------------------------------
    return Intent("BID", "", normalized)


def strip_fillers(text: str) -> tuple[str, list[str]]:
    """Remove filler words, returning the cleaned text and what was removed."""
    removed: list[str] = []

    def record(match: re.Match[str]) -> str:
        removed.append(match.group(1))
        return " "

    cleaned = _FILLER_RE.sub(record, text)
    return re.sub(r"\s+", " ", cleaned).strip(), removed


def delta_marker(text: str) -> str | None:
    """Return the leading incremental marker (``mais``, ``cobre``, ...), if any."""
    tokens = text.split()
    if not tokens:
        return None
    # "mil em cima" has the marker at the end.
    if len(tokens) >= 2 and " ".join(tokens[-2:]) == "em cima":
        return "em cima"
    if tokens[0] in DELTA_MARKERS:
        return tokens[0]
    if len(tokens) >= 2 and " ".join(tokens[:2]) in DELTA_MARKERS:
        return " ".join(tokens[:2])
    return None


def closing_marker(text: str) -> bool:
    """True when the speaker signalled they are closing the lot."""
    return bool(re.search(r"(?<!\w)e\s+fechando(?!\w)", text))


def is_stopword_token(token: str) -> bool:
    """Convenience wrapper used by the phonetic repair guard."""
    return strip_accents(token) in {strip_accents(word) for word in _STOPWORD_CACHE}


from auction_demo.nlu.lexicon import STOPWORDS as _STOPWORDS  # noqa: E402

_STOPWORD_CACHE = frozenset(_STOPWORDS)
