"""Parser tests: every reference case from ``docs/03-nlu-spec.md`` §5.3."""

from __future__ import annotations

import pytest

from auction_demo.nlu.parser import parse, parse_nbest

# (utterance, value, has_addition, has_multiplication, action_type, bid_kind)
CASES = [
    ("mais vinte", 20.0, True, False, "BID", "BID_DELTA"),
    ("vinte mais vinte", 40.0, False, False, "BID", "BID_SUM"),
    ("mais vinte mais vinte mais sete", 47.0, True, False, "BID", "BID_DELTA"),
    ("mais dez mais dez", 20.0, True, False, "BID", "BID_DELTA"),
    ("cinquenta, cinquenta, cinquenta", 50.0, False, False, "BID", "BID_ABSOLUTE"),
    ("50, 40, 20", 20.0, False, False, "BID", "BID_ABSOLUTE"),
    ("dobro", 2.0, False, True, "MULTIPLY_ONLY", "MULTIPLY_ONLY"),
    ("dobro de cinquenta", 100.0, False, True, "BID", "BID_MULTIPLY"),
    ("triplo de cinquenta", 150.0, False, True, "BID", "BID_MULTIPLY"),
    ("metade de cem", 50.0, False, True, "BID", "BID_MULTIPLY"),
    ("passo", 0.0, False, False, "PASS", "PASS"),
    ("tô fora", 0.0, False, False, "PASS", "PASS"),
    ("errei", 0.0, False, False, "CORRECT", "CORRECT"),
    ("dou-lhe uma", 0.0, False, False, "CHANT", "CHANT"),
    ("lote três", 0.0, False, False, "LOT", "LOT"),
    # regression cases from the legacy parser bugs
    ("quero", 0.0, False, False, "NONE", "UNKNOWN"),
    ("sem lance", 0.0, False, False, "PASS", "PASS"),
    ("lote um", 0.0, False, False, "LOT", "LOT"),
    # plain bids
    ("cinquenta reais", 50.0, False, False, "BID", "BID_ABSOLUTE"),
    ("duzentos e cinquenta mil", 250000.0, False, False, "BID", "BID_ABSOLUTE"),
    ("um barão", 1000.0, False, False, "BID", "BID_ABSOLUTE"),
    ("três placas", 300000.0, False, False, "BID", "BID_ABSOLUTE"),
    ("meio pau", 500.0, False, False, "BID", "BID_ABSOLUTE"),
    ("cem conto", 100.0, False, False, "BID", "BID_ABSOLUTE"),
    ("duas onças", 100.0, False, False, "BID", "BID_ABSOLUTE"),
    ("meia dúzia", 6.0, False, False, "BID", "BID_ABSOLUTE"),
    ("nove mil novecentos e noventa e nove", 9999.0, False, False, "BID", "BID_ABSOLUTE"),
]


@pytest.mark.parametrize(("text", "value", "addition", "mult", "action", "kind"), CASES)
def test_reference_cases(
    text: str,
    value: float,
    addition: bool,
    mult: bool,
    action: str,
    kind: str,
) -> None:
    result = parse(text)
    assert abs(result.value - value) < 0.01, f"value: {result.value}"
    assert result.has_addition is addition, f"has_addition: {result.has_addition}"
    assert result.has_multiplication is mult, f"has_multiplication: {result.has_multiplication}"
    assert result.action_type == action
    assert result.bid_kind == kind


def test_action_type_maps_to_bid_kind() -> None:
    """Legacy ``action_type`` collapses the four bid flavours onto ``BID``."""
    for text, expected_action in [
        ("cinquenta", "BID"),
        ("mais vinte", "BID"),
        ("vinte mais vinte", "BID"),
        ("dobro de cem", "BID"),
        ("dobro", "MULTIPLY_ONLY"),
        ("passo", "PASS"),
    ]:
        result = parse(text)
        assert result.action_type == expected_action
        assert result.bid_kind.startswith("BID_") or result.action_type == result.bid_kind


def test_empty_and_whitespace() -> None:
    for text in ("", "   ", None):
        result = parse(text or "")
        assert result.value == 0.0
        assert result.bid_kind == "UNKNOWN"
        assert result.reason == "silence"


def test_correct_carries_target_value() -> None:
    result = parse("errei era cinquenta")
    assert result.action_type == "CORRECT"
    assert result.target_value == 50.0


def test_lot_orders_articles() -> None:
    assert parse("lote 2").lot == 2
    assert parse("segundo lote").lot == 2
    assert parse("quinto lote").lot == 5


def test_fillers_do_not_change_the_value() -> None:
    assert parse("cinquenta redondo").value == 50.0
    assert parse("cinquenta reais na lata").value == 50.0


def test_closing_marker() -> None:
    assert parse("cinquenta e fechando").closing is True
    assert parse("cinquenta").closing is False


def test_currency_formatting_in_result() -> None:
    assert parse("cinquenta").currency == "R$ 50,00"
    assert parse("duzentos e cinquenta mil").currency == "R$ 250.000,00"


def test_nbest_picks_the_parsable_hypothesis() -> None:
    """Vosk's own ranking is unreliable, so the parser scores each alternative."""
    result = parse_nbest(["mas vindo de", "mais vinte"])
    assert result.value == 20.0
    assert len(result.nbest) == 2
    assert result.nbest[0].chosen is False
    assert result.nbest[1].chosen is True


def test_nbest_falls_back_to_first_when_all_equal() -> None:
    result = parse_nbest(["vinte", "trinta"])
    assert result.value == 20.0
    assert result.nbest[0].chosen is True


def test_determinism() -> None:
    """Same input must give the same decision; only timing may differ."""
    a = parse("mais vinte mais vinte mais sete", debug=True).to_dict()
    b = parse("mais vinte mais vinte mais sete", debug=True).to_dict()
    a.pop("latency_ms")
    b.pop("latency_ms")
    assert a == b


def test_trace_is_populated_only_in_debug() -> None:
    assert parse("cinquenta").trace == {}
    assert parse("cinquenta", debug=True).trace != {}
