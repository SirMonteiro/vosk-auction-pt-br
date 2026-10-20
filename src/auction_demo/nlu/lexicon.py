"""Domain lexicon for Brazilian Portuguese auction bidding.

Data only: no parsing logic lives here. All canonical keys are **accented** lower
case, because Vosk silently drops unaccented grammar entries (see
``docs/04-asr-and-grammar.md`` §2.1).

Magnitude semantics follow the user's glossary recorded in
``docs/07-risks-and-decisions.md`` (D-8, D-8b, D-8c):

===========  ==========  =====================================================
term         factor      note
===========  ==========  =====================================================
``barão``    1_000       from the Cruzeiro note that carried Barão do Rio Branco
``pau``      1_000       counts thousands ("cinco paus" = R$ 5 000)
``placa``    100_000     large sums, car-trade slang
``conto``    1           synonym for reais ("cem conto" = R$ 100)
``pila``     1           synonym for reais
``prata``    1           synonym for reais
===========  ==========  =====================================================
"""

from __future__ import annotations

from typing import Final

# ---------------------------------------------------------------------------
# Numerals
# ---------------------------------------------------------------------------

#: 0-19, tens and hundreds, including the colloquial augmentatives that appear
#: in fast auction speech ("duzentão", "cinquentinha", "milzinho").
BASES: Final[dict[str, int]] = {
    # units
    "zero": 0,
    "um": 1,
    "uma": 1,
    "unzinho": 1,
    "umzinho": 1,
    "dois": 2,
    "duas": 2,
    "doizinho": 2,
    "doisão": 2,
    "três": 3,
    "tres": 3,
    "quatro": 4,
    "cinco": 5,
    "cincão": 5,
    "cinquinho": 5,
    "seis": 6,
    "sete": 7,
    "oito": 8,
    "nove": 9,
    # teens
    "dez": 10,
    "dezão": 10,
    "dezinho": 10,
    "onze": 11,
    "doze": 12,
    "treze": 13,
    "quatorze": 14,
    "catorze": 14,
    "quinze": 15,
    "quinzinho": 15,
    "dezesseis": 16,
    "dezessete": 17,
    "dezoito": 18,
    "dezenove": 19,
    # tens
    "vinte": 20,
    "vintão": 20,
    "vintinho": 20,
    "vintola": 20,
    "trinta": 30,
    "trintão": 30,
    "trintinha": 30,
    "trintola": 30,
    "quarenta": 40,
    "quarentão": 40,
    "quarentinha": 40,
    "cinquenta": 50,
    "cinquentão": 50,
    "cinquentinha": 50,
    "sessenta": 60,
    "sessentão": 60,
    "setenta": 70,
    "setentão": 70,
    "oitenta": 80,
    "oitentão": 80,
    "noventa": 90,
    "noventão": 90,
    # hundreds
    "cem": 100,
    "cento": 100,
    "cemzão": 100,
    "cemzinho": 100,
    "senzão": 100,
    "duzentos": 200,
    "duzento": 200,
    "duzentas": 200,
    "duzentão": 200,
    "duzentinho": 200,
    "trezentos": 300,
    "trezento": 300,
    "trezentas": 300,
    "trezentão": 300,
    "trezentinho": 300,
    "quatrocentos": 400,
    "quatrocento": 400,
    "quatrocentas": 400,
    "quatrocentão": 400,
    "quinhentos": 500,
    "quinhento": 500,
    "quinhentas": 500,
    "quinhentão": 500,
    "seiscentos": 600,
    "seiscento": 600,
    "seiscentas": 600,
    "seiscentão": 600,
    "setecentos": 700,
    "setecento": 700,
    "setecentas": 700,
    "setecentão": 700,
    "oitocentos": 800,
    "oitocento": 800,
    "oitocentas": 800,
    "oitocentão": 800,
    "novecentos": 900,
    "novecento": 900,
    "novecentas": 900,
    "novecentão": 900,
}

#: Multipliers that scale the preceding number.
MULTIPLIERS: Final[dict[str, float]] = {
    "mil": 1_000.0,
    "milzão": 1_000.0,
    "milzinho": 1_000.0,
    "milhão": 1_000_000.0,
    "milhões": 1_000_000.0,
    "milhao": 1_000_000.0,
    "milhoes": 1_000_000.0,
    "bilhão": 1_000_000_000.0,
    "bilhões": 1_000_000_000.0,
    "trilhão": 1_000_000_000_000.0,
    "trilhões": 1_000_000_000_000.0,
}

#: Slang units that multiply the preceding number by a fixed factor.
#: ``conto``/``pila``/``prata`` are deliberately *not* here: they are currency
#: synonyms handled by :data:`CURRENCY_UNITS` (factor 1).
MAGNITUDES: Final[dict[str, float]] = {
    "barão": 1_000.0,
    "barões": 1_000.0,
    "barao": 1_000.0,
    "baroes": 1_000.0,
    "pau": 1_000.0,
    "paus": 1_000.0,
    "placa": 100_000.0,
    "placas": 100_000.0,
}

