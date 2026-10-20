# 05 — Fine-tuning and Adaptation Plan

Goal: make the model more assertive on **fast auction speech**, **spelled numbers**, and the
**game's specific vocabulary** — and be able to *prove* an improvement with a repeatable evaluation.

This phase starts **after** the demo baseline exists (decision D-2 in `07-risks-and-decisions.md`),
because the demo's corpus + eval harness are the prerequisites.

## 1. The adaptation ladder (Vosk's own framing)

| Level | What changes | Effort | Feasible here? | Expected gain |
|---|---|---|---|---|
| **0. Runtime grammar** | decoder search space at runtime | hours | **yes, done in the demo** | huge latency win, rejects off-domain speech |
| **1. Language model rebuild** | `Gr.fst` recompiled from in-domain text | 1–2 days | **yes** | main accuracy lever for phrasing/probabilities |
| **2. Lexicon + graph rebuild** | add OOV words with pronunciations | 2–4 days | yes, fiddly | unlocks `oitão`, `quadruplico`, `xuxa`, `goiás` |
| **3. Acoustic fine-tune** | the AM itself | weeks | **no recipe for `small`** | would fix rushed speech, but out of reach |

Important consequence: **levels 1–2 cannot add new words by themselves if the word is missing from
the vocabulary** (level 1 only re-weights existing words; level 2 adds words). Level 3 is where
"fast speech" is truly learned — everything before it is a proxy.

## 2. Level 1 — Language model rebuild

### 2.1 Prerequisites

Kaldi's `tools` (openfst + opengrm). **Verified working on this machine on 2026-10-20** — the
pipeline below was executed, not just planned:

| Component | Version that works | Why |
|---|---|---|
| openfst | **1.8.4** | 1.8.3 and 1.7.9 fail on GCC 16 (`std::unique_ptr` in `FstImpl::operator=`) |
| opengrm-ngram | **1.3.17** | 1.3.4/1.3.7 ship `-std=c++11` and use `FLAGS_*` instead of `FST_FLAGS_*`; both fail |
| build flags | `CXXFLAGS="-O2 -std=c++17"`, `CPPFLAGS="-I<openfst>/include"`, `LDFLAGS="-L<openfst>/lib"` | opengrm's configure cannot find `fst/fst.h` otherwise |

Constraints measured:

- No usable Docker/Podman → no container build.
- `/tmp` is tmpfs (7.8 GB) and not persistent → build in `.toolchain/` on the project disk.
- Disk: 42 MB (openfst install) + 110 MB (opengrm build tree) ≈ 200 MB, not 2 GB.
- `conda-forge` openfst exists (1.8.4) but opengrm is **not** packaged; the source build is required.
- Build time: ~3 min openfst, ~4 min opengrm on 12 threads.

`tools/bootstrap_kaldi.sh` will pin these exact versions and flags.

### 2.2 Steps (executed successfully)

```bash
# 0. toolchain (once)
curl -sL -o openfst.tar.gz https://www.openfst.org/twiki/pub/FST/FstDownload/openfst-1.8.4.tar.gz
tar xzf openfst.tar.gz && cd openfst-1.8.4
./configure --prefix=$PWD/../inst --enable-far --enable-ngram-fsts CXXFLAGS="-O2 -std=c++17"
make -j12 && make install

curl -sL -o ngram.tar.gz https://www.opengrm.org/twiki/pub/GRM/NGramDownload/ngram-1.3.17.tar.gz
tar xzf ngram.tar.gz && cd ngram-1.3.17
CXXFLAGS="-O2 -std=c++17" CPPFLAGS="-I../inst/include" LDFLAGS="-L../inst/lib" \
  ./configure --prefix=$PWD/../inst
make -j12 && make install

export PATH=$PWD/../inst/bin:$PATH
export LD_LIBRARY_PATH=$PWD/../inst/lib:$PWD/../inst/lib/fst

# 1. corpus (filtered against the model vocabulary)
uv run python -m tools.build_lm_corpus --out data/lm/text.txt --seed 20261020 \
    --filter-against models/small/Gr.fst

# 2. grammar
MODEL=models/small-lm-v1
cp -r models/small $MODEL
fstsymbols --save_osymbols=data/lm/words.txt models/small/Gr.fst > /dev/null

farcompilestrings --fst_type=compact --symbols=data/lm/words.txt --keep_symbols \
    data/lm/text.txt \
  | ngramcount --order=3 \
  | ngrammake --method=kneser_ney \
  | fstconvert --fst_type=ngram > $MODEL/Gr.fst

# 3. manifest + regression
uv run python -m tools.eval --model $MODEL --corpus data/corpus/tts.jsonl \
    --report data/reports/lm-v1.md
```

