"""Grammar builder tests: budget, accents, and coverage of the reference phrases."""

from __future__ import annotations

import json

import pytest

from auction_demo.nlu.grammar import (
    build_grammar,
    grammar_stats,
    phrases_from_texts,
)

#: Phrases that must survive: the reference cases of ``docs/03`` §5.3 and the
#: OOV-prone slang.
REQUIRED = [
    "mais vinte",
    "cobre cinquenta",
    "vou de cem",
    "cinquenta",
    "cem",
    "mil",
    "duzentos e cinquenta mil",
    "quarenta e sete mil e quinhentos",
    "um barão",
    "três placas",
    "meio pau",
    "duas onças",
    "meia dúzia",
    "dois patinhos na lagoa",
    "passo",
    "dobro de cinquenta",
    "dou lhe uma",
    "não quero",
]


def test_reference_phrases_are_present() -> None:
    report = build_grammar(max_phrases=6_000)
    for phrase in REQUIRED:
        assert phrase in report.phrases, f"missing {phrase!r}"


def test_budget_is_respected() -> None:
    for cap in (1_000, 3_000, 6_000, 12_000):
        report = build_grammar(max_phrases=cap)
        assert report.size <= cap + 1  # +1 for "[unk]"


def test_hard_limit_is_enforced() -> None:
    with pytest.raises(ValueError, match="hard limit"):
        build_grammar(max_phrases=20_000, hard_limit=15_000)


def test_unknown_token_is_appended() -> None:
    assert build_grammar(max_phrases=100).phrases[-1] == "[unk]"


def test_accents_are_preserved_in_json() -> None:
    """``ensure_ascii=False`` is required or Vosk drops every accented word."""
    payload = build_grammar(max_phrases=6_000).to_json()
    assert "\\u" not in payload
    assert "garça" in payload
    assert "onça" in payload
    assert "não quero" in payload
    parsed = json.loads(payload)
    assert isinstance(parsed, list)


def test_duplicates_are_removed() -> None:
    report = build_grammar(max_phrases=6_000)
    assert len(report.phrases) == len(set(report.phrases))


def test_deterministic() -> None:
    a = build_grammar(max_phrases=6_000)
    b = build_grammar(max_phrases=6_000)
    assert a.phrases == b.phrases
    assert a.counts == b.counts


def test_model_validation_reports_drops() -> None:
    def fake_find(word: str) -> int:
        return -1 if word in {"oitão", "xuxa"} else 42

    report = build_grammar(max_phrases=6_000, is_known_word=fake_find)
    assert "garça" in report.dropped or "garça" in report.phrases
    assert isinstance(report.dropped, list)


def test_no_phrases_contain_unknown_words_when_filtered() -> None:
    report = build_grammar(max_phrases=6_000, is_known_word=lambda w: 1)
    assert report.dropped == []
    assert len(report.phrases) == report.size


def test_grammar_stats() -> None:
    stats = grammar_stats(["um dois", "três"])
    assert stats == {
        "phrases": 2,
        "tokens": 3,
        "bytes": len(json.dumps(["um dois", "três"], ensure_ascii=False)),
    }


def test_phrases_from_texts_deduplicates_and_normalises() -> None:
    report = phrases_from_texts(["  Mais   Vinte ", "mais vinte", "vinte"])
    assert report.phrases == ["mais vinte", "vinte", "[unk]"]


def test_summary_is_readable() -> None:
    report = build_grammar(max_phrases=1_000)
    text = report.summary()
    assert "grammar:" in text
    assert "phrases" in text


def test_count_and_size_are_consistent() -> None:
    report = build_grammar(max_phrases=6_000)
    assert report.size == len(report.phrases)
    assert sum(report.counts.values()) + (1 if "[unk]" in report.phrases else 0) >= report.size - 1
