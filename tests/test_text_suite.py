"""Evaluation of the authoritative text test suite (tests/fixtures/text_cases.jsonl)."""

from __future__ import annotations

import json

import pytest

from auction_demo.config import PROJECT_ROOT
from auction_demo.nlu.parser import parse

FIXTURE_PATH = PROJECT_ROOT / "tests" / "fixtures" / "text_cases.jsonl"


def load_cases():
    if not FIXTURE_PATH.is_file():
        return []
    lines = FIXTURE_PATH.read_text(encoding="utf-8").splitlines()
    return [json.loads(line) for line in lines if line.strip()]


@pytest.mark.parametrize("case", load_cases(), ids=lambda c: c["text"])
def test_text_suite_case(case: dict) -> None:
    result = parse(case["text"])

    assert abs(result.value - case["value"]) < 0.01, (
        f"Value mismatch for {case['text']!r}: expected {case['value']}, got {result.value}"
    )
    assert result.has_addition is case["has_addition"], (
        f"has_addition mismatch for {case['text']!r}: expected {case['has_addition']}, got {result.has_addition}"
    )
    assert result.has_multiplication is case["has_multiplication"], (
        f"has_multiplication mismatch for {case['text']!r}: expected {case['has_multiplication']}, got {result.has_multiplication}"
    )
    assert result.action_type == case["action_type"], (
        f"action_type mismatch for {case['text']!r}: expected {case['action_type']}, got {result.action_type}"
    )
    assert result.bid_kind == case["bid_kind"], (
        f"bid_kind mismatch for {case['text']!r}: expected {case['bid_kind']}, got {result.bid_kind}"
    )
