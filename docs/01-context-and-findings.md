# 01 — Context and Findings

## 1. What exists today

Three sibling projects under `/home/gabriel/projects/auction`. Only the first is a git repository
with history; the other two are uncommitted working trees.

### 1.1 `whisper-auction-pt-br/` (git, 4 commits)

| Path | Role |
|---|---|
| `auction_engine/lexicon.py` | Richest domain lexicon: fauna banknotes, bingo nicknames, magnitudes, pass phrases |
| `auction_engine/parser.py` | Original number/bid parser (`parse_subexpression`, `extract_numbers_from_segment`, `AuctionBidEvaluator`) |
| `auction_engine/phonetics.py` | `phonological_repair` regex pipeline + idiom replacement |
| `auction_engine/phonetic_alphabet.py` | PB grapheme→phonetic encoder, Levenshtein helpers |
| `auction_engine/phonetic_matcher.py` | Fuzzy token repair against domain vocabulary |
| `auction_engine/currency.py`, `models.py` | `R$ 1.000,00` formatting, `BidResult` dataclass |
| `asr/whisper_transcriber.py` | HF Transformers Whisper, GPU/CPU, prompt biasing, RMS gate |
| `web/app.py`, `web/templates.py` | Flask PTT page, `/transcribe` |
| `server.py` | CLI entry point |
| `tests/` | 441 lines: parser, phonetic alphabet, web API |

Known issues, verified by execution:

- Parser regressions: `"mais dez mais dez"` → **10.0** (should be 20.0), `"mais vinte mais vinte mais sete"` → **7.0** (should be 47.0). Text-level score **38/40**.
- `"quero"` → cleaned to `zero`; `"sem lance"` → `cem lance` → **R$ 100** instead of PASS; `"lote um"` → bid **1.0**.
- `"dou-lhe uma"` (auctioneer chant) → bid **1.0**. There is no intent layer.
- `BidResult` lacks `int_value` / `has_sum` (present only in the `vosk-python` copy).

### 1.2 `vosk-python/` (uncommitted)

| Path | Role |
|---|---|
| `src/vosk_python/evaluator.py` | **Best semantic reference**: `has_sum`, `int_value`, leading-`mais` chain handling. Text-level **40/40** |
| `src/vosk_python/transcriber.py` | Vosk model load, hot-swap, streaming partials, stdout block, ffmpeg conversion |
| `src/vosk_python/grammar_builder.py` | Runtime vocabulary builder — **broken**, see below |
| `src/vosk_python/app.py` | Flask + `flask-sock`, `/status`, `/models`, `/models/swap`, `/transcribe`, `/stream` |
| `templates/index.html`, `static/js/app.js` (554 lines), `static/css/style.css` (728 lines) | Partial-text preview, metrics cards, model dropdown, copy buttons |
| `tests/test_app.py` | 140 lines, includes synthetic WAV upload |

`grammar_builder.py` defects (measured against `vosk-model-small-pt-0.3`):

- It emits **isolated words**, which removes `e`/`de` connectors from the decoder path. With a word
  grammar, `"cem mil"` decodes as `"mil"` and `"trinta e cinco mil"` as `"cinco mil"`.
- It adds accented and unaccented variants, but Vosk **requires the accented form**; unaccented
  entries are silently ignored. Net effect: 26 of 190 domain words are dropped, including
  `garça`, `onça`, `milhão`, `barão`, `leilão`, `não`, `dúzia`, `oitão`, `xuxa`.
- It has no size budget and no validation report.

Maintenance note: the existing `.venv` is broken — `bin/python` points at
`~/.local/share/uv/python/cpython-3.12-linux-x86_64-gnu/bin/python3.12`, which no longer exists.
The new project creates its own environment.

### 1.3 `numeros/` (uncommitted)

