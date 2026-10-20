"""Brazilian Portuguese cardinal parsing.

Implements §3 of ``docs/03-nlu-spec.md``:

* tokenisation into scale classes (units, teens, tens, hundreds, multipliers)
* chunking, where an illegal scale regression starts a new number
  (``trinta e dez`` is 30 then 10, not 40)
* cardinal evaluation with ``mil`` / ``milhão`` handling
* spelled digits (``sete seis`` -> 76), centavos, and the slang magnitudes
  recorded in the user glossary (``barão`` = 1000, ``placa`` = 100 000,
  ``conto``/``pila``/``prata`` = 1)

The parser never raises on unknown input; unknown tokens are skipped and
reported in the returned ``ParseOutcome.tokens`` for tracing.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from auction_demo.nlu.lexicon import (
    BASES,
    CURRENCY_UNITS,
    DENOMINATIONS,
    MAGNITUDES,
    MULTIPLIERS,
    NOTE_DENOMINATORS,
    SINGLE_DIGITS,
)

#: Scale of a token. A *lower* number is a "smaller" magnitude class.
SCALE_UNIT = 1
SCALE_TEEN = 2
SCALE_TEN = 3
SCALE_HUNDRED = 4
SCALE_MULTIPLIER = 5
SCALE_DENOMINATION = 6
SCALE_AUXILIARY = 0

_NUMERIC = re.compile(r"^\d+(?:[.,]\d+)?$")


def strip_accents(text: str) -> str:
    """Lowercase and remove diacritics. Used for matching, never for output."""
    import unicodedata

    decomposed = unicodedata.normalize("NFD", text)
    return "".join(char for char in decomposed if unicodedata.category(char) != "Mn").lower()


@dataclass(slots=True)
class NumberToken:
    """A classified token."""

    raw: str
    scale: int
    value: float | None = None
    kind: str = "unknown"


@dataclass(slots=True)
class ParseOutcome:
    """Result of parsing a token sequence."""

    values: list[float] = field(default_factory=list)
    tokens: list[NumberToken] = field(default_factory=list)
    unknown: list[str] = field(default_factory=list)

    @property
    def total(self) -> float:
        return sum(self.values)

    @property
    def last(self) -> float:
        return self.values[-1] if self.values else 0.0


def classify(token: str) -> NumberToken:
    """Map one token to its scale class and numeric contribution.

    Attached punctuation is stripped (``cinquenta,`` is ``cinquenta``) because the
    tokeniser works on whitespace-split text.
    """
    lowered = token.strip(".,;:!?¡¿\"'`()[]{}").lower()

    if _NUMERIC.match(lowered):
        number = float(lowered.replace(",", "."))
        if number < 10:
            scale = SCALE_UNIT
        elif number < 20:
            scale = SCALE_TEEN
        elif number < 100:
            scale = SCALE_TEN
        elif number < 1000:
            scale = SCALE_HUNDRED
        else:
            scale = SCALE_MULTIPLIER
        return NumberToken(raw=token, scale=scale, value=number, kind="digit")

    if lowered in ("meia", "meio"):
        return NumberToken(raw=token, scale=SCALE_UNIT, value=None, kind="half")

    if lowered in SINGLE_DIGITS:
        return NumberToken(
            raw=token, scale=SCALE_UNIT, value=float(SINGLE_DIGITS[lowered]), kind="base"
        )

    if lowered in BASES:
        value = float(BASES[lowered])
        if value < 10:
            scale = SCALE_UNIT
        elif value < 20:
            scale = SCALE_TEEN
        elif value < 100:
            scale = SCALE_TEN
        else:
            scale = SCALE_HUNDRED
        return NumberToken(raw=token, scale=scale, value=value, kind="base")

    if lowered in MULTIPLIERS:
        return NumberToken(
            raw=token, scale=SCALE_MULTIPLIER, value=MULTIPLIERS[lowered], kind="multiplier"
        )

    if lowered in DENOMINATIONS:
        return NumberToken(
            raw=token, scale=SCALE_DENOMINATION, value=DENOMINATIONS[lowered], kind="denomination"
        )

    if lowered in MAGNITUDES:
        return NumberToken(
            raw=token, scale=SCALE_DENOMINATION, value=MAGNITUDES[lowered], kind="magnitude"
        )

    if lowered in CURRENCY_UNITS:
        return NumberToken(raw=token, scale=SCALE_AUXILIARY, value=1.0, kind="currency")

    if lowered in NOTE_DENOMINATORS:
        return NumberToken(raw=token, scale=SCALE_AUXILIARY, value=None, kind="note")

    if lowered in ("e", "de", "com"):
        return NumberToken(raw=token, scale=SCALE_AUXILIARY, value=None, kind="connector")

    return NumberToken(raw=token, scale=-1, kind="unknown")


def is_number_token(token: str) -> bool:
    return classify(token).kind != "unknown"


def _is_boundary(previous: int, current: int) -> bool:
    """True when ``current`` starts a new number after ``previous``.

    Mirrors the table in ``docs/03-nlu-spec.md`` §3.2. The order of these checks
    matters: the specific ungrammatical pairs must be tested before the generic
    "descending magnitudes compose" rule, otherwise ``trinta e dez`` would be
    read as 40 instead of two numbers (30, 10).
    """
    if previous in (SCALE_AUXILIARY, SCALE_DENOMINATION):
        return False
    if current == SCALE_AUXILIARY:
        return False

    # A count before a multiplier or denomination always composes: "doze mil",
    # "duas onças", "nove mil".
    if current in (SCALE_MULTIPLIER, SCALE_DENOMINATION):
        return False

    # Ungrammatical pairs that speakers use to correct themselves. These are
    # checked *before* the descending rule below.
    if previous == SCALE_TEEN and current in (
        SCALE_UNIT,
        SCALE_TEEN,
        SCALE_TEN,
        SCALE_HUNDRED,
    ):
        return True
    if previous == SCALE_TEN and current in (SCALE_TEEN, SCALE_TEN):
        return True

    # Descending magnitudes compose: "mil duzentos", "cento e vinte".
    if current < previous:
        return False

    # Two bare units form a spelled-digit sequence ("sete seis").
    if previous == SCALE_UNIT and current == SCALE_UNIT:
        return False

    return True


def _evaluate_chunk(tokens: list[NumberToken]) -> float:
    """Evaluate one chunk of tokens into a single number."""
    if not tokens:
        return 0.0

    # Spelled digits: two or more bare digits with no connector and no complex
    # word concatenate ("sete seis" -> 76, "cinco zero zero" -> 500).
    if (
        len(tokens) >= 2
        and all(t.kind in {"base", "digit"} and t.scale == SCALE_UNIT for t in tokens)
        and not any(t.raw.lower() == "e" for t in tokens)
    ):
        digits = "".join(str(int(t.value or 0)) for t in tokens)
        return float(digits)

    total = 0.0
    current = 0.0
    last_multiplier = 1.0
    in_cents = False
    cents = 0.0
    note_count: float | None = None

    index = 0
    while index < len(tokens):
        token = tokens[index]
        lowered = token.raw.lower()
        following = tokens[index + 1] if index + 1 < len(tokens) else None

        if token.kind == "note":
            note_count = current if current else 1.0
            current = 0.0
            index += 1
            continue

        if token.kind == "currency":
            if lowered in ("centavo", "centavos"):
                if in_cents:
                    total += cents / 100
                    cents = 0.0
                    in_cents = False
                elif current:
                    total += current / 100
                    current = 0.0
                in_cents = True
            else:
                scaled = current if current else 0.0
                if note_count is not None:
                    scaled *= note_count
                    note_count = None
                total += scaled
                current = 0.0
                last_multiplier = 1.0
            index += 1
            continue

        if token.kind == "half":
            # "meio pau" = 500, "meia dúzia" = 6: half of the *following* magnitude.
            if following is not None and following.kind in (
                "multiplier",
                "denomination",
                "magnitude",
            ):
                total += 0.5 * (following.value or 1.0)
                last_multiplier = following.value or 1.0
                current = 0.0
                index += 2
                continue

            # "cinquenta e meia" = 50.5: half of the previous magnitude.
            if index > 0 and tokens[index - 1].kind == "connector":
                if last_multiplier > 1:
                    total += 0.5 * last_multiplier
                elif current:
                    current += 0.5
                elif total:
                    total += 0.5
                index += 1
                continue

            # Standalone "meia" in bingo/auction means 6 ("meia dúzia" spoken alone).
            current += 6.0
            index += 1
            continue

        if token.kind == "multiplier":
            factor = token.value or 1.0
            base = current if current else 1.0
            total += base * factor
            last_multiplier = factor
            current = 0.0
            index += 1
            continue

        if token.kind in ("denomination", "magnitude"):
            factor = token.value or 1.0
            count = current if current else 1.0
            total += count * factor
            last_multiplier = factor
            current = 0.0
            index += 1
            continue

        if token.kind == "connector":
            index += 1
            continue

        # Plain base number or literal digit.
        value = token.value or 0.0
        if in_cents:
            cents += value
        else:
            current += value
        index += 1

    total += current
    if cents:
        total += cents / 100
    return total


def parse_numbers(tokens: list[str]) -> ParseOutcome:
    """Parse a token list into the numbers it contains.

    ``notas``/``cédulas``/``moedas`` are a prefix quantifier: in
    ``duas notas de cem`` the ``duas`` multiplies the ``cem`` that follows. The
    count is therefore carried across the connector tokens instead of being
    evaluated where it appears.
    """
    outcome = ParseOutcome()
    chunk: list[NumberToken] = []
    last_scale: int | None = None

    def flush() -> None:
        nonlocal chunk, last_scale
        if chunk:
            value = _evaluate_chunk(chunk)
            if value:
                outcome.values.append(float(value))
        chunk = []
        last_scale = None

    pending_note = False
    note_count = 1.0

    for raw in tokens:
        # Punctuation splits numbers ("50, 40, 20" is three bids, not one).
        if not raw or all(not char.isalnum() for char in raw):
            flush()
            pending_note = False
            continue

        token = classify(raw)
        outcome.tokens.append(token)

        if token.kind == "note":
            # Remember the count that precedes the note word; it applies to the
            # number that follows.
            numeric = [t for t in chunk if t.kind in {"base", "digit", "multiplier"} and t.value]
            note_count = numeric[-1].value if numeric else 1.0
            if numeric:
                chunk = chunk[:-1]
            pending_note = True
            continue

        if token.kind == "unknown":
            outcome.unknown.append(raw)
            flush()
            pending_note = False
            continue

        if token.kind == "connector":
            chunk.append(token)
            continue

        if chunk and last_scale is not None and _is_boundary(last_scale, token.scale):
            flush()

        if pending_note:
            # Emit the multiplication directly: "duas notas de cem" -> 200,
            # not the digit-concatenation "2" + "cem" -> 210 that the
            # spelled-digit fast path would produce.
            base = (note_count * (token.value or 1.0)) if token.kind == "base" else None
            if base is not None:
                value = float(base)
                outcome.values.append(value)
                pending_note = False
                note_count = 1.0
                chunk = []
                last_scale = None
                continue
            # Non-numeric quantity ("duas notas velhas") falls back to the
            # accumulated count.
            chunk = [
                NumberToken(raw=str(note_count), scale=SCALE_UNIT, value=note_count, kind="digit"),
                token,
            ]
            last_scale = token.scale
            pending_note = False
            note_count = 1.0
            continue

        chunk.append(token)
        last_scale = token.scale

    flush()
    return outcome


def number_to_words(value: int) -> str:
    """Render a cardinal in Portuguese. Used by tools, not by the runtime."""
    if value < 0:
        raise ValueError("negative values are not supported")

    units = [
        "zero",
        "um",
        "dois",
        "três",
        "quatro",
        "cinco",
        "seis",
        "sete",
        "oito",
        "nove",
    ]
    teens = [
        "dez",
        "onze",
        "doze",
        "treze",
        "quatorze",
        "quinze",
        "dezesseis",
        "dezessete",
        "dezoito",
        "dezenove",
    ]
    tens = [
        "",
        "",
        "vinte",
        "trinta",
        "quarenta",
        "cinquenta",
        "sessenta",
        "setenta",
        "oitenta",
        "noventa",
    ]
    hundreds = [
        "",
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

    if value < 10:
        return units[value]
    if value < 20:
        return teens[value - 10]
    if value < 100:
        ten, unit = divmod(value, 10)
        return tens[ten] if unit == 0 else f"{tens[ten]} e {units[unit]}"
    if value < 1000:
        if value == 100:
            return "cem"
        hundred, rest = divmod(value, 100)
        head = hundreds[hundred]
        return head if rest == 0 else f"{head} e {number_to_words(rest)}"
    if value < 1_000_000:
        thousands, rest = divmod(value, 1000)
        head = "mil" if thousands == 1 else f"{number_to_words(thousands)} mil"
        return head if rest == 0 else f"{head} e {number_to_words(rest)}"
    if value < 1_000_000_000:
        millions, rest = divmod(value, 1_000_000)
        head = "um milhão" if millions == 1 else f"{number_to_words(millions)} milhões"
        return head if rest == 0 else f"{head} e {number_to_words(rest)}"
    raise ValueError("values above one billion are not supported")
