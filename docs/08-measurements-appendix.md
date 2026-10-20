# 08 — Measurements Appendix

Every number in the plan comes from one of the runs below, executed on this machine on
2026-10-19/20. Commands are written to be re-runnable; paths reflect the scratch environment used
during planning (`/tmp/opencode`) — in the project they become `models/` and `data/`.

## 0. Environment

```
CPU     12 threads
RAM     15 GiB (browser/daily use ~5–6 GiB)
GPU     NVIDIA RTX 2070 8 GB (unused)
Disk    btrfs, 37 GB free on the project mount
/tmp    tmpfs 7.8 GB (not persistent)
Python  system 3.14.8; uv 0.13.1; project pins 3.12
ffmpeg  present
vosk    0.3.45 (wheel: py3-none-manylinux2010_x86_64, bundles libvosk.so)
piper   1.8.0 (tooling only)
```

Scratch setup used for the measurements:

```bash
uv venv --python 3.12 /tmp/opencode/v312
uv pip install --python /tmp/opencode/v312/bin/python \
    vosk==0.3.45 numpy scipy piper-tts num2words
curl -o /tmp/opencode/small-pt.zip \
    https://alphacephei.com/vosk/models/vosk-model-small-pt-0.3.zip
unzip -q /tmp/opencode/small-pt.zip -d /tmp/opencode/vosk-small
uv run python -m piper.download_voices pt_BR-faber-medium \
    --download-dir /tmp/opencode/piper-voices
```

## 1. Model metadata

`https://alphacephei.com/vosk/models/model-list.json`, filtered to `lang == "pt"`:

| name | type | size | md5 |
|---|---|---|---|
| `vosk-model-small-pt-0.3` | small | 32 453 112 (30.9 MiB) | `458c69371c5a0b9ab6ee8fa417bf89da` |
| `vosk-model-pt-fb-v0.1.1-pruned` | big | 1 223 922 169 (1.1 GiB) | `1623ac7ab970fc234a56dd1782261300` |
| `vosk-model-pt-fb-v0.1.1-20220516_2113` | big | 1 693 530 883 (1.6 GiB) | `5d259cb674fd52a61c97c1e53282d4b3` |

Unpacked `small`: 53 552 717 bytes, 16 files (`final.mdl`, `Gr.fst`, `HCLr.fst`, `ivector/`,
`phones.txt`, `word_boundary.int`, `mfcc.conf`, `README`).

`mfcc.conf` (small):

```
--sample-frequency=16000
--use-energy=false
--num-mel-bins=20
--num-ceps=20
--low-freq=20
--high-freq=7600
```

`pruned` uses 40 mel bins / 40 ceps (`conf/mfcc.conf`) and a static `graph/HCLG.fst` plus
`rescore/G.carpa`; `graph/words.txt` has 250 006 entries.

## 2. Load time and memory

Fresh process, model only:

| Model | load | RSS after load |
|---|---|---|
| small | 0.19–0.28 s | 145 MB |
| pruned | 1.10–5.82 s | 1 841–2 438 MB |

With numpy + Piper voice loaded in the same process (tools): 426–539 MB (small).

```python
import time, resource
from vosk import Model, SetLogLevel
SetLogLevel(-1)
t0 = time.perf_counter(); m = Model(path); print(time.perf_counter() - t0)
print(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, "MB")
```

## 3. Runtime grammar support (decisive)

Spoken: `"cinquenta reais"`; grammar: `["um","dois","[unk]"]`.

| Model | plain | grammar | `SetGrammar()` runtime |
|---|---|---|---|
| small | `cinquenta reais` | `[unk]` | `[unk]` |
| pruned | `cinquenta reais` | `cinquenta reais` | `cinquenta reais` |

Conclusion: grammar restriction is a **small-only** feature.

## 4. Grammar form and size

### 4.1 Words vs phrases (small)

| Input | word list | phrase list |
|---|---|---|
| `mais vinte` | `vinte` | `mais vinte` |
| `cinquenta reais` | `cinquenta` | `cinquenta reais` |
| `cem mil` | `mil` | `cem mil` |
| `trinta e cinco mil` | `cinco mil` | `trinta e cinco mil` |
| `duzentos e cinquenta mil` | `duzentos cinquenta mil` | `duzentos e cinquenta mil` |
| `cento e vinte e cinco` | `cento e vinte cinco` | `cento e vinte e cinco` |

