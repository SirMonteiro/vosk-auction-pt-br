# 03 — NLU Specification

Everything here is pure text→structure. No audio, no model, no I/O. This is the module the future
Unity client must mirror or call.

## 1. Pipeline

```
raw text
  │ 1. normalize      lowercase, collapse spaces, unify unicode, keep digits
  │ 2. idiom pass     longest-match replacement (fauna, bingo, bordões)
  │ 3. phonetic pass  only OOV tokens, margin-guarded (see §7)
  │ 4. intent pass    CHANT → CORRECT → PASS → QUERY → LOT → MULTIPLY → BID
  │ 5. number pass    tokenizer → chunker → cardinal evaluator
  │ 6. assemble       BidResult with legacy + new fields
  ▼
BidResult
```

Order matters. `"dou-lhe uma"` must hit CHANT before `uma` becomes a bid. `"sem lance"` must hit
PASS before the phonetic pass can turn `sem` into `cem`. `"errei, cinquenta"` must hit CORRECT
before the number.

## 2. Lexicon (single source of truth)

`nlu/lexicon.py` holds data only, all canonical forms **accented**:

| Table | Contents |
|---|---|
| `BASES` | 0–19, tens, hundreds, with augmentatives/diminutives (`duzentão`, `cinquentinha`, `milzinho`) |
| `MULTIPLIERS` | `mil`, `milhão/milhões`, `bilhão/bilhões`, `trilhão` |
| `MAGNITUDES` | `barão/barões`, `placa/placas`, `pau/paus`, `conto/contos` = ×1000 (see D-8); `pila/pilas`, `prata/pratas` = ×1 |
| `DENOMINATIONS` | fauna banknotes: `beija-flor` 1, `tartaruga` 2, `garça` 5, `arara` 10, `mico`/`mico-leão` 20, `onça`/`onça-pintada`/`oncinha` 50, `garoupa`/`peixe` 100, `lobo`/`lobo-guará`/`lobinho` 200 |
| `BINGO` | 22 `dois patinhos na lagoa`, 33 `idade de cristo`, 38 `justiça de goiás`/`três oitão`, 44 `quá-quá-quá`/`bico largo`, 45 `fim do primeiro tempo`, 51 `uma boa ideia`/`a cana`, 55 `dois portugueses numa perna só`, 66 `um tapa atrás da orelha`, 69 `um pra cima e outro pra baixo`, 77 `par de machados`, 88 `dois bujões`, 11 `pernas da avó`/`pernas da xuxa`/`dois palitos`, 10 `pau e bola`/`camisa dez`, 18 `uma dúzia e meia`/`maioridade`, 6 `meia dúzia`, 12 `uma dúzia`, 13 `o azarado`/`deu azar`, 15 `a debutante`/`menina bonita`, 24 `rapazinho alegre`/`número feio`, 25 `dia de natal`, 31 `meteu num 31`, 35 `o louco`, 50 `meia telha`/`meio caminho` |
| `PASS` | `passo`, `tô fora`, `desisto`, `sem lance`, `não quero`, `deixa passar`, `não dou`, `abandono`, `saio`, `na minha vez não` |
| `CORRECT` | `errei`, `corrige`, `corrigir`, `cancela`, `cancelar`, `anula`, `volta`, `desconsidera`, `falei errado`, `não era isso` |
| `QUERY` | `quanto tá`, `quanto está`, `qual o lance`, `qual é o valor`, `tá em quanto`, `repete o lance` |
| `CHANT` | `dou-lhe uma`, `dou-lhe duas`, `dou-lhe três`, `vendido`, `arrematado`, `bateu o martelo`, `ninguém dá mais` |
| `FILLER` | `redondo`, `cheio`, `liso`, `quebrado`, `sem troco`, `na lata`, `tá dado`, `pelo valor de`, `vou de`, `na mão`, `pra levar`, `e fechando` |
| `STOPWORDS` | function words + all intent words — **never** phonetically repaired |

Aliases are stored per entry; the canonical key is the display form. Every table entry gets a test.

## 3. Number grammar

### 3.1 Token classes

| Class | Scale | Examples |
|---|---|---|
| `DIGIT` | 1 | `um`, `dois`, `três`, `meia` (bingo) |
| `TEEN` | 2 | `dez`…`dezenove` |
| `TEN` | 3 | `vinte`…`noventa`, `duzentão`? no — hundreds |
| `HUNDRED` | 4 | `cem`, `cento`, `duzentos`…`novecentos` |
| `MULTIPLIER` | 5 | `mil`, `milhão`, `bilhão` |
| `DENOMINATION` | 6 | `onça`, `garoupa`, `lobo` |
| `CURRENCY` | 0 | `reais`, `pila`, `prata`, `centavos` (`conto` and `pau` are `MAGNITUDES`, not currency units) |
| `CONNECTOR` | — | `e`, `de`, `com` |