Whisper/Nemo/OpenVINO experiments (`distill/ptt_whisper_phonetic_money*.py`), plus
`ds2-sn-brsdv2-finetune-split-*.pth` checkpoints and NeMo configs (CTC, GRU, 16 kHz, pt-BR).
Not used by the demo; relevant only as a lead for a future acoustic model.

## 2. Why Whisper was abandoned

The user's report: `whisper.unity` + `whisper.cpp` worked but showed high latency and crashes on
AMD cards and older NVIDIA (GTX 1050 Ti), in that order of severity. VRAM is shared with the Unity
game, so the model has to be small, and Vulkan backend stability is not under our control.

Vosk changes the constraint: **CPU only, 31 MB model, no VRAM at all**. On this machine:

| | Whisper small (ONNX present: 1.1 GB) | Vosk small (31 MB) |
|---|---|---|
| Device | GPU (Vulkan/CUDA) or slow CPU | CPU |
| VRAM | ~1–2 GB | 0 |
| Model load | seconds | 0.19–0.28 s |
| RSS | large | 145 MB (model only) |

The demo therefore targets Vosk only. Whisper files stay untouched as reference.

## 3. Environment measured

| Item | Value |
|---|---|
| CPU | 12 threads |
| RAM | 15 GiB total, user reports 5–6 GiB used by browser/daily apps |
| GPU | RTX 2070 8 GB (not used by the demo) |
| Project disk | btrfs, 37 GB free |
| `/tmp` | tmpfs, 7.8 GB, **not persistent** |
| Python | system 3.14.8; `uv` 0.13.1; project will pin 3.12 |
| ffmpeg | present |
| espeak/piper system voices | absent (Piper installed via pip for tooling) |
| Docker/Podman | unusable (rootless permission errors) |

Consequence: **no container-based Kaldi build**. A local source build was required; the working
versions are pinned in `05-finetuning-plan.md` (openfst 1.8.4 + opengrm 1.3.17).

## 4. Measured Vosk behavior (baseline, before the LM rebuild)

Everything in this section was measured with the **unmodified** `vosk-model-small-pt-0.3`.
Section 5 shows what changes after the LM rebuild — read both before drawing conclusions.

All numbers reproducible; see `08-measurements-appendix.md`.

### 4.1 Runtime grammar works on `small` and is ignored on `pruned`

Decisive test — speak `"cinquenta reais"` with grammar `["um","dois","[unk]"]`:

| Model | plain | grammar `["um","dois"]` | `SetGrammar()` at runtime |
|---|---|---|---|
| `vosk-model-small-pt-0.3` | `cinquenta reais` | **`[unk]`** | `[unk]` (works) |
| `vosk-model-pt-fb-v0.1.1-pruned` | `cinquenta reais` | **`cinquenta reais`** | `cinquenta reais` (ignored) |

The `small` model ships `HCLr.fst` + `Gr.fst` (dynamic graph) and honors the phrase list. The
`pruned` model has a static `HCLG.fst` and silently ignores it. This invalidates any plan that
assumes grammar restriction on `pruned`; grammar is a `small`-only feature.

### 4.2 Grammar must be a phrase list, not a word list

| Input | word list | phrase list |
|---|---|---|
| `cem mil` | `mil` | `cem mil` |
| `trinta e cinco mil` | `cinco mil` | `trinta e cinco mil` |
| `duzentos e cinquenta mil` | `duzentos cinquenta mil` (drops `e`) | `duzentos e cinquenta mil` |

### 4.3 Grammar size budget

| Phrases | JSON | Build | Max chunk latency | RTF |
|---|---|---|---|---|
| 2 379 (curated) | 66 KB | 2 ms | ~7 ms | 0.03 |
| 14 202 | 494 KB | 22 ms | ~9 ms | 0.03 |
| 842 562 | 43 MB | 1.57 s | — | 0.93 |
| 3 504 660 | 190 MB | 6.9 s | — | 3.9–4.25 |