#: Real banknote fauna. Each value is the note's face value in reais.
DENOMINATIONS: Final[dict[str, float]] = {
    "beija-flor": 1.0,
    "beija flor": 1.0,
    "tartaruga": 2.0,
    "tartarugas": 2.0,
    "garça": 5.0,
    "garças": 5.0,
    "garcinha": 5.0,
    "arara": 10.0,
    "araras": 10.0,
    "ararinha": 10.0,
    "mico": 20.0,
    "micos": 20.0,
    "mico-leão": 20.0,
    "mico leão": 20.0,
    "micos leões": 20.0,
    "onça": 50.0,
    "onças": 50.0,
    "oncinha": 50.0,
    "oncinhas": 50.0,
    "onça-pintada": 50.0,
    "onça pintada": 50.0,
    "garoupa": 100.0,
    "garoupas": 100.0,
    "peixe": 100.0,
    "peixes": 100.0,
    "lobo": 200.0,
    "lobos": 200.0,
    "lobo-guará": 200.0,
    "lobo guará": 200.0,
    "lobos guará": 200.0,
    "lobinho": 200.0,
    # Dozens: "meia dúzia" = 6, "dúzia e meia" = 18.
    "dúzia": 12.0,
    "dúzias": 12.0,
    "duzia": 12.0,
    "duzias": 12.0,
}

#: Single digits, used for the spelled-digit fast path ("sete seis" -> 76).
SINGLE_DIGITS: Final[dict[str, int]] = {
    "zero": 0,
    "um": 1,
    "uma": 1,
    "dois": 2,
    "duas": 2,
    "três": 3,
    "tres": 3,
    "quatro": 4,
    "cinco": 5,
    "seis": 6,
    "sete": 7,
    "oito": 8,
    "nove": 9,
}

#: Words that close the current number without scaling it.
CURRENCY_UNITS: Final[frozenset[str]] = frozenset(
    {
        "real",
        "reais",
        "conto",
        "contos",
        "continho",
        "pila",
        "pilas",
        "prata",
        "pratas",
        "centavo",
        "centavos",
    }
)

#: Quantifiers for "duas notas de cem".
NOTE_DENOMINATORS: Final[frozenset[str]] = frozenset(
    {"nota", "notas", "cédula", "cédulas", "cedula", "cedulas", "moeda", "moedas"}
)

# ---------------------------------------------------------------------------
# Bingo / jogo do bicho nicknames
# ---------------------------------------------------------------------------

#: Numbers commonly used as bid values because they have a memorable nickname.
BINGO: Final[dict[str, int]] = {
    "dois patinhos na lagoa": 22,
    "dois patinhos": 22,
    "casal de patos": 22,
    "dois patos": 22,
    "idade de cristo": 33,
    "a idade de cristo": 33,
    "justiça de goiás": 38,
    "justica de goias": 38,
    "três oitão": 38,
    "tres oitao": 38,
    "oitão": 38,
    "quá quá quá": 44,
    "qua qua qua": 44,
    "bico largo": 44,
    "quatro patos": 44,
    "fim do primeiro tempo": 45,
    "uma boa ideia": 51,
    "boa ideia": 51,
    "a cana": 51,
    "dois portugueses numa perna só": 55,
    "dois cachorros do padre": 55,
    "cachorro do padre": 55,
    "um tapa atrás da orelha": 66,
    "as barrigudas": 66,
    "duas velhas": 66,
    "um pra cima e outro pra baixo": 69,
    "um que vai outro que vem": 69,
    "par de machados": 77,
    "dois machados": 77,
    "dois bujões": 88,
    "dois bujoes": 88,
    "duas gordas": 88,
    "as pernas da avó": 11,
    "pernas da avó": 11,
    "as pernas da xuxa": 11,
    "pernas da xuxa": 11,
    "dois palitos": 11,
    "entre na fila": 11,
    "pau e bola": 10,
    "camisa dez": 10,
    "craque de bola": 10,
    "uma dúzia e meia": 18,
    "meia dúzia": 6,
    "meia duzia": 6,
    "uma dúzia": 12,
    "uma duzia": 12,
    "o azarado": 13,
    "deu azar": 13,
    "número do zagallo": 13,
    "a debutante": 15,
    "debutante": 15,
    "a menina bonita": 15,
    "menina bonita": 15,
    "menina moça": 15,
    "maioridade": 18,
    "rapazinho alegre": 24,
    "número feio": 24,
    "dia de natal": 25,
    "natal": 25,
    "meteu num 31": 31,
    "o louco": 35,
    "meia telha": 50,
    "meio caminho": 50,
}

# ---------------------------------------------------------------------------
# Intents
# ---------------------------------------------------------------------------

#: Phrases that mean "I am out".
PASS_PHRASES: Final[frozenset[str]] = frozenset(
    {
        "passo",
        "tô fora",
        "to fora",
        "desisto",
        "sem lance",
        "não quero",
        "nao quero",
        "deixa passar",
        "não dou",
        "nao dou",
        "abandono",
        "saio",
        "na minha vez não",
    }
)

