"""Idiom and bingo phrase replacement.

Performs longest-match replacement of domain idioms, bingo nicknames, and
colloquial quantifiers into numeral tokens (``docs/03-nlu-spec.md`` §1 & §2).
"""

from __future__ import annotations

import re
from typing import Final

from auction_demo.nlu.lexicon import IDIOMS

# Sort phrases longest first so "dois patinhos na lagoa" matches before "dois patinhos".
_SORTED_IDIOMS: Final[list[tuple[str, str, re.Pattern[str]]]] = [
    (
        phrase,
        str(val),
        re.compile(r"(?<!\w)" + re.escape(phrase) + r"(?!\w)", re.IGNORECASE),
    )
    for phrase, val in sorted(IDIOMS.items(), key=lambda item: len(item[0]), reverse=True)
]


def replace_idioms(text: str) -> tuple[str, list[str]]:
    """Replace domain idioms with numeric tokens, returning the text and matched idioms.

    Longest matches fire first. Matched phrases are recorded in ``idioms_found``.
    """
    if not text:
        return text, []

    current = text
    found: list[str] = []

    for phrase, replacement, pattern in _SORTED_IDIOMS:
        if pattern.search(current):
            current = pattern.sub(replacement, current)
            if phrase not in found:
                found.append(phrase)

    cleaned = re.sub(r"\s+", " ", current).strip()
    return cleaned, found
