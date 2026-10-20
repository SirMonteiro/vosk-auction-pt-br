"""Generate the language-model corpus for the auction domain.

The text that goes into ``ngramcount`` decides what the decoder hears, so the
generator is deterministic (fixed seed) and weighted: bid frames dominate, then
bare numbers, then chained bids, then idioms, then intents.

Every token must exist in the model's symbol table (``Gr.fst`` words) or
``farcompilestrings`` aborts, so the corpus is filtered and the dropped set is
reported instead of silently shrinking.

Usage::

    uv run python -m tools.build_lm_corpus --out data/lm/text.txt --seed 20261020
"""

from __future__ import annotations

import argparse
import hashlib
import json
import random
import re
from pathlib import Path

from auction_demo.config import PROJECT_ROOT
from auction_demo.nlu.grammar import ACCENT_FIX, FRAME_PREFIXES
from auction_demo.nlu.lexicon import (
    BINGO,
    CHANT_PHRASES,
    CORRECT_PHRASES,
    CURRENCY_UNITS,
    DENOMINATIONS,
    MAGNITUDES,
    MULTIPLIER_WORDS,
    PASS_PHRASES,
    QUERY_PHRASES,
)
from auction_demo.nlu.numbers import number_to_words

UNITS = ["um", "dois", "três", "quatro", "cinco", "seis", "sete", "oito", "nove"]
TENS = ["vinte", "trinta", "quarenta", "cinquenta", "sessenta", "setenta", "oitenta", "noventa"]
HUNDREDS = [
    "cento",
    "duzentos",
    "trezentos",
    "quatrocentos",
    "quinhentos",
    "seiscentos",
    "setecentos",
    "oitocentos",
    "novecentos",
]

#: How many times each source is repeated in the shuffled corpus.
WEIGHTS = {
    "frames": 2,
    "numbers": 1,
    "chained": 3,
    "idioms": 60,
    "intents": 60,
}


def below_1000() -> list[int]:
    """0..999."""
    values = list(range(0, 20))
    for tens in range(20, 100, 10):
        values.append(tens)
        values.extend(tens + unit for unit in range(1, 10))
    values.append(100)
    for hundred in range(200, 1000, 100):
        values.append(hundred)
        values.extend(hundred + tail for tail in range(1, 100))
    return sorted(set(values))


def all_numbers() -> list[int]:
    """0..999 plus the thousands that matter for bids."""
    base = below_1000()
    thousands = []
    for value in base:
        thousands.append(value * 1000)
        for tail in (1, 5, 15, 25, 50, 100, 200, 500):
            thousands.append(value * 1000 + tail)
    return sorted(set(base) | set(thousands))


def canonical(text: str) -> str:
    """Lowercase, collapse whitespace and restore model-known accents."""
    text = re.sub(r"\s+", " ", text.replace("-", " ").replace(",", " ")).strip().lower()
    return " ".join(ACCENT_FIX.get(token, token) for token in text.split())


def build_lines(seed: int) -> list[str]:
    rng = random.Random(seed)
    numbers = all_numbers()
    lines: list[str] = []

    # 1. bid frames: "mais vinte reais", "cobre cem conto", ...
    for value in numbers:
        words = number_to_words(value)
        for prefix in ("", "mais", "cobre", "vou de", "subo", "aumento"):
            for suffix in ("", "reais", "conto", "pila", "mil reais"):
                phrase = f"{prefix} {words} {suffix}".strip()
                lines.append(phrase)

    # 2. bare numbers
    lines.extend(number_to_words(value) for value in numbers)

    # 3. chained additions: "mais vinte mais vinte mais sete"
    for _ in range(20_000):
        count = rng.randint(2, 4)
        chain = [number_to_words(rng.choice(numbers)) for _ in range(count)]
        lines.append("mais " + " mais ".join(chain))

    # 4. idioms and slang
    idioms: list[str] = []
    idioms.extend(BINGO)
    for word in DENOMINATIONS:
        idioms.append(word)
    for word in MAGNITUDES:
        idioms.append(f"um {word}")
        idioms.append(f"dois {word}")
        idioms.append(f"três {word}")
    for word in CURRENCY_UNITS:
        idioms.append(word)
    idioms.extend(f"meio {word}" for word in MAGNITUDES)
    idioms.extend(["meia dúzia", "uma dúzia", "dúzia e meia"])
    lines.extend(idioms)

    # 5. intents and multipliers
    intents: list[str] = []
    intents.extend(PASS_PHRASES)
    intents.extend(CORRECT_PHRASES)
    intents.extend(QUERY_PHRASES)
    intents.extend(CHANT_PHRASES)
    intents.extend(MULTIPLIER_WORDS)
    intents.extend(
        f"{word} de {number_to_words(n)}" for word in MULTIPLIER_WORDS for n in (50, 100, 200)
    )
    intents.extend(list(FRAME_PREFIXES))
    lines.extend(intents)

    return [canonical(line) for line in lines if canonical(line)]


