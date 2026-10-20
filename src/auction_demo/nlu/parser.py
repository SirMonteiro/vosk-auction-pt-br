"""Bid parser: text in, :class:`BidResult` out.

Pure and deterministic: no I/O, no model, no globals beyond compiled tables.
Implements §5 of ``docs/03-nlu-spec.md``.

The semantics intentionally mirror ``vosk-python``'s evaluator, which is the only
existing implementation that scores 40/40 on the text suite:

* a single number is an absolute bid
* a leading marker (``mais``, ``cobre``, ...) makes it incremental
* several numbers joined by ``mais`` are summed
* several numbers *not* joined are a correction: the last one wins
* a multiplier scales the number, or stands alone
"""

from __future__ import annotations

import time

from auction_demo.nlu.currency import format_brazilian_currency
from auction_demo.nlu.intents import (
    closing_marker,
    delta_marker,
    detect,
    normalize,
    strip_fillers,
)
from auction_demo.nlu.models import BidResult, NBestItem, action_for
from auction_demo.nlu.numbers import parse_numbers


def _empty(raw: str, reason: str | None = None) -> BidResult:
    return BidResult(
        raw_transcript=raw,
        normalized_text="",
        value=0.0,
        int_value=0,
        currency=format_brazilian_currency(0.0),
        action_type="NONE",
        bid_kind="UNKNOWN",
        reason=reason,
    )


def _confidence(parsed: BidResult, dropped: int, repairs: int) -> float:
    """Deterministic parse confidence in [0, 1].

    Rewards a valid bid and a clean token stream, penalises tokens the parser
    had to ignore. This is what ranks n-best alternatives, because Vosk's own
    confidence ordering is unreliable (``docs/08`` §8).
    """
    score = 0.0
    if parsed.bid_kind.startswith("BID") or parsed.bid_kind in {
        "MULTIPLY_ONLY",
        "PASS",
        "CORRECT",
        "QUERY",
        "CHANT",
        "LOT",
    }:
        score += 1.0
    score += 0.3 if dropped == 0 else 0.0
    score += 0.2 if repairs == 0 else 0.0
    score -= min(0.5, 0.1 * dropped)
    return max(0.0, min(1.0, score))


