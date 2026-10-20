"""End-to-end check of the whole pipeline on real speech.

Loads the model once, runs the same path the server uses (decode -> n-best ->
parse), and prints one report block per clip. This is the acceptance script for
P0/P1: it must show the reference bids parsed correctly.

Usage::

    artifacts/v312/bin/python tools/test_e2e.py
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from auction_demo.asr import audio as audio_utils  # noqa: E402
from auction_demo.asr.vosk_engine import VoskEngine  # noqa: E402
from auction_demo.config import load_settings  # noqa: E402
from auction_demo.nlu.parser import parse_nbest  # noqa: E402

CORPUS = PROJECT_ROOT / "data" / "corpus" / "e2e.jsonl"

# Expected results, from docs/03-nlu-spec.md §5.3.
EXPECTED = {
    "mais vinte mais vinte mais sete": (47.0, True),
    "cinquenta reais": (50.0, False),
    "duzentos e cinquenta mil": (250000.0, False),
    "duas onças": (100.0, False),
    "passo": (0.0, False),
    "um milhão e duzentos mil": (1200000.0, False),
    "quarenta e sete": (47.0, False),
    "cem mil": (100000.0, False),
    "meia dúzia": (6.0, False),
    "três placas": (300000.0, False),
}


def load_wav(path: Path) -> bytes:
    import wave

    with wave.open(str(path), "rb") as handle:
        return handle.readframes(handle.getnframes())


def main() -> int:
    parser = argparse.ArgumentParser(description="End-to-end check on real speech")
    parser.add_argument("--model", default="small", help="model directory name")
    args = parser.parse_args()
    settings = load_settings(model=args.model)
    engine = VoskEngine(settings)
    print(engine.grammar.summary())
    print(f"dropped {len(engine.grammar_dropped)} OOV entries")

    rows = [json.loads(line) for line in CORPUS.read_text(encoding="utf-8").splitlines() if line]

    hits = 0
    for row in rows:
        pcm = load_wav(PROJECT_ROOT / row["audio"])
        padded = audio_utils.pad_utterance(
            pcm,
            lead_ms=settings.lead_silence_ms,
            tail_ms=settings.tail_silence_ms,
        )
        asr = engine.transcribe_pcm(padded)
        result = parse_nbest(asr.nbest_texts())
        result.latency_ms = round(asr.decode_ms, 1)

        want_value, want_add = EXPECTED[row["text"]]
        ok_value = abs(result.value - want_value) < 0.01
        ok_add = result.has_addition is want_add
        good = ok_value and ok_add
        hits += good

        flag = "OK " if good else "BAD"
        print(f"{flag} said={asr.text!r:42s} -> {result.value:>12.2f} add={result.has_addition}"
              f"  (want {want_value:.2f} add={want_add})")

    print(f"\n{hits}/{len(rows)} clips fully correct")
    return 0 if hits == len(rows) else 1


if __name__ == "__main__":
    raise SystemExit(main())
