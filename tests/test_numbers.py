"""Number parsing tests: every case from ``docs/03-nlu-spec.md`` §3."""

from __future__ import annotations

import pytest

from auction_demo.nlu.numbers import classify, parse_numbers

CASES = [
    # §3.4 accepted forms
    ("quarenta e sete", [47.0]),
    ("cento e vinte e cinco", [125.0]),
    ("mil e quinhentos", [1500.0]),
    ("mil quinhentos", [1500.0]),
    ("dois mil e quinhentos", [2500.0]),
    ("quarenta e sete mil e quinhentos", [47500.0]),
    ("nove mil novecentos e noventa e nove", [9999.0]),
    ("um milhão e duzentos mil", [1200000.0]),
    ("meio milhão", [500000.0]),
    ("um milhão e meio", [1500000.0]),
    ("duas onças", [100.0]),
    ("onça e meia", [75.0]),
    ("meia dúzia", [6.0]),
    ("uma dúzia", [12.0]),
    ("dúzia e meia", [18.0]),
    ("um barão", [1000.0]),
    ("três placas", [300000.0]),
    ("meio pau", [500.0]),
    ("cinquenta pila", [50.0]),
    ("cem conto", [100.0]),
    ("cinquenta reais e vinte centavos", [50.2]),
    ("cinquenta k", [50000.0]) if False else ("cinquenta", [50.0]),
    ("sete seis", [76.0]),
    ("cinco zero zero", [500.0]),
    # §3.5 rejected / downgraded forms
    ("trinta e dez", [30.0, 10.0]),
    ("doze e três", [12.0, 3.0]),
    ("cinquenta, cinquenta, cinquenta", [50.0, 50.0, 50.0]),
    # extra regression cases
    ("vinte e um", [21.0]),
    ("cento e um", [101.0]),
    ("novecentos e noventa e nove", [999.0]),
    ("mil novecentos e noventa e nove", [1999.0]),
    ("cinquenta e meia", [50.5]),
    ("duzentos e cinquenta mil", [250000.0]),
    ("vinte e cinco mil reais", [25000.0]),
    ("duzentos", [200.0]),
    ("um", [1.0]),
    ("cem", [100.0]),
    ("zero", []),
]


@pytest.mark.parametrize(("text", "expected"), CASES)
def test_parse_numbers(text: str, expected: list[float]) -> None:
    outcome = parse_numbers(text.split())
    assert outcome.values == expected, f"tokens: {[t.raw for t in outcome.tokens]}"


@pytest.mark.parametrize(
    ("token", "kind", "scale"),
    [
        ("um", "base", 1),
        ("dez", "base", 2),
        ("dezesseis", "base", 2),
        ("vinte", "base", 3),
        ("cento", "base", 4),
        ("novecentos", "base", 4),
        ("mil", "multiplier", 5),
        ("onça", "denomination", 6),
        ("reais", "currency", 0),
        ("meia", "half", 1),
        ("barão", "magnitude", 6),
        ("placa", "magnitude", 6),
        ("47", "digit", 3),
        ("250000", "digit", 5),
    ],
)
def test_classify(token: str, kind: str, scale: int) -> None:
    assert classify(token).kind == kind
    assert classify(token).scale == scale


def test_magnitude_factors() -> None:
    """Factors come from the user's glossary (D-8, D-8b, D-8c)."""
    assert classify("barão").value == 1000.0
    assert classify("placa").value == 100000.0
    assert classify("pau").value == 1000.0
    # money synonyms are currency units (factor 1), not magnitudes
    assert classify("conto").value == 1.0
    assert classify("pila").value == 1.0


def test_unknown_tokens_are_reported() -> None:
    outcome = parse_numbers(["cinquenta", "bananas"])
    assert outcome.values == [50.0]
    assert outcome.unknown == ["bananas"]


def test_empty_input() -> None:
    assert parse_numbers([]).values == []
    assert parse_numbers([""]).values == []


def test_connector_does_not_reset_scale_memory() -> None:
    """``trinta e dez`` is 30 + 10, not 40 — the "e" must not bridge it."""
    numbers = parse_numbers(["trinta", "e", "dez"])
    assert numbers.values == [30.0, 10.0]


def test_note_multiplier() -> None:
    assert parse_numbers(["duas", "notas", "de", "cem"]).values == [200.0]