### 3.2 Chunking

A new chunk starts when the scale goes **backwards illegally**:

| Previous → next | Same chunk? |
|---|---|
| DIGIT → DIGIT (no `e`) | yes → spelled digits (`sete seis` = 76) |
| DIGIT/TEEN/TEN/HUNDRED → MULTIPLIER or DENOMINATION | yes (`duas` + `onças`) |
| MULTIPLIER → lower | yes (`mil` + `duzentos`) |
| HUNDRED → TEN/TEEN/DIGIT | yes (`duzentos` + `vinte`) |
| TEN → DIGIT | yes (`vinte` + `um`) |
| TEN → TEN or TEN → TEEN | **no** (`trinta e dez` → 30, 10) |
| TEEN → DIGIT (even with `e`) | **no** (`doze e três` → 12, 3) |
| anything → CURRENCY | closes chunk |

When several chunks survive: if all are joined by `mais`, sum them; otherwise **take the last**
(the speaker corrected themselves or repeated the chant). This reproduces the measured 40/40.

### 3.3 Cardinal evaluation (per chunk)

State: `total`, `current`, `last_magnitude`.

| Token | Action |
|---|---|
| base | `current += value` |
| `mil` | `current = max(current,1) * 1000`; `total += current`; `current = 0` |
| `milhão/milhões` | `current = max(current,1) * 1e6`; `total += current`; `current = 0` |
| `bilhão/bilhões` | ×1e9, same pattern |
| denomination | `total += max(current,1) * denom`; `current = 0` |
| magnitude (`barão`, `placa`, `pau`, `conto`) | `total += max(current,1) * factor`; `current = 0` |
| `pila`, `prata`, `real`, `reais` | close `current` into `total` (factor 1) |
| `centavos` | `total += current / 100`; `current = 0`; switch to cents mode |
| `meia/meio` after `e` | `+0.5 × last_magnitude` (or `+0.5` if none) |
| `meia` alone | `6` (bingo) — documented exception |
| `meio` before `pau`/`milhão` | `0.5 × factor` |

Spelled digits: a chunk of ≥ 2 DIGIT tokens with no `e` and no complex word concatenates:
`sete seis` → 76, `cinco zero zero` → 500, `um dois cinco` → 125.

### 3.4 Accepted forms (each becomes a test)

| Utterance | Value |
|---|---|
| `quarenta e sete` | 47 |
| `cento e vinte e cinco` | 125 |
| `mil e quinhentos` / `mil quinhentos` | 1500 |
| `dois mil e quinhentos` | 2500 |
| `quarenta e sete mil e quinhentos` | 47 500 |
| `nove mil novecentos e noventa e nove` | 9999 |
| `um milhão e duzentos mil` | 1 200 000 |
| `meio milhão` | 500 000 |
| `um milhão e meio` | 1 500 000 |
| `duas onças` / `onça e meia` | 100 / 75 |
| `meia dúzia` / `uma dúzia` / `dúzia e meia` | 6 / 12 / 18 |
| `um barão` / `três placas` / `meio pau` | 1000 / 3000 / 500 |
| `cinquenta pila` / `cem conto` | 50 / 100 000 (see D-8) |
| `cinquenta reais e vinte centavos` | 50.20 |
| `cinquenta k` / `dois mi` | 50 000 / 2 000 000 (acronyms, see §6) |
| `sete seis` / `cinco zero zero` | 76 / 500 (spelled digits) |

### 3.5 Rejected / downgraded forms

| Utterance | Result |
|---|---|
| `trinta e dez` | 10 (last chunk) |
| `doze e três` | 3 (last chunk) |
| `50, 40, 20` | 20 (last) |
| `cinquenta, cinquenta, cinquenta` | 50 (last, no sum) |
| `quero` | UNKNOWN, no bid (never `zero`) |
| `lote um` | LOT, not bid 1 |

## 4. Intents

Detection order is fixed; the first match wins.