def apply_weights(lines: list[str], target_lines: int) -> list[str]:
    """Repeat ``lines`` by its source weights, capped at ``target_lines``.

    Each source category is repeated by its weight, then the pool is truncated.
    A fixed target keeps the build bounded while preserving proportions.
    """
    weighted: list[str] = []
    for weight in WEIGHTS.values():
        weighted.extend(lines * weight)
    if len(weighted) > target_lines:
        weighted = weighted[:target_lines]
    return weighted


def model_vocabulary(model_dir: Path) -> set[str]:
    """Words the model's Gr.fst knows. Requires the openfst toolchain."""
    import subprocess
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        symbols = Path(tmp) / "words.txt"
        command = [
            "fstsymbols",
            f"--save_osymbols={symbols}",
            str(model_dir / "Gr.fst"),
        ]
        subprocess.run(command, check=True, capture_output=True)
        vocabulary: set[str] = set()
        for line in symbols.read_text(encoding="utf-8").splitlines():
            parts = line.split("\t")
            if parts:
                vocabulary.add(parts[0])
    return vocabulary


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Build the LM corpus")
    parser.add_argument("--out", default="data/lm/text.txt", help="output text file")
    parser.add_argument("--model", default="small", help="model to filter the vocabulary against")
    parser.add_argument("--seed", type=int, default=20261020, help="shuffle seed")
    parser.add_argument("--no-filter", action="store_true", help="skip vocabulary filtering")
    parser.add_argument("--max-lines", type=int, default=400_000, help="corpus size cap")
    args = parser.parse_args(argv)

    rng = random.Random(args.seed)
    lines = apply_weights(build_lines(args.seed), args.max_lines)
    rng.shuffle(lines)

    output = PROJECT_ROOT / args.out
    output.parent.mkdir(parents=True, exist_ok=True)

    if not args.no_filter:
        model_dir = PROJECT_ROOT / "models" / args.model
        if not model_dir.is_dir():
            raise SystemExit(f"model not found: {model_dir}")
        vocabulary = model_vocabulary(model_dir)

        kept: list[str] = []
        dropped_tokens: set[str] = set()
        for line in lines:
            tokens = line.split()
            missing = [token for token in tokens if token not in vocabulary]
            if missing:
                dropped_tokens.update(missing)
                continue
            kept.append(line)
        lines = kept

    output.write_text("\n".join(lines) + "\n", encoding="utf-8")

    sha = hashlib.sha256(output.read_bytes()).hexdigest()[:16]
    print(f"corpus: {len(lines)} lines -> {output}  sha256:{sha}")
    if not args.no_filter:
        print(f"filtered against {args.model}: {len(dropped_tokens)} unknown token kinds")
        if dropped_tokens:
            print("  " + " ".join(sorted(dropped_tokens)))
    (output.parent / "corpus.json").write_text(
        json.dumps({"lines": len(lines), "sha256": sha, "seed": args.seed}, indent=2) + "\n",
        encoding="utf-8",
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
