"""Structured results produced by the NLU layer.

The field names are a contract shared with the future Unity client; see
``docs/unity-handoff.md``. Legacy names (``has_addition``, ``has_multiplication``,
``action_type``, ``int_value``) are kept so the C# port does not inherit two
dialects, while ``bid_kind`` is the authoritative, finer-grained enum.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
from typing import Any


@dataclass(slots=True)
class NBestItem:
    """One decoder alternative together with the parser's own score for it."""

    text: str
    score: float = 0.0
    chosen: bool = False

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass(slots=True)
class BidResult:
    """Outcome of parsing one utterance.

    ``value_mode`` explains how ``value`` must be interpreted by the game:

    - ``absolute``: the bid is exactly ``value``
    - ``delta``: add ``value`` to the current bid (``mais vinte``)
    - ``sum``: the numbers were added together (``vinte mais vinte`` -> 40)
    - ``multiply``: a multiplier was applied to the parsed number
    """

    raw_transcript: str = ""
    normalized_text: str = ""
    nbest: list[NBestItem] = field(default_factory=list)

    value: float = 0.0
    int_value: int = 0
    currency: str = "R$ 0,00"
    value_mode: str = "absolute"

    has_addition: bool = False
    has_sum: bool = False
    has_multiplication: bool = False

    action_type: str = "NONE"
    bid_kind: str = "UNKNOWN"

    numbers_found: list[float] = field(default_factory=list)
    idioms_found: list[str] = field(default_factory=list)

    confidence: float = 0.0
    latency_ms: float = 0.0
    reason: str | None = None

    closing: bool = False
    target_value: float | None = None
    lot: int | None = None

    trace: dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        """Serialise for JSON, keeping ``nbest`` as plain dictionaries."""
        data = asdict(self)
        data["nbest"] = [item.to_dict() for item in self.nbest]
        return data


# Mapping from the fine-grained ``bid_kind`` to the legacy ``action_type``.
# Every bid flavour collapses onto "BID"; intents map one-to-one.
BID_KIND_TO_ACTION: dict[str, str] = {
    "BID_ABSOLUTE": "BID",
    "BID_DELTA": "BID",
    "BID_SUM": "BID",
    "BID_MULTIPLY": "BID",
    "MULTIPLY_ONLY": "MULTIPLY_ONLY",
    "PASS": "PASS",
    "CORRECT": "CORRECT",
    "QUERY": "QUERY",
    "CHANT": "CHANT",
    "LOT": "LOT",
    "UNKNOWN": "NONE",
}


def action_for(bid_kind: str) -> str:
    """Return the legacy ``action_type`` for a ``bid_kind``."""
    return BID_KIND_TO_ACTION.get(bid_kind, "NONE")