Measured run: 426 k lines / 4.6 M tokens → 1.7 s build → `Gr.fst` 4.1 MB (base: 16.0 MB).

**Hard requirement discovered:** every token in the corpus must exist in the model's
`words.txt`/symbol table. `farcompilestrings` aborts on the first unknown token. The corpus
builder must filter and report (measured OOV set: `barrigudas`, `bujões`, `goiás`, `guará`,
`oitão`, `xuxa`, `dou-lhe`).


### 2.3 What goes into the LM corpus

Weighted template expansion (`tools/build_lm_corpus.py`), all lowercase, no punctuation:

| Source | Weight | Examples |
|---|---|---|
| Bid frames | 40 % | `mais vinte`, `cobre cinquenta`, `vou de cem`, `duzentos e cinquenta mil reais` |
| Bare numerals | 20 % | every number 0–999 999 |
| Chained bids | 15 % | `mais vinte mais vinte mais sete`, `mais dez mais dez` |
| Idioms/slang | 10 % | `duas onças`, `meia dúzia`, `um barão`, `três placas` |
| Intents | 8 % | `passo`, `tô fora`, `dobro de cinquenta`, `errei` |
| Spelled digits | 5 % | `cinco zero`, `um dois cinco` |
| Chants/fillers | 2 % | `dou-lhe uma`, `redondo`, `na lata` |

Dedup, shuffle with a fixed seed, target 2–10 M tokens. The same generator feeds the runtime
grammar, but with different caps (grammar = 15 k phrases; LM = full).

### 2.4 Acceptance

- WER on the TTS corpus does not regress.
- Slot accuracy (value + add + mult + action) improves by ≥ 3 points absolute over baseline.
- The 40-case text suite stays 40/40.
- Latency unchanged or better (measured: RTF 0.22 → 0.026, **8× faster**).
- If it does not improve, the report says so and the model is **not** adopted. No cherry-picking.

### 2.5 Measured prototype result (planning spike)

A prototype corpus (426 k lines, 4.6 M tokens, weighted templates) rebuilt into a 4.1 MB `Gr.fst`
moved the suite from **26–28/40 to 37–38/40** value accuracy, with `add` 40/40 and `mult` 39/40,
and reduced RTF from 0.18–0.30 to 0.026–0.031. See `01-context-and-findings.md` §5 and
`08-measurements-appendix.md` §16.

This is evidence that the LM rebuild is worth doing in P1, and that the expected gain is ~10 points, not ~3.


## 3. Level 2 — Lexicon and graph rebuild

Only if level 1 leaves the known OOV idioms broken.

1. Export the model's `words.txt` (from `Gr.fst` symbols for `small`).
2. `tools/build_lexicon.py`:
   - canonical words + aliases,
   - G2P: rule-based pt-BR → phonemes, validated against the model's `phones.txt`,
   - manual overrides for the hard ones (`oitão`, `xuxa`, `goiás`, `guará`, `quadruplico`).
3. Compile `L.fst` (lexicon) and `HCLr.fst`/`Gr.fst` as Vosk's "updating words and vocabulary in
   the big models" guide describes.
4. Validate: every added word decodes in isolation; grammar still builds; no phone is unknown.

Risk: this is the most fragile step and can silently corrupt the model. Always work on a copy,
always keep the base model, always run the full suite.

## 4. Level 3 — Acoustic fine-tune (documented, not planned)

Facts:

- Vosk publishes **no** fine-tuning recipe for `vosk-model-small-pt-0.3`; the AM is a Kaldi chain
  model whose training data/alignment recipe is not distributed.
- The realistic route is to train a pt-BR acoustic model with the same feature pipeline
  (`mfcc.conf`: 16 kHz, 40 mel bins, 40 ceps, `use-energy=false`, high-freq −400) and export it to
  Vosk's format — this is a multi-week effort with GPU training.
- A cheaper parallel experiment: **NeMo/CTC** using the `numeros/ds2-sn-brsdv2` checkpoints already
  on disk (CTC, GRU, 16 kHz, pt-BR). That produces a *different* runtime, not a Vosk model, so it is
  out of scope for this project but worth keeping as a research branch.

What would make it viable later: 1–3 h of transcribed auction speech (the corpus tooling in §5 is
designed to collect exactly that), plus GPU time.