| # | Intent | Triggers | Output |
|---|---|---|---|
| 1 | `CHANT` | `dou-lhe uma/duas/três`, `vendido`, `arrematado`, `bateu o martelo`, `ninguém dá mais` | `action_type=CHANT`, `value=0` |
| 2 | `CORRECT` | `errei`, `corrige`, `cancela`, `anula`, `volta`, `falei errado` | `action_type=CORRECT`, `value=0`; if a number is also present, `target_value` carries it |
| 3 | `PASS` | PASS table | `action_type=PASS`, `value=0` |
| 4 | `QUERY` | QUERY table | `action_type=QUERY`, `value=0` |
| 5 | `LOT` | `lote <n>`, `primeiro/segundo/terceiro lote` | `action_type=LOT`, `lot=<n>` |
| 6 | `MULTIPLY` | `dobro`, `triplo`, `quádruplo`, `quíntuplo`, `metade`, verbs `dobra/duplica/triplica/quadruplica` | see §5 |
| 7 | `BID` | any number found | see §5 |
| 8 | `UNKNOWN` | otherwise | `action_type=NONE`, `reason` |

## 5. Bid semantics

### 5.1 Fields

| Field | Meaning |
|---|---|
| `value` | final numeric value (absolute reais, or delta, see `value_mode`) |
| `int_value` | `int(round(value))` |
| `value_mode` | `absolute` \| `delta` \| `sum` \| `multiply` |
| `has_addition` | legacy: `True` iff `value_mode == "delta"` or a `+` chain was summed |
| `has_sum` | legacy alias of `has_addition` (kept identical for compatibility) |
| `has_multiplication` | `True` iff a multiplier verb/noun was applied |
| `action_type` | `BID` \| `PASS` \| `MULTIPLY_ONLY` \| `NONE` \| `CHANT` \| `QUERY` \| `CORRECT` \| `LOT` |
| `bid_kind` | `BID_ABSOLUTE` \| `BID_DELTA` \| `BID_SUM` \| `BID_MULTIPLY` \| `MULTIPLY_ONLY` \| `PASS` \| `CORRECT` \| `QUERY` \| `CHANT` \| `LOT` \| `UNKNOWN` |

### 5.2 Rules

1. **Single number, no leading marker** → absolute. `cinquenta` → 50, `value_mode=absolute`.
2. **Leading marker + single number** → delta. `mais vinte`, `cobre cinquenta`, `subo trinta`,
   `aumento vinte`, `mil em cima` → value = the number, `value_mode=delta`, `has_addition=True`.
   The game adds it to the current bid; the demo only reports it.
3. **Two or more numbers joined by `mais`** → sum. `vinte mais vinte` → 40, `value_mode=sum`,
   `has_addition=False` (absolute calculated bid) unless the utterance **starts** with `mais`
   (`mais vinte mais vinte mais sete` → 47, `has_addition=True`, `value_mode=delta`).
4. **Multiple numbers without connectors** → last one wins, `value_mode=absolute`.
5. **Multiplier + number** → number × factor, `has_multiplication=True`, `value_mode=multiply`.
6. **Multiplier alone** → `MULTIPLY_ONLY`, value = factor (legacy: `dobro` → 2).
7. **Filler words are removed** before parsing (`redondo`, `cheio`, `sem troco`, `na lata`…).
8. **Unknown words do not block a bid** if a valid number exists; they go to `trace.ignored`.

### 5.3 Reference cases (must stay green)

| Utterance | value | has_addition | has_multiplication | action_type |
|---|---|---|---|---|
| `mais vinte` | 20 | True | False | BID |
| `vinte mais vinte` | 40 | False | False | BID |
| `mais vinte mais vinte mais sete` | 47 | True | False | BID |
| `mais dez mais dez` | 20 | True | False | BID |
| `cinquenta, cinquenta, cinquenta` | 50 | False | False | BID |
| `50, 40, 20` | 20 | False | False | BID |
| `dobro` | 2 | False | True | MULTIPLY_ONLY |
| `dobro de cinquenta` | 100 | False | True | BID |
| `triplo de cinquenta` | 150 | False | True | BID |
| `metade de cem` | 50 | False | True | BID |
| `passo` / `tô fora` | 0 | False | False | PASS |
| `errei` | 0 | False | False | CORRECT |
| `dou-lhe uma` | 0 | False | False | CHANT |
| `lote três` | 0 | False | False | LOT |

## 6. Acronyms and abbreviations

Spoken-game forms, all optional but cheap to support:

| Form | Meaning | Guard |
|---|---|---|
| `K`, `ca`, `ká` | ×1000 | only immediately after a number (`cinquenta k` = 50 000) |
| `mi` | ×1 000 000 | only after a number (`dois mi`) |
| `bi` | ×1 000 000 000 | only after a number |
| `pct` | percent | out of scope for bids; mapped to UNKNOWN |
| `R$` | reais | already handled as currency unit |