def parse(text: str, *, debug: bool = False) -> BidResult:
    """Parse one utterance into a :class:`BidResult`."""
    started = time.perf_counter()
    raw = text or ""

    if not raw.strip():
        result = _empty(raw, reason="silence")
        result.latency_ms = (time.perf_counter() - started) * 1000
        return result

    normalized = normalize(raw)
    intent = detect(normalized)

    trace: dict = {}
    if debug:
        trace = {"intent": intent.name, "matched": intent.matched}

    # --- non-bid intents -------------------------------------------------
    if intent.name in {"PASS", "CORRECT", "QUERY", "CHANT"}:
        kind = intent.name
        result = BidResult(
            raw_transcript=raw,
            normalized_text=normalized,
            value=0.0,
            int_value=0,
            currency=format_brazilian_currency(0.0),
            action_type=action_for(kind),
            bid_kind=kind,
            closing=closing_marker(normalized),
        )
        # "errei, era cinquenta" carries the corrected value.
        if kind == "CORRECT":
            numbers = parse_numbers(intent.remainder.split())
            if numbers.values:
                result.target_value = numbers.values[-1]
        result.confidence = _confidence(result, 0, 0)
        result.trace = trace
        result.latency_ms = (time.perf_counter() - started) * 1000
        return result

    if intent.name == "LOT":
        result = BidResult(
            raw_transcript=raw,
            normalized_text=normalized,
            value=0.0,
            int_value=0,
            currency=format_brazilian_currency(0.0),
            action_type="LOT",
            bid_kind="LOT",
            lot=int(intent.multiplier or 0),
        )
        result.confidence = _confidence(result, 0, 0)
        result.trace = trace
        result.latency_ms = (time.perf_counter() - started) * 1000
        return result

    # --- numbers ---------------------------------------------------------
    cleaned, fillers = strip_fillers(intent.remainder)
    multiplier = intent.multiplier if intent.name == "MULTIPLY" else None
    marker = delta_marker(cleaned)

    # Remove the leading marker before parsing so "mais vinte" is not "more 20".
    number_text = cleaned
    if marker:
        number_text = cleaned[len(marker) :].strip()

    parsed = parse_numbers(number_text.split())
    values = parsed.values
    dropped = len(parsed.unknown) + len(fillers)

    if debug:
        trace |= {
            "cleaned": cleaned,
            "marker": marker,
            "values": values,
            "unknown": parsed.unknown,
            "fillers": fillers,
            "multiplier": multiplier,
        }

    # --- multiplier with no number: "dobro" ------------------------------
    if not values and multiplier is not None:
        result = BidResult(
            raw_transcript=raw,
            normalized_text=normalized,
            value=multiplier,
            int_value=int(round(multiplier)),
            currency=format_brazilian_currency(multiplier),
            value_mode="multiply",
            has_multiplication=True,
            action_type="MULTIPLY_ONLY",
            bid_kind="MULTIPLY_ONLY",
            numbers_found=[],
        )
        result.confidence = _confidence(result, dropped, 0)
        result.trace = trace
        result.latency_ms = (time.perf_counter() - started) * 1000
        return result

    # --- no numbers at all -----------------------------------------------
    if not values:
        result = _empty(raw, reason="unknown")
        result.normalized_text = normalized
        result.trace = trace
        result.latency_ms = (time.perf_counter() - started) * 1000
        return result

    # --- value assembly --------------------------------------------------
    plus_count = cleaned.count(" mais ")
    if marker == "mais":
        plus_count += 1

    if len(values) == 1:
        value = values[0]
        if marker is not None:
            mode = "delta"
        else:
            mode = "absolute"
    elif marker is not None and plus_count >= len(values):
        # "mais vinte mais vinte mais sete": an incremental chain.
        value = sum(values)
        mode = "delta"
    elif plus_count == len(values) - 1:
        # "vinte mais vinte": a sum stated as one bid.
        value = sum(values)
        mode = "sum"
    else:
        # Numbers without connectors: the speaker corrected themselves.
        value = values[-1]
        mode = "absolute"

    has_multiplication = multiplier is not None
    if multiplier is not None:
        value *= multiplier
        mode = "multiply"

    kind = {
        "absolute": "BID_ABSOLUTE",
        "delta": "BID_DELTA",
        "sum": "BID_SUM",
        "multiply": "BID_MULTIPLY",
    }[mode]

    result = BidResult(
        raw_transcript=raw,
        normalized_text=cleaned,
        value=float(value),
        int_value=int(round(value)),
        currency=format_brazilian_currency(value),
        value_mode=mode,
        has_addition=mode == "delta",
        has_sum=mode == "delta",
        has_multiplication=has_multiplication,
        action_type=action_for(kind),
        bid_kind=kind,
        numbers_found=values,
        closing=closing_marker(normalized),
    )
    result.confidence = _confidence(result, dropped, 0)
    result.trace = trace
    result.latency_ms = (time.perf_counter() - started) * 1000
    return result


def parse_nbest(texts: list[str], *, debug: bool = False) -> BidResult:
    """Parse every decoder alternative and keep the most confident valid one.

    Vosk's confidence ranking is unreliable (it ranked ``mas vindo de`` above
    ``mais vinte``), so the parser scores each hypothesis itself. Ties keep the
    decoder's order, which favours the first hypothesis.
    """
    if not texts:
        return _empty("", reason="silence")

    best: BidResult | None = None
    items: list[NBestItem] = []

    for index, candidate in enumerate(texts):
        parsed = parse(candidate, debug=debug)
        items.append(NBestItem(text=candidate, score=parsed.confidence, chosen=False))
        if best is None or parsed.confidence > best.confidence:
            best = parsed
            best_index = index

    assert best is not None
    for index, item in enumerate(items):
        item.chosen = index == best_index
    best.nbest = items
    return best
