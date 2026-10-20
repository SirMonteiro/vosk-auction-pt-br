"""Runtime phrase grammar for Vosk.

Vosk's ``KaldiRecognizer(model, rate, grammar)`` replaces the language model with
a finite-state grammar built from a list of strings. Three rules are non-obvious
and fail *silently* when broken (see ``docs/04-asr-and-grammar.md``):

1. **Phrases, not words.** A list of isolated words removes connectors, so
   ``cem mil`` decodes as ``mil``. Every entry here is a phrase.
2. **Accents are mandatory.** ``garca`` is ignored by the decoder while ``garça``
   works. JSON must be dumped with ``ensure_ascii=False``.
3. **Numbers are mandatory.** An intent-only grammar scored 18/40 because the
   grammar *replaces* the LM rather than biasing it.

The builder validates every phrase against the model vocabulary and reports what
it dropped, so a silently shrinking grammar cannot go unnoticed.
"""

from __future__ import annotations

import json
from collections.abc import Iterable
from dataclasses import dataclass, field

from auction_demo.nlu.lexicon import (
    BINGO,
    CHANT_PHRASES,
    CORRECT_PHRASES,
    CURRENCY_UNITS,
    DELTA_MARKERS,
    FILLERS,
    MAGNITUDES,
    MULTIPLIER_WORDS,
    PASS_PHRASES,
    QUERY_PHRASES,
)