No accuracy gain above ~2.4 k curated. **Hard cap: 15 000 phrases.** The builder must refuse, not
degrade.

### 4.4 Accents are mandatory

`["garça","onça","cem"]` decodes `duas onças` correctly. `["garca","onca","cem"]` yields `duas`.
`ensure_ascii=True` also breaks it (the JSON becomes `gar\u00e7a` and Vosk does not unescape).
**Rule: `json.dumps(..., ensure_ascii=False)` and accented strings only.**

### 4.5 Nested alternatives are unsupported

`[["oi","ola"],"tudo","[unk]"]` raises `Failed to create a recognizer`. Only flat phrase lists.

### 4.6 Silence padding helps

300 ms leading + 400 ms trailing silence on the 40-case suite:

| Model | plain, no pad | plain, padded | grammar, no pad | grammar, padded |
|---|---|---|---|---|
| small | 21/40 | 25/40 | 26/40 | **29/40** |
| pruned | 35/40 | 34/40 | (ignored) | 34/40 |

The demo pads the utterance and the browser keeps a 250 ms pre-roll ring buffer.

> Note: these runs used the *legacy* curated grammar and the unmodified model. With the rebuilt LM
> (`01` §5) the same suite reaches 37–38/40 and padding matters less, but it stays in the pipeline
> because it is free and protects the first syllable on real mics.

### 4.7 Confidence and n-best

`SetWords(True)` + `SetMaxAlternatives(n)` work and return word timings/confidences, but the
ranking is unreliable: for `"mais vinte"` the top alternative is `"mas vindo de"`, with
`"mais vinte"` fifth. Plan: **score the n-best list with the parser** instead of trusting the
decoder's ranking.

### 4.8 Model facts

- `vosk-model-small-pt-0.3`: 31 MB zip, 53 MB unpacked, load 0.19–0.28 s, 145 MB RSS (fresh
  process), 498 MB peak with numpy + Piper loaded.
- `vosk-model-pt-fb-v0.1.1-pruned`: 1.1 GB zip, 1.7 GB unpacked, load 1.1–5.8 s, 1.84–2.44 GB RSS.
- `vosk_model_find_word()` is the only vocabulary probe for `small` (no `words.txt` in the model
  directory). `pruned` has `graph/words.txt` with 250 006 entries.
- Small-model OOV domain words (16): `arremate`, `barrigudas`, `bujões`, `dáblio`, `efe`, `ene`,
  `goiás`, `guará`, `gê`, `milhãozinho`, `oitão`, `oncinha`, `quadruplico`, `quintuplico`, `xuxa`, `ágá`.
  `pruned` misses 6: `dáblio`, `milhãozinho`, `quadruplico`, `quintuplico`, `triplico`, `ágá`.

### 4.9 End-to-end accuracy (TTS corpus, 40 cases)

TTS: Piper `pt_BR-faber-medium`, `length_scale` 1.0 (normal) and 0.8 (rushed), padded, streamed in
100 ms chunks, scored through `vosk-python`'s evaluator.

| Model | LS | plain value | grammar value | add | mult | RTF (grammar) |
|---|---|---|---|---|---|---|
| small | 1.0 | 25/40 | 29/40 | 39/40 | 38/40 | 0.03 |
| small | 0.8 | 26/40 | 29/40 | 38/40 | 38/40 | 0.03 |
| pruned | 1.0 | 34/40 | 34/40 (ignored) | 38/40 | 39/40 | 0.12 |
| pruned | 0.8 | 32/40 | 32/40 (ignored) | 39/40 | 39/40 | 0.11 |

Text-level (no ASR) the `vosk-python` evaluator scores **40/40**. The gap to 29–36 is therefore
**ASR**, not parsing — which is exactly what the fine-tuning phase targets.