#: Phrases that retract or amend the previous bid.
CORRECT_PHRASES: Final[frozenset[str]] = frozenset(
    {
        "errei",
        "corrige",
        "corrigir",
        "cancela",
        "cancelar",
        "anula",
        "anular",
        "volta",
        "desconsidera",
        "falei errado",
        "não era isso",
        "nao era isso",
    }
)

#: Phrases that ask for information instead of bidding.
QUERY_PHRASES: Final[frozenset[str]] = frozenset(
    {
        "quanto tá",
        "quanto ta",
        "quanto está",
        "quanto esta",
        "qual o lance",
        "qual é o valor",
        "qual o valor",
        "tá em quanto",
        "ta em quanto",
        "repete o lance",
    }
)

#: Auctioneer chant. These must never be parsed as bids.
CHANT_PHRASES: Final[frozenset[str]] = frozenset(
    {
        "dou-lhe uma",
        "dou lhe uma",
        "dou-lhe duas",
        "dou lhe duas",
        "dou-lhe três",
        "dou lhe tres",
        "vendido",
        "arrematado",
        "bateu o martelo",
        "ninguém dá mais",
        "ninguem da mais",
    }
)

#: Words that carry no numeric meaning and are dropped before parsing.
FILLERS: Final[frozenset[str]] = frozenset(
    {
        "redondo",
        "redonda",
        "cheio",
        "cheia",
        "liso",
        "lisa",
        "quebrado",
        "quebrada",
        "sem troco",
        "na lata",
        "tá dado",
        "ta dado",
        "pelo valor de",
        "na mão",
        "na mao",
        "pra levar",
        "para levar",
    }
)

#: Prefixes that turn a number into an incremental bid ("mais vinte").
DELTA_MARKERS: Final[frozenset[str]] = frozenset(
    {
        "mais",
        "cobre",
        "cobrindo",
        "subo",
        "aumento",
        "aumentando",
        "ofereço",
        "ofereco",
        "dou",
        "vou de",
        "em cima",
    }
)

#: Multiplier verbs and nouns. ``dobro`` -> 2, ``triplo`` -> 3, etc.
MULTIPLIER_WORDS: Final[dict[str, float]] = {
    "dobro": 2.0,
    "dobra": 2.0,
    "dobre": 2.0,
    "dobrar": 2.0,
    "duplico": 2.0,
    "duplica": 2.0,
    "duplicar": 2.0,
    "duplo": 2.0,
    "triplo": 3.0,
    "triplica": 3.0,
    "triplico": 3.0,
    "triplicar": 3.0,
    "quádruplo": 4.0,
    "quadruplo": 4.0,
    "quadruplica": 4.0,
    "quadruplico": 4.0,
    "quíntuplo": 5.0,
    "quintuplo": 5.0,
    "quintuplica": 5.0,
    "metade": 0.5,
}

#: Function words that carry no numeric meaning. Everything else that must never
#: be phonetically repaired is added automatically in :data:`STOPWORDS` below.
_FUNCTION_WORDS: Final[frozenset[str]] = frozenset(
    {
        "a",
        "o",
        "as",
        "os",
        "um",
        "uma",
        "uns",
        "umas",
        "de",
        "do",
        "da",
        "dos",
        "das",
        "e",
        "em",
        "com",
        "por",
        "para",
        "pra",
        "que",
        "é",
        "eh",
        "tá",
        "ta",
        "não",
        "nao",
        "sim",
        "meu",
        "minha",
        "seu",
        "sua",
        "lhe",
        "eu",
        "ele",
        "ela",
        "isso",
        "era",
        "vou",
        "vai",
        "dou",
        "na",
        "no",
        "pelo",
        "pela",
        "sem",
        "mais",
        "menos",
    }
)


def _tokens_of(phrases: frozenset[str]) -> frozenset[str]:
    """Split every phrase into its individual tokens."""
    return frozenset(token for phrase in phrases for token in phrase.split())


#: Function words plus every token used by an intent phrase. Nothing in this set
#: is ever phonetically repaired: rewriting `quero` -> `zero` or `sem` -> `cem`
#: silently turns a pass into a bid (see ``docs/03-nlu-spec.md`` §7).
STOPWORDS: Final[frozenset[str]] = (
    _FUNCTION_WORDS
    | _tokens_of(PASS_PHRASES)
    | _tokens_of(CORRECT_PHRASES)
    | _tokens_of(QUERY_PHRASES)
    | _tokens_of(CHANT_PHRASES)
    | _tokens_of(FILLERS)
    | DELTA_MARKERS
)


def all_number_words() -> frozenset[str]:
    """Every token that can contribute to a numeric value.

    Used by the grammar builder and by the phonetic repair guard: a token in this
    set must never be rewritten into a different word class.
    """
    words: set[str] = set()
    words |= set(BASES)
    words |= set(MULTIPLIERS)
    words |= set(MAGNITUDES)
    words |= set(DENOMINATIONS)
    words |= set(CURRENCY_UNITS)
    words |= set(NOTE_DENOMINATORS)
    words |= {"meia", "meio", "centavo", "centavos"}
    return frozenset(words)


def all_idioms() -> dict[str, int]:
    """Every multi-word phrase that expands to a number."""
    return {**BINGO}