UNITS = ["um", "dois", "três", "quatro", "cinco", "seis", "sete", "oito", "nove"]
TEENS = [
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

#: Prefixes that turn a bare number into a framed bid ("mais vinte reais").
FRAME_PREFIXES = ("", "mais", "cobre", "vou de", "subo", "aumento")

#: Numbers that get the full prefix × suffix treatment. Chosen to cover the most
#: common auction bids and a spread of magnitudes.
FRAME_NUMBERS = (
    "cinco",
    "dez",
    "vinte",
    "vinte e cinco",
    "trinta",
    "quarenta",
    "cinquenta",
    "sessenta",
    "setenta",
    "oitenta",
    "noventa",
    "cem",
    "cento e cinquenta",
    "duzentos",
    "duzentos e cinquenta",
    "trezentos",
    "quatrocentos e setenta",
    "quinhentos",
    "mil",
    "mil e quinhentos",
    "dois mil",
    "cinco mil",
    "dez mil",
    "cem mil",
    "duzentos e cinquenta mil",
    "quarenta e sete mil e quinhentos",
    "um milhão",
    "um milhão e duzentos mil",
)



@dataclass(slots=True)
class GrammarReport:
    """What the builder produced and what it had to drop."""

    phrases: list[str]
    dropped: list[str] = field(default_factory=list)
    counts: dict[str, int] = field(default_factory=dict)

    @property
    def size(self) -> int:
        return len(self.phrases)

    def to_json(self) -> str:
        """Serialise for ``KaldiRecognizer``.

        ``ensure_ascii=False`` is required: escaped codepoints such as
        ``gar\\u00e7a`` are treated as unknown words by Vosk.
        """
        return json.dumps(self.phrases, ensure_ascii=False)

    def summary(self) -> str:
        lines = [f"grammar: {self.size} phrases, {len(self.to_json()) / 1024:.0f} KB"]
        for name, count in sorted(self.counts.items()):
            lines.append(f"  {name:<12s}{count:>7d}")
        if self.dropped:
            lines.append(f"  dropped (OOV) {len(self.dropped):>4d}")
        return "\n".join(lines)


def _below_1000() -> list[str]:
    """Every cardinal from 0 to 999, in the forms a speaker actually uses."""
    values: list[str] = []
    values.extend(UNITS)
    values.extend(TEENS)
    values.extend(TENS)
    values.extend(f"{ten} e {unit}" for ten in TENS for unit in UNITS)
    values.append("cem")
    values.extend(HUNDREDS)
    values.extend(f"{hundred} e {below}" for hundred in HUNDREDS for below in _below_100())
    return values


def _below_100() -> list[str]:
    values = list(UNITS) + list(TEENS) + list(TENS)
    values.extend(f"{ten} e {unit}" for ten in TENS for unit in UNITS)
    return values


def _canonicalize(phrase: str) -> str:
    """Restore accents and expand hyphens that the lexicon stores both ways.

    The lexicon keeps unaccented aliases (``nao quero``) and hyphenated forms
    (``beija-flor``) for robustness, but Vosk only knows ``não quero`` and
    ``beija flor``. Building the grammar from the raw alias list dropped 72
    entries silently before this existed.
    """
    tokens = phrase.replace("-", " ").split()
    return " ".join(_ACCENT_FIX.get(token, token) for token in tokens)


# ---------------------------------------------------------------------------
# Unaccented/hyphenated forms present in the lexicon, mapped to the accented
# spelling the model actually knows. Anything genuinely absent from the model
# (``oitão``, ``xuxa``, ``goiás``, ``quadruplico``) is reported as dropped
# rather than guessed at.
# ---------------------------------------------------------------------------
ACCENT_FIX: dict[str, str] = {
    "nao": "não",
    "mao": "mão",
    "ofereco": "ofereço",
    "barao": "barão",
    "baroes": "barões",
    "milhao": "milhão",
    "milhoes": "milhões",
    "duzia": "dúzia",
    "duzias": "dúzias",
    "garca": "garça",
    "garcas": "garças",
    "onca": "onça",
    "oncas": "onças",
    "tres": "três",
    "oitao": "oitão",
    "goias": "goiás",
    "guara": "guará",
    "leao": "leão",
    "leilao": "leilão",
    "pregao": "pregão",
    "ninguem": "ninguém",
    "justica": "justiça",
    "voce": "você",
    "tambem": "também",
    "ja": "já",
    "so": "só",
    "esta": "está",
    "ta": "tá",
    "quadruplo": "quádruplo",
    "quintuplo": "quíntuplo",
    "bico": "bico",
    "lagos": "lagoa",
}
_ACCENT_FIX = ACCENT_FIX


def build_grammar(
    *,
    max_phrases: int = 6_000,
    hard_limit: int = 15_000,
    include_frames: bool = True,
    is_known_word=None,
) -> GrammarReport:
    """Build the phrase list, deterministically and within budget.

    ``is_known_word`` is an optional callable (normally
    ``Model.vosk_model_find_word``) used to drop entries the model cannot
    pronounce; dropped entries are reported instead of silently vanishing.
    """
    if max_phrases > hard_limit:
        raise ValueError(
            f"max_phrases={max_phrases} exceeds the measured hard limit {hard_limit}; "
            "decoder latency collapses above it without accuracy gain"
        )

    buckets: dict[str, set[str]] = {}

    # --- 1. numbers ------------------------------------------------------
    # Split into two buckets so truncation never drops the thousands forms:
    # "duzentos e cinquenta mil" is far more valuable than "novecentos e noventa e nove".
    below_1000 = _below_1000()
    buckets["numbers"] = set(below_1000)
    # Thousands: "N mil" plus the compound "N mil e M", where M is the most
    # common trailing part. The full cross product is 10^6 entries; restricting
    # M to units/teens/hundreds covers every auction-plausible bid.
    thousands_units = [
        "e um",
        "e dois",
        "e três",
        "e quatro",
        "e cinco",
        "e dez",
        "e quinze",
        "e vinte",
        "e vinte e cinco",
        "e trinta",
        "e quarenta",
        "e cinquenta",
        "e sessenta",
        "e setenta",
        "e oitenta",
        "e noventa",
        "e cem",
        "e duzentos",
        "e quinhentos",
    ]
    buckets["thousands"] = {f"{value} mil" for value in below_1000}
    # Compound "N mil e M" is valuable but only for round N: it produces 1000
    # auction-plausible entries instead of 20 000, so it fits in the budget.
    # Compound "N mil e M" is valuable but only for round N plus the few
    # non-round numbers that appear in real auctions; the full cross product is
    # 1000 × 19 entries and does not fit.
    round_hundreds = [
        "cem",
        "duzentos",
        "trezentos",
        "quatrocentos",
        "quinhentos",
        "seiscentos",
        "setecentos",
        "oitocentos",
        "novecentos",
    ]
    compound: set[str] = set()
    for head in round_hundreds:
        compound.add(head)
        for tail in thousands_units:
            compound.add(f"{head} mil {tail}")
    buckets["compound_thousands"] = compound

    # --- 2. currency frames ----------------------------------------------
    # Frames are keyword-substituted templates over a *representative* number
    # set, not the full 0-999 expansion: the grammar is a search restriction,
    # not a corpus, and 3 × 1000 frames buys nothing measurable.
    frames: set[str] = set()
    if include_frames:
        for number in FRAME_NUMBERS:
            for prefix in ("", "mais", "cobre", "vou de", "subo", "aumento"):
                for suffix in ("", "reais", "conto", "pila"):
                    phrase = f"{prefix} {number} {suffix}".strip()
                    frames.add(phrase)
    buckets["frames"] = frames

    # --- 3. idioms and slang ---------------------------------------------
    # Framed forms are hand-written rather than generated: automatic
    # "duas <token>" produces nonsense like "duas micos" or "uma lobos guará",
    # which the model cannot pronounce and the parser would score anyway.
    idioms: set[str] = set(BINGO)

    # Multiword/singular fauna forms as a speaker says them.
    idiom_frames = {
        "beija flor",
        "tartaruga",
        "garça",
        "garcinha",
        "arara",
        "ararinha",
        "mico",
        "mico leão",
        "onça",
        "onça pintada",
        "oncinha",
        "garoupa",
        "peixe",
        "lobo",
        "lobo guará",
        "tartarugas",
        "garças",
        "araras",
        "onças",
        "garoupas",
        "peixes",
        "lobos",
    }
    idioms |= idiom_frames

    # Natural quantifier/article pairs, spelled explicitly.
    idioms |= {
        "um beija flor",
        "uma tartaruga",
        "uma garça",
        "uma garcinha",
        "uma arara",
        "uma ararinha",
        "um mico",
        "um mico leão",
        "uma onça",
        "uma onça pintada",
        "uma oncinha",
        "uma garoupa",
        "um peixe",
        "um lobo",
        "um lobo guará",
        "duas tartarugas",
        "duas garças",
        "duas araras",
        "duas onças",
        "duas oncinhas",
        "duas garoupas",
        "dois peixes",
        "dois lobos",
        "dois micos",
    }

    # Slang magnitudes read naturally with these quantifiers.
    # "três placas" must be present: it is one of the documented reference cases.
    magnitudes: set[str] = set(MAGNITUDES)
    for word in MAGNITUDES:
        for quantifier in ("um", "dois", "três", "quatro", "cinco"):
            magnitudes.add(f"{quantifier} {word}")
    idioms |= magnitudes
    # "conto"/"pila"/"prata" are currency synonyms, used with any quantity.
    for word in CURRENCY_UNITS:
        idioms.add(word)
    buckets["idioms"] = idioms

    # --- 4. intents -------------------------------------------------------
    intents: set[str] = set()
    intents |= PASS_PHRASES
    intents |= CORRECT_PHRASES
    intents |= QUERY_PHRASES
    intents |= CHANT_PHRASES
    intents |= set(MULTIPLIER_WORDS)
    intents |= DELTA_MARKERS
    # Hyphenated chant forms are not in the model vocabulary; the spoken form is
    # "dou lhe uma". Add both spellings so the grammar lists something real.
    intents |= {phrase.replace("-", " ") for phrase in CHANT_PHRASES}
    for word in ("meio", "meia"):
        intents.add(word)
    # "meio pau", "meia dúzia" and the multiplier phrases.
    for phrase in (
        "meio pau",
        "meia dúzia",
        "uma dúzia",
        "dúzia e meia",
        "meio caminho",
        "meia telha",
    ):
        intents.add(phrase)
    for number in ("cinquenta", "cem", "duzentos", "mil"):
        for word in MULTIPLIER_WORDS:
            intents.add(f"{word} de {number}")
    buckets["intents"] = intents

    # --- 5. fillers -------------------------------------------------------
    buckets["fillers"] = set(FILLERS)

    # --- 6. spelled digits ------------------------------------------------
    digits = ["zero", *UNITS]
    buckets["digits"] = {f"{a} {b}" for a in digits for b in digits}

    # --- assemble with a deterministic priority ---------------------------
    # Priority: intents and idioms are never trimmed (they are the smallest and
    # the most valuable), then numbers below 1000, then the thousands forms,
    # then frames, then spelled digits.
    # Priority is by value, not size: intents and idioms are always kept, then a
    # compact layer of high-value compound numbers *only for round thousands*
    # ("quarenta e sete mil e quinhentos"), then frames, then the exhaustive
    # 0-999 and full "N mil" sets. Filling the budget with the exhaustive set
    # first leaves no room for frames, which measurably hurts accuracy.
    priority = [
        "intents",
        "idioms",
        "fillers",
        "frames",
        "compound_thousands",
        "thousands",
        "numbers",
        "digits",
    ]
    ordered: list[str] = []
    seen: set[str] = set()
    counts: dict[str, int] = {}

    for name in priority:
        added = 0
        for phrase in sorted(buckets[name]):
            if len(ordered) >= max_phrases:
                break
            canonical = _canonicalize(phrase)
            if canonical in seen:
                continue
            seen.add(canonical)
            ordered.append(canonical)
            added += 1
        counts[name] = added

    # --- validate against the model vocabulary ---------------------------
    dropped: list[str] = []
    if is_known_word is not None:
        kept: list[str] = []
        for phrase in ordered:
            if all(is_known_word(token) >= 0 for token in phrase.split()):
                kept.append(phrase)
            else:
                dropped.append(phrase)
        ordered = kept

    ordered.append("[unk]")
    return GrammarReport(phrases=ordered, dropped=dropped, counts=counts)


def phrases_from_texts(texts: Iterable[str]) -> GrammarReport:
    """Build a grammar from explicit phrases (used by tests and tools)."""
    unique = sorted({" ".join(text.split()).lower() for text in texts if text.strip()})
    return GrammarReport(phrases=[*unique, "[unk]"], counts={"custom": len(unique)})


def grammar_stats(phrases: list[str]) -> dict[str, int]:
    """Cheap introspection used by ``/health``."""
    return {
        "phrases": len(phrases),
        "tokens": sum(len(phrase.split()) for phrase in phrases),
        "bytes": len(json.dumps(phrases, ensure_ascii=False)),
    }