### 4.2 Accents (small)

| Grammar JSON | Decoded `"duas onças"` |
|---|---|
| `["duas","onças","onca","garça","cem","[unk]"]` (`ensure_ascii=False`) | `duas onças` |
| same, `ensure_ascii=True` | `duas` |
| `["duas","oncas","onca","garca","cem","[unk]"]` | `duas` |

### 4.3 Size / latency (small, 1.68 s utterance, 100 ms chunks)

| Entries | JSON | Build | Total decode | Max chunk |
|---|---|---|---|---|
| 2 379 (curated) | 66 KB | 2 ms | — | ~7 ms |
| 14 202 | 494 KB | 22 ms | — | ~9 ms |
| 102 026 | 4.6 MB | — | — | — |
| 842 562 | 43 MB | 1 571 ms | — | — |
| 3 504 660 | 190 MB | 6 905 ms | — | — |
| 906 000 | 47.6 MB | 1 690 ms | 21–28 ms/utterance | — |

Grammar **content** matters more than size: the curated 2 379-phrase set scored 37/40 on the base
model, while a 14 202-phrase set built from a different template family scored 31/40 and an
842 562-phrase set also scored 31/40 (RTF 0.93). Larger did not mean better.

## 5. Streaming latency

`small`, 100 ms chunks (`3200` bytes), 16 kHz s16le:

| Mode | Utterance | Audio | Wall | Max chunk | Final flush |
|---|---|---|---|---|---|
| plain | `duzentos e cinquenta mil reais` | 1.64 s | 0.37 s | 178.9 ms | 31.0 ms |
| plain | `quarenta e sete` | 0.91 s | 0.35 s | 251.7 ms | 73.6 ms |
| grammar | `duzentos e cinquenta mil reais` | 1.66 s | 0.03 s | 9.8 ms | 8.6 ms |
| grammar | `nove mil novecentos e noventa e nove` | 2.00 s | 0.04 s | 9.6 ms | 11.4 ms |
| grammar | `mais vinte mais vinte mais sete` | 1.76 s | 0.03 s | 9.1 ms | 15.9 ms |

`pruned`, plain: 7.6–15.9 ms max chunk, RTF ≈ 0.04.

## 6. Silence padding

40-case suite, TTS, scored through `vosk-python` evaluator:

| Model | LS | plain no pad | plain padded | grammar no pad | grammar padded |
|---|---|---|---|---|---|
| small | 1.0 | 21/40 | 25/40 | 26/40 | **29/40** |
| small | 0.8 | 22/40 | 26/40 | 25/40 | **29/40** |
| pruned | 1.0 | 35/40 | 34/40 | n/a | n/a |
| pruned | 0.8 | 32/40 | 32/40 | n/a | n/a |

Padding = 300 ms leading + 400 ms trailing zeros. These are base-model runs with the legacy
curated grammar; the LM-rebuilt model is measured in §15.

## 7. Accuracy, 40-case suite

Scored with `vosk_python.evaluator.AuctionBidEvaluator` (value, `has_addition`,
`has_multiplication`):

| Model | LS | mode | value | add | mult |
|---|---|---|---|---|---|
| small | 1.0 | plain | 25/40 | 39/40 | 38/40 |
| small | 1.0 | grammar | 29/40 | 39/40 | 38/40 |
| small | 0.8 | plain | 26/40 | 39/40 | 38/40 |
| small | 0.8 | grammar | 29/40 | 38/40 | 38/40 |
| pruned | 1.0 | plain | 34/40 | 38/40 | 39/40 |
| pruned | 1.0 | grammar (ignored) | 34/40 | 38/40 | 39/40 |
| pruned | 0.8 | plain | 32/40 | 38/40 | 38/40 |
| pruned | 0.8 | grammar (ignored) | 33/40 | 39/40 | 39/40 |

Text-only (no ASR):

| Parser | value | add | mult |
|---|---|---|---|
| `whisper-auction-pt-br/auction_engine/parser.py` | 38/40 | 38/40 | 40/40 |
| `vosk-python/src/vosk_python/evaluator.py` | **40/40** | **40/40** | **40/40** |

`whisper` failures: `mais dez mais dez` → 10.0 (expected 20.0), `mais vinte mais vinte mais sete` →
7.0 (expected 47.0).

## 8. N-best behavior (small, plain)

`"mais vinte"`:

| rank | confidence | text |
|---|---|---|
| 1 | 34.79 | `mas vindo de` |
| 2 | 34.79 | `mais` |
| 3 | 34.74 | `vinde` |
| 4 | 33.87 | `mas vindo` |
| 5 | 33.85 | `mais vinte` |

`"duzentos e cinquenta mil"`:

| rank | confidence | text |
|---|---|---|
| 1 | 76.56 | `duzentos e cinquenta mil` |
| 2 | 73.64 | `n duzentos e cinquenta mil` |
| 3 | 73.27 | `a duzentos e cinquenta mil` |

Conclusion: decoder confidence cannot be trusted for selection; parser scoring is required.

## 9. Word-level confidences (plain, small)

`"mais vinte mais vinte mais sete"` → `vinte mais vinte e sete`:

| word | conf |
|---|---|
| vinte | 0.767 |
| mais | 0.715 |
| vinte | 0.999 |
| e | 0.923 |
| sete | 0.973 |

Word confidences are usable for trace/diagnostics, not for arbitration.

## 10. Spelled digits

300 ms lead + 300 ms tail, `small`:

| Spoken | no pad plain | padded plain | padded digit grammar |
|---|---|---|---|
| `cinco zero` | `zero` | `cinco zero` | `cinco zero` |
| `cinco zero zero` | `zero zero` | `cinco zero zero` | `cinco zero zero` |
| `dois zero zero` | `zero` | `zero` | `dois zero zero` |
| `um dois cinco` | `cinco` | `um dos cinco` | `um dois cinco` |
| `quatro sete` | `` | `site` | `quatro sete` |
| `zero cinco` | `` | `zero cinco` | `zero cinco` |
| `nove nove nove` | `nova nova` | `nova nova nove` | `nove nove nove` |

Conclusion: spelled-digit mode needs both padding and its own grammar subset.

## 11. Domain vocabulary coverage

Probe: `Model.vosk_model_find_word(word) >= 0`.

Small model OOV (16 of ~200 domain words):

```
arremate  barrigudas  bujões  dáblio  efe  ene  goiás  guará
gê  milhãozinho  oitão  oncinha  quadruplico  quintuplico  xuxa  ágá
```

Pruned model OOV (6):

```
dáblio  milhãozinho  quadruplico  quintuplico  triplico  ágá
```

Legacy `grammar_builder.py` output against `small`: 190 candidates → 164 valid, **26 dropped**
(`arremate`, `barao`, `baroes`, `barrigudas`, `bilhao`, `bujoes`, `cedula`, `cedulas`, `duzia`,
`duzias`, `garca`, `garcas`, `goias`, `leao`, `leilao`, `micos`, `milhao`, `nao`, `ofereco`,
`oitao`, `onca`, `oncas`, `pregao`, `quadruplico`, `quintuplico`, `xuxa`) because it emits
unaccented variants that Vosk ignores.

## 12. ASR failure catalog (small, plain, LS 1.0)

| Spoken | Decoded |
|---|---|
| `dobro de cinquenta` | `disse enquanto` / `cinquenta` |
| `passo` | `` (empty) |
| `duas onças` | `` (empty) |
| `um barão` | `` (empty) |
| `três placas` | `placas` |
| `cem mil` | `` (empty, plain) |
| `trinta e cinco mil` | `cinco mil` |
| `meia dúzia` | `doze` |
| `dois patinhos na lagoa` | `esses patinhos na lagoa` |
| `idade de cristo` | `de que isto` |
| `três oitão` | `leitão` |
| `justiça de goiás` | `justiça` |
| `um pau` | `paulo` |
| `meio pau` | `paulo` / `pau` |
| `cinquenta pila` | `pílula` |
| `cem conto` | `encontro` |
| `uma garoupa` | `uma garota` |
| `lobo guará` | `agora` |
| `um milhão` | `` / `união` |

These are the concrete targets of the LM rebuild (P1) and the NLU fallbacks (P2).

## 13. Legacy parser behavior (context for the rewrite)

`whisper-auction-pt-br` parser, text-only:

| Utterance | value | add | mult | action | note |
|---|---|---|---|---|---|
| `mais dez mais dez` | 10.00 | False | False | BID | should be 20.00, add=True |
| `mais vinte mais vinte mais sete` | 7.00 | False | False | BID | should be 47.00, add=True |
| `quero` | 0.00 | False | False | NONE | cleaned to `zero` (phonetic false positive) |
| `sem lance` | 100.00 | False | False | BID | cleaned to `cem lance`; should be PASS |
| `lote um` | 1.00 | False | False | BID | should be LOT |
| `dou-lhe uma` | 1.00 | False | False | BID | should be CHANT |
| `cem conto` | 100.00 | False | False | BID | should be 100 000 under D-8 |

`vosk-python` evaluator on the same text inputs: 40/40.

## 14. Reproduction script (planned)

`tools/bench_matrix.py` will reproduce §5–§7 with:

```bash
uv run python -m tools.bench_matrix \
    --models small \
    --grammar off,on \
    --padding none,300/400 \
    --speeds 1.0,0.8,0.65 \
    --corpus data/corpus/tts.jsonl \
    --out data/reports/matrix.md
```

## 15. LM rebuild prototype (2026-10-20)

### 15.1 Toolchain

| Item | Version | Result |
|---|---|---|
| openfst | 1.8.3 | **fails** on GCC 16 (`FstImpl::operator=`, `std::unique_ptr`) |
| openfst | 1.7.9 | **fails** on GCC 16 |
| openfst | **1.8.4** | **builds**, `--enable-far --enable-ngram-fsts`, `-std=c++17` |
| opengrm-ngram | 1.3.4 | **fails** (`-std=c++11`, `FLAGS_*` vs `FST_FLAGS_*`) |
| opengrm-ngram | 1.3.7 | **fails** (same) |
| opengrm-ngram | **1.3.17** | **builds** with `CPPFLAGS=-I<openfst>/include` |

Install size: 42 MB (openfst) + 110 MB (opengrm build tree).

### 15.2 Corpus

| Metric | Value |
|---|---|
| Lines | 426 420 |
| Tokens | 4 591 812 |
| Sources | number frames (0–999, `N mil`, `N mil e M`), 54 idioms ×60, 53 intents ×60, 20 k chained bids |
| OOV dropped | 540 lines; tokens: `barrigudas`, `bujões`, `dou-lhe`, `goiás`, `guará`, `oitão`, `xuxa` |
| Build time | 1.7 s (whole pipeline) |

### 15.3 Result

| Model | value | add | mult | RTF | max chunk | load | RSS |
|---|---|---|---|---|---|---|---|
| base `small` | 26–28/40 | 40/40 | 38/40 | 0.18–0.30 | 130–205 ms | 0.20 s | 498 MB |
| `small` + new `Gr.fst` | **37–38/40** | 40/40 | 39/40 | **0.026–0.031** | **10–13 ms** | 0.18 s | 498 MB |

`Gr.fst`: 4 133 967 bytes (base: 16 007 119), 775 states, 4 189 arcs, ngram type.

Runtime grammar still restricts on the rebuilt model (`["um","dois"]` + `cinquenta reais` → `cem`,
i.e. forced into the allowed vocabulary).

### 15.4 Grammar interaction (important)

| Config | value |
|---|---|
| base + curated grammar (2 379 phrases) | 37/40 |
| LM rebuilt, plain | 37–38/40 |
| LM rebuilt + curated grammar (2 379) | 37/40 |
| LM rebuilt + intent-only grammar (103) | **18/40** |
| LM rebuilt + full number grammar (33 120) | 36/40 |

An intent-only grammar **breaks numbers**, because the grammar replaces the LM entirely. Numbers
must always be in the grammar if a grammar is used. With the LM rebuilt, grammar is optional.

### 15.5 Residual failures after LM rebuild

| Spoken | Decoded |
|---|---|
| `nove mil novecentos e noventa e nove` | `noventa mil e novecentos e noventa e nove` |
| `mais dez mais dez` | `mais três mais dez` |
| `dobro` | `` / `subo` |

These become the next corpus iteration's targets (long compounds, chained repetition, bare
multiplier).

## 16. Notes on validity

- All audio in §5–§12 is **synthetic** (Piper `pt_BR-faber-medium`), not human. It measures relative
  behavior (model A vs B, grammar on vs off, padding, speed), not absolute field accuracy.
- `length_scale` 0.65–0.8 is a rough proxy for rushed auction speech; it does not reproduce shouting,
  breath noise, or overlapping voices.
- The 40-case suite was written from the legacy project's test intentions plus the measured failure
  catalog, so it is biased toward known-hard cases. That is deliberate for regression, not for
  headline accuracy.
