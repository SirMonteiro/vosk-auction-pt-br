"""Unit tests for idiom and bingo nickname replacements (docs/03-nlu-spec.md §1 & §2)."""

from __future__ import annotations

import pytest

from auction_demo.nlu.idioms import replace_idioms
from auction_demo.nlu.parser import parse


@pytest.mark.parametrize(
    ("phrase", "expected_num"),
    [
        ("dois patinhos na lagoa", 22.0),
        ("dois patinhos", 22.0),
        ("idade de cristo", 33.0),
        ("a idade de cristo", 33.0),
        ("justiça de goiás", 38.0),
        ("três oitão", 38.0),
        ("quá-quá-quá", 44.0),
        ("fim do primeiro tempo", 45.0),
        ("uma boa ideia", 51.0),
        ("dois portugueses numa perna só", 55.0),
        ("um tapa atrás da orelha", 66.0),
        ("um pra cima e outro pra baixo", 69.0),
        ("par de machados", 77.0),
        ("dois bujões", 88.0),
        ("pernas da xuxa", 11.0),
        ("camisa dez", 10.0),
        ("uma dúzia e meia", 18.0),
        ("meia dúzia", 6.0),
        ("uma dúzia", 12.0),
        ("o azarado", 13.0),
        ("a debutante", 15.0),
        ("rapazinho alegre", 24.0),
        ("dia de natal", 25.0),
        ("meteu num 31", 31.0),
        ("o louco", 35.0),
        ("meia telha", 50.0),
    ],
)
def test_bingo_nicknames_parse_correctly(phrase: str, expected_num: float) -> None:
    result = parse(phrase)
    assert abs(result.value - expected_num) < 0.01, f"{phrase} -> {result.value}"
    norm = phrase.replace("-", " ")
    assert norm in result.idioms_found or any(p in result.idioms_found for p in norm.split())


def test_longest_match_precedence() -> None:
    """Longer phrases must match first ('dois patinhos na lagoa' before 'dois patinhos')."""
    text, found = replace_idioms("dois patinhos na lagoa")
    assert "22" in text
    assert found == ["dois patinhos na lagoa"]


@pytest.mark.parametrize(
    ("phrase", "expected_val"),
    [
        ("um par de onças", 100.0),
        ("par de onças", 100.0),
        ("onça e meia", 75.0),
        ("duas dezenas", 20.0),
        ("meia centena", 50.0),
        ("um milheiro", 1000.0),
    ],
)
def test_extended_idioms_parse_correctly(phrase: str, expected_val: float) -> None:
    result = parse(phrase)
    assert abs(result.value - expected_val) < 0.01
    assert result.action_type == "BID"
    assert result.bid_kind == "BID_ABSOLUTE"