Recurring ASR failures with `small`: `dobro de cinquenta` → `cinquenta`, `duas onças` → `duas`/empty,
`meia dúzia` → `doze`, `um milhão` → `um`, `cem mil` → `mil`, `trinta e cinco mil` → `cinco mil`,
`mais vinte mais vinte mais sete` → loses a `mais`, `dobro` alone → empty.

## 5. LM rebuild is proven on this machine (new, 2026-10-20)

The full Vosk "level 1" pipeline was executed end-to-end during planning, not just planned:

```
farcompilestrings --fst_type=compact --symbols=words.txt --keep_symbols text.txt \
  | ngramcount --order=3 \
  | ngrammake --method=kneser_ney \
  | fstconvert --fst_type=ngram > Gr.fst
```

Toolchain that actually compiles here: **openfst 1.8.4** + **opengrm-ngram 1.3.17**, both built
from source with `-std=c++17` (openfst 1.8.3 and opengrm 1.3.4/1.3.7 both fail on GCC 16).

Measured effect on the 40-case suite (TTS, padded, `vosk_python` evaluator):

| Model | value | add | mult | RTF | load | RSS |
|---|---|---|---|---|---|---|
| `small` base | 26–28/40 | 40/40 | 38/40 | 0.18–0.30 | 0.20 s | 498 MB |
| `small` + rebuilt `Gr.fst` | **37–38/40** | 40/40 | 39/40 | **0.026–0.031** | 0.18 s | 498 MB |

The rebuilt `Gr.fst` is 4.1 MB versus 16.0 MB in the base model, decoding is **~8× faster**, and
the model still honours runtime grammar restriction (verified: grammar `["um","dois","cem"]` forces
`cinquenta reais` to decode as `cem` instead of passing it through).

Two findings that constrain the design:

1. **The grammar must contain numbers.** An intent-only grammar (103 phrases, intents + currency
   words, relying on the LM for numbers) collapses to **18/40** — `quarenta e sete` → empty,
   `duzentos e cinquenta mil` → `cinquenta mil`. Grammar and numbers are inseparable.
2. **Corpus words must exist in the model vocabulary.** `farcompilestrings` hard-fails on any
   token absent from `words.txt`; the corpus builder must filter against the symbol table and
   report the dropped set (`barrigudas`, `bujões`, `goiás`, `guará`, `oitão`, `xuxa`, `dou-lhe`).

Best observed combinations:

| Config | value | Notes |
|---|---|---|
| base + curated grammar (~2.4 k) | 37/40 | best without touching the model |
| LM rebuilt, plain | 37–38/40 | best overall, simplest runtime |
| LM rebuilt + curated grammar | 37/40 | no gain over LM plain |
| LM rebuilt + intent-only grammar | 18/40 | **anti-pattern** |

Conclusion: with the LM rebuilt, runtime grammar becomes optional (useful for OOD rejection, not
for accuracy). Without it, grammar is essential. The demo ships both paths and defaults to
LM + curated grammar.

Remaining LM failures (targets for the next corpus iteration): `nove mil novecentos e noventa e
nove` → `noventa mil e novecentos e noventa e nove` (long compound numbers under-represented),
`mais dez mais dez` → `mais três mais dez` (chained repetition), `dobro` alone → empty/`subo`.

## 6. What is reusable, what is rewritten

| Component | Decision |
|---|---|
| `vosk-python` evaluator semantics | **Port** (40/40) into `nlu/parser.py`, then extend |
| `whisper-auction-pt-br` lexicon | **Port** as data, normalized to accented canonical forms |
| Phonetic repair | **Rewrite** — current version creates false positives (`quero`→`zero`, `sem lance`→`cem lance`) |
| `grammar_builder.py` | **Rewrite** — phrase-based, accented, validated, budgeted |
| Whisper transcriber | Not used |
| `vosk-python` Flask/WS skeleton | **Reference only**; new server in `server/app.py` |
| `vosk-python` UI | Reference for layout; new UI with n-best/trace panels |
| `numeros/ds2-*` | Untouched; future acoustic lead |