`ca`→`k` is a phonetic repair that must **never** fire on `cá`, `casa`, `cana`; guard = previous
token is a number and the token is exactly `ca`/`ká`/`k`.

## 7. Phonetic repair (rewritten)

Rules, in order:

1. **Skip protected tokens**: STOPWORDS, intent words, pass/correct/query/chant phrases.
2. **Skip known vocabulary**: exact match in any lexicon table.
3. **Skip short tokens**: length < 4 (except the explicit `ca`→`k` rule).
4. **Candidate set by class**: a number-like token may only map to numbers, a fauna token only to
   fauna, etc. Never cross classes.
5. **Threshold**: similarity ≥ 0.80 **and** margin ≥ 0.08 over the runner-up, otherwise keep the
   token unchanged.
6. **Explicit table first** (measured, deterministic), fuzzy only as fallback:

| Input | Output | Guard |
|---|---|---|
| `sixenta`, `sisenta`, `seicenta` | `sessenta` | — |
| `novem` | `nove` | only before `mil` |
| `noventenove`, `noventenói` | `noventa e nove` | — |
| `noventa em nove` | `noventa e nove` | — |
| `mas` | `mais` | only before a number or currency word |
| `sem` | `cem` | only before a number/currency and never in `sem lance` |
| `mew zom`, `mil zom` | `milzão` | — |
| `muzinho` | `milzinho` | — |
| `vindes`, `vídeo`, `vite` | `vinte` | — |
| `cinco itinhas` | `cinquentinha` | — |
| `duza então`, `duzentão` variants | `duzentão` | — |
| `quinza então` | `quinhentão` | — |
| `tresentão`, `trezentos` variants | `trezentão` | — |
| `seissentos` | `seiscentos` | — |
| `ca` | `k` | previous token is a number |

7. **Never** repair a token that would change the intent (`quero`→`zero` is forbidden).

Each explicit rule gets a unit test; the fuzzy fallback gets property tests over the domain lexicon
asserting no false positives on a 200-word Portuguese stoplist.

## 8. Output contract

```json
{
  "raw_transcript": "mais vinte mais vinte mais sete",
  "normalized_text": "+ vinte + vinte + sete",
  "nbest": [
    {"text": "mais vinte mais vinte mais sete", "score": 47.0, "chosen": true},
    {"text": "mais vinte mais vinte e sete",    "score": 7.0,  "chosen": false}
  ],
  "value": 47.0,
  "int_value": 47,
  "currency": "R$ 47,00",
  "value_mode": "delta",
  "has_addition": true,
  "has_sum": true,
  "has_multiplication": false,
  "action_type": "BID",
  "bid_kind": "BID_DELTA",
  "numbers_found": [20.0, 20.0, 7.0],
  "idioms_found": [],
  "confidence": 0.82,
  "latency_ms": 38.4,
  "reason": null,
  "trace": {}
}
```

`trace` is populated only with `--debug` (per-stage tokens, scales, boundaries, repairs).

## 9. Fun extensions beyond the legacy lexicon

These are proposals, not yet in the reference code. Each is a one-line table entry plus a test.

| Utterance | Behavior |
|---|---|
| `vou de cinquenta` | absolute 50 (filler `vou de`) |
| `cinquenta na mão` | absolute 50 |
| `cinquenta pra levar` | absolute 50 |
| `cinquenta e fechando` | absolute 50 + `closing=true` |
| `mil em cima` | delta 1000 |
| `cinquenta em cima` | delta 50 |
| `sobe mais` (no number) | UNKNOWN, `reason="needs_current_bid"` |
| `tampa` / `cobre o lance` (no number) | UNKNOWN, `reason="needs_current_bid"` |
| `pelo valor de cem` | absolute 100 |
| `cinquenta redondo` | absolute 50 (filler) |
| `sem troco` | filler |
| `duas dezenas` | 20 |
| `meia centena` | 50 |
| `um milheiro` | 1000 |
| `um par de onças` | 100 |
| `onça e meia` | 75 |
| `errei, era cinquenta` | CORRECT with `target_value=50` |

The game layer (not the demo) decides what to do with `closing`, `target_value` and
`needs_current_bid`; the demo only reports them.

## 10. Currency formatting

`R$ 1.234,56` — dot thousands, comma decimals, always two decimals. Negative never occurs.
Zero prints `R$ 0,00`.
