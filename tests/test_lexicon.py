"""Tests for the lexicon tables and currency formatting.

These run without any model download: they are the P0 acceptance gate.
"""

from __future__ import annotations

import pytest

from auction_demo.nlu.currency import format_brazilian_currency
from auction_demo.nlu.lexicon import (
    BASES,
    BINGO,
    CHANT_PHRASES,
    CORRECT_PHRASES,
    CURRENCY_UNITS,
    DELTA_MARKERS,
    DENOMINATIONS,
    FILLERS,
    MAGNITUDES,
    MULTIPLIER_WORDS,
    MULTIPLIERS,
    PASS_PHRASES,
    QUERY_PHRASES,
    SINGLE_DIGITS,
    STOPWORDS,
    all_idioms,
    all_number_words,
)


class TestCurrency:
    @pytest.mark.parametrize(
        ("value", "expected"),
        [
            (0.0, "R$ 0,00"),
            (47.0, "R$ 47,00"),
            (1234.56, "R$ 1.234,56"),
            (1_000_000.0, "R$ 1.000.000,00"),
            (0.5, "R$ 0,50"),
            (50.2, "R$ 50,20"),
        ],
    )
    def test_format(self, value: float, expected: str) -> None:
        assert format_brazilian_currency(value) == expected


class TestMagnitudes:
    """The slang factors come from the user glossary (D-8, D-8b, D-8c)."""

    def test_barao_is_thousand(self) -> None:
        assert MAGNITUDES["barão"] == 1_000.0
        assert MAGNITUDES["barões"] == 1_000.0

    def test_pau_is_thousand(self) -> None:
        assert MAGNITUDES["pau"] == 1_000.0
        assert MAGNITUDES["paus"] == 1_000.0

    def test_placa_is_hundred_thousand(self) -> None:
        # Changed from the legacy x1000: "placa" means R$ 100 000 in the car trade.
        assert MAGNITUDES["placa"] == 100_000.0
        assert MAGNITUDES["placas"] == 100_000.0

    def test_conto_is_not_a_magnitude(self) -> None:
        # "conto" is a synonym for reais, handled as a currency unit (factor 1).
        assert "conto" not in MAGNITUDES
        assert "conto" in CURRENCY_UNITS
        assert "contos" in CURRENCY_UNITS

    def test_pila_and_prata_are_currency(self) -> None:
        for word in ("pila", "pilas", "prata", "pratas"):
            assert word in CURRENCY_UNITS
            assert word not in MAGNITUDES


class TestLexiconIntegrity:
    def test_no_overlap_between_magnitudes_and_currency(self) -> None:
        assert not set(MAGNITUDES) & set(CURRENCY_UNITS)

    def test_no_overlap_between_multipliers_and_magnitudes(self) -> None:
        assert not set(MULTIPLIERS) & set(MAGNITUDES)

    def test_all_canonical_keys_are_lowercase(self) -> None:
        for table in (BASES, MULTIPLIERS, MAGNITUDES, DENOMINATIONS, BINGO, MULTIPLIER_WORDS):
            for key in table:
                assert key == key.lower(), f"{key!r} is not lowercase"

    def test_accents_are_preserved(self) -> None:
        # Vosk silently drops unaccented grammar entries, so canonical forms
        # must keep their diacritics.
        assert "garça" in DENOMINATIONS
        assert "onça" in DENOMINATIONS
        assert "milhão" in MULTIPLIERS
        assert "barão" in MAGNITUDES

    def test_single_digits_cover_zero_to_nine(self) -> None:
        assert {v for v in SINGLE_DIGITS.values()} == set(range(10))

    def test_bases_are_positive_or_zero(self) -> None:
        assert all(v >= 0 for v in BASES.values())

    def test_multipliers_ascending(self) -> None:
        assert MULTIPLIERS["mil"] < MULTIPLIERS["milhão"] < MULTIPLIERS["bilhão"]

    def test_multiplier_words_factors(self) -> None:
        assert MULTIPLIER_WORDS["dobro"] == 2.0
        assert MULTIPLIER_WORDS["triplo"] == 3.0
        assert MULTIPLIER_WORDS["quádruplo"] == 4.0
        assert MULTIPLIER_WORDS["metade"] == 0.5


class TestIntentTables:
    def test_intent_sets_are_disjoint(self) -> None:
        sets = {
            "pass": PASS_PHRASES,
            "correct": CORRECT_PHRASES,
            "query": QUERY_PHRASES,
            "chant": CHANT_PHRASES,
        }
        names = list(sets)
        for i, left in enumerate(names):
            for right in names[i + 1 :]:
                overlap = sets[left] & sets[right]
                assert not overlap, f"{left} and {right} overlap: {overlap}"

    def test_chant_contains_auctioneer_calls(self) -> None:
        assert "dou-lhe uma" in CHANT_PHRASES
        assert "vendido" in CHANT_PHRASES

    def test_pass_contains_no_bid_words(self) -> None:
        assert "passo" in PASS_PHRASES
        assert "sem lance" in PASS_PHRASES

    def test_stopwords_cover_every_intent_token(self) -> None:
        for phrase in PASS_PHRASES | CORRECT_PHRASES | QUERY_PHRASES | CHANT_PHRASES:
            for token in phrase.split():
                assert token in STOPWORDS, f"{token!r} from {phrase!r} is not a stopword"

    def test_stopwords_cover_delta_markers(self) -> None:
        assert DELTA_MARKERS <= STOPWORDS

    def test_fillers_are_stopwords(self) -> None:
        for phrase in FILLERS:
            for token in phrase.split():
                assert token in STOPWORDS


class TestHelpers:
    def test_all_number_words_includes_every_table(self) -> None:
        words = all_number_words()
        assert "cem" in words
        assert "mil" in words
        assert "onça" in words
        assert "barão" in words
        assert "reais" in words

    def test_all_idioms_matches_bingo(self) -> None:
        assert all_idioms() == BINGO

    def test_bingo_values_are_bid_sized(self) -> None:
        # Nicknames exist to make bids memorable; anything above 100 is a typo.
        assert all(0 < value <= 100 for value in BINGO.values())
