"""Phonetic repair pass.

Implements §7 of ``docs/03-nlu-spec.md``:
- Explicit guarded replacements for common ASR misrecognitions.
- Protected token guard (STOPWORDS and intent tokens are never modified).
- Safe similarity fallback ensuring zero false positives on the stoplist.
"""

from __future__ import annotations

import difflib
import re
from typing import Final

from auction_demo.nlu.lexicon import (
    STOPWORDS,
    all_number_words,
)
from auction_demo.nlu.numbers import strip_accents

# Explicit phrases (multi-word and contextual replacements)
_EXPLICIT_PHRASES: Final[list[tuple[re.Pattern[str], str]]] = [
    # Sandhi augmentatives
    (re.compile(r"\bduza\s+ent[aã]o\b", re.IGNORECASE), "duzentão"),
    (re.compile(r"\bquinza\s+ent[aã]o\b", re.IGNORECASE), "quinhentão"),
    (re.compile(r"\btresent[aã]o\b", re.IGNORECASE), "trezentão"),
    (re.compile(r"\bseissentos\b", re.IGNORECASE), "seiscentos"),
    (re.compile(r"\bcinco\s+itinhas\b", re.IGNORECASE), "cinquentinha"),
    (re.compile(r"\b(?:mew|mil)\s*[- ]\s*zom\b", re.IGNORECASE), "milzão"),
    (re.compile(r"\bmuzinho\b", re.IGNORECASE), "milzinho"),
    # Acoustic variants
    (re.compile(r"\b(?:sixenta|sisenta|seicenta)\b", re.IGNORECASE), "sessenta"),
    (re.compile(r"\bnovent[ea]n(?:ove|[oó]i)\b", re.IGNORECASE), "noventa e nove"),
    (re.compile(r"\bnoventa\s+em\s+nove\b", re.IGNORECASE), "noventa e nove"),
    # Context-guarded replacements
    (re.compile(r"\bnovem(?=\s+mil\b)", re.IGNORECASE), "nove"),
    # "mas" -> "mais" only when followed by a number / multiplier / currency
    (
        re.compile(
            r"\bmas(?=\s+(?:mil|\d+|um|uma|dois|duas|três|tres|quatro|cinco|seis|sete|oito|nove|dez|"
            r"onze|doze|treze|quatorze|quinze|vinte|trinta|quarenta|cinquenta|sessenta|setenta|"
            r"oitenta|noventa|cem|cento|duzentos|trezentos|quatrocentos|quinhentos|seiscentos|"
            r"setecentos|oitocentos|novecentos|reais|conto|pila|bar[aã]o|bar[oõ]es|placa|placas|pau|paus))\b",
            re.IGNORECASE,
        ),
        "mais",
    ),
    # "sem" -> "cem" only before numbers/currency
    (
        re.compile(
            r"\bsem\s+(mil|\d+|reais|real|conto|pila|bar[aã]o|placa|pau|unidades?)\b",
            re.IGNORECASE,
        ),
        r"cem \1",
    ),
    # "ca" or "ká" after a number -> "mil"
    (
        re.compile(
            r"\b(\d+|um|dois|duas|três|tres|quatro|cinco|seis|sete|oito|nove|dez|"
            r"vinte|trinta|quarenta|cinquenta|sessenta|setenta|oitenta|noventa|cem|duzentos)\s+(?:ca|k[aá]|k)\b",
            re.IGNORECASE,
        ),
        r"\1 mil",
    ),
    # Simple word misrecognitions
    (re.compile(r"\b(?:vindes|v[ií]deo|vite)\b", re.IGNORECASE), "vinte"),
]

# Known vocabulary sets
_ALL_NUMBERS: Final[frozenset[str]] = all_number_words()
_STOPWORDS_NORM: Final[frozenset[str]] = frozenset(strip_accents(w) for w in STOPWORDS)
_NUMBERS_NORM: Final[dict[str, str]] = {strip_accents(w): w for w in _ALL_NUMBERS}


def repair_phonetics(text: str) -> tuple[str, list[tuple[str, str]]]:
    """Apply phonetic corrections according to §7.

    Returns the repaired text and a list of `(original_span, replacement)` pairs.
    """
    if not text:
        return text, []

    current = text
    repairs: list[tuple[str, str]] = []

    # 1. Apply explicit guarded replacements first
    for pattern, replacement in _EXPLICIT_PHRASES:
        match = pattern.search(current)
        if match:
            original = match.group(0)
            current = pattern.sub(replacement, current)
            repairs.append((original, replacement))

    # 2. Token-by-token safe fuzzy matching for OOV tokens
    tokens = current.split()
    modified = False
    new_tokens: list[str] = []

    for token in tokens:
        norm = strip_accents(token)

        # Protected checks:
        # a) Skip if in STOPWORDS (e.g. "quero" -> zero is strictly forbidden)
        # b) Skip if token length < 4
        # c) Skip if already a known number/lexicon token
        # d) Skip digits
        if norm in _STOPWORDS_NORM or len(norm) < 4 or norm in _NUMBERS_NORM or token.isdigit():
            new_tokens.append(token)
            continue

        # Check candidate similarity against numbers
        matches = difflib.get_close_matches(norm, _NUMBERS_NORM.keys(), n=2, cutoff=0.80)
        if len(matches) == 1:
            best = matches[0]
            # Ensure similarity >= 0.80
            sim = difflib.SequenceMatcher(None, norm, best).ratio()
            if sim >= 0.80:
                replacement = _NUMBERS_NORM[best]
                new_tokens.append(replacement)
                repairs.append((token, replacement))
                modified = True
                continue
        elif len(matches) >= 2:
            sim1 = difflib.SequenceMatcher(None, norm, matches[0]).ratio()
            sim2 = difflib.SequenceMatcher(None, norm, matches[1]).ratio()
            if sim1 >= 0.80 and (sim1 - sim2) >= 0.08:
                replacement = _NUMBERS_NORM[matches[0]]
                new_tokens.append(replacement)
                repairs.append((token, replacement))
                modified = True
                continue

        new_tokens.append(token)

    result_text = " ".join(new_tokens) if modified else current
    return result_text, repairs