## 5. Corpus tooling

### 5.1 Schema (`data/corpus/schema.jsonl`, one object per line)

```json
{
  "id": "tts-faber-ls080-000123",
  "audio": "data/corpus/audio/tts-faber-ls080-000123.wav",
  "raw": "mais vinte mais vinte mais sete",
  "canonical": "mais vinte mais vinte mais sete",
  "intent": "BID_DELTA",
  "expected": {"value": 47.0, "has_addition": true, "has_multiplication": false, "action_type": "BID"},
  "voice": "piper:pt_BR-faber-medium",
  "speed": 0.8,
  "noise": "none",
  "provenance": "synthetic",
  "split": "test"
}
```

`provenance` is `synthetic` or `human`. Reports **must** label results by provenance; synthetic and
human numbers are never mixed into one figure.

### 5.2 `tools/tts_corpus.py`

- Voices: `pt_BR-faber-medium`, `pt_BR-cadu-medium`, `pt_BR-jeff-medium`, `pt_BR-edresson-low`
  (4 voices ≈ diversity proxy; `edresson-low` also simulates a worse mic).
- Speed: `length_scale` ∈ {1.0, 0.9, 0.8, 0.72, 0.65}.
- Phrases: the same generator as the LM corpus, sampled to ~1 000 per voice/speed combination,
  capped at 5 000 total clips (≈ 1.5 h audio, < 500 MB).
- Outputs 16 kHz mono WAV + JSONL, with `split` assigned deterministically (80/10/10).

Caveat, repeated in the report header: Piper is not a human; it cannot model disfluency, real room
noise, or the vocal effort of shouting a bid. It is a **regression harness**, not an accuracy claim.

### 5.3 `tools/augment.py`

- Speed perturbation 0.85–1.15 (resample),
- gain −6…+3 dB,
- additive room noise at SNR 20/15/10 dB (noise from a small bundled set or generated),
- light clipping (simulates cheap headsets),
- every variant recorded as a separate row with `provenance="synthetic-augmented"`.

### 5.4 `tools/record.py` (future human corpus, stub now)

Interactive: shows a phrase, records with `sounddevice`, plays back, lets the user accept/fix the
transcript, writes the row. Includes a "hard cases" mode that forces fast repetitions
(`mais vinte mais vinte mais sete`) and spelled digits. Target: 200–500 utterances, 3–5 speakers,
2 mics. Not run now (decision D-4: TTS only).

## 6. Evaluation harness

`tools/eval.py` computes:

| Metric | Definition |
|---|---|
| WER / CER | on `raw` vs `canonical` |
| Value accuracy | `abs(predicted − expected) < 0.01` |
| Flag accuracy | `has_addition`, `has_multiplication` exact match |
| Action accuracy | `action_type` exact match |
| Slot F1 | value + flags + action combined |
| Latency | p50 / p95 keyup→result, and per-chunk max |
| RSS | peak process RSS |
| Failures | per-utterance dump with raw text, parsed result, expected |

Outputs Markdown + CSV to `data/reports/`, with a header stating model, grammar size, corpus hash,
provenance and date. `--compare base,lm-v1` produces a side-by-side table.

`tools/bench_matrix.py` runs the cross product:

```
models   = small, small-lm-v1, [pruned]
grammar  = off, on
padding  = none, 300/400
speed    = 1.0, 0.8, 0.65
```

and writes one table. This productizes the ad-hoc matrix that produced §4.9 of
`01-context-and-findings.md`.

## 7. Order of work (revised after the spike)

The spike proved the LM rebuild works, so it is no longer "after the demo":

1. `tools/bootstrap_kaldi.sh` (openfst 1.8.4 + opengrm 1.3.17, pinned) — **P1**.
2. `build_lm_corpus.py` + `build_graph.py` → `small-lm-v1`; A/B report — **P1**.
3. `tts_corpus.py` + `eval.py` → baseline + matrix report — **P3**.
4. Corpus v2 targeting residual failures → `small-lm-v2` — **P4**.
5. Only if needed: `build_lexicon.py` → `small-lm-v3` with OOV idioms — **P5**.
6. Optional: pruned evaluation as an accuracy ceiling reference — **P5**.

## 8. Reproducibility rules

- Every model directory carries `manifest.json` with base model md5, corpus sha256, tool versions,
  n-gram order and date.
- Every report carries the same identifiers.
- No model is adopted without a report that includes the baseline on the same corpus.
- Rebuilds are `--dry-run`-able and never overwrite `models/small-base`.
