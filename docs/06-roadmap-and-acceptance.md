# 06 — Roadmap and Acceptance Criteria

## 1. Phases

### P0 — Scaffold (½ day)

Deliverables:

- `pyproject.toml` (uv, py 3.12), `.gitignore`, `README.md`.
- `src/auction_demo/{config.py,cli.py}` skeleton.
- `nlu/lexicon.py` ported from `whisper-auction-pt-br` + `vosk-python`, normalized to accented
  canonical forms, with the extra tables from `03-nlu-spec.md`.
- `nlu/models.py` (`BidResult`), `nlu/currency.py`.
- `tools/fetch_models.py`.
- `uv run pytest -q` green (lexicon + currency tests only).

Acceptance:

- `uv sync` works from a clean checkout.
- `uv run python -m tools.fetch_models small` downloads and verifies md5.
- No import errors; `ruff` clean if configured.

### P1 — Streaming engine + UI (1 day)

Deliverables:

- `asr/audio.py`, `asr/base.py`, `asr/vosk_engine.py`.
- `nlu/grammar.py` (phrase builder + validator + budget).
- `server/app.py`, `templates/index.html`, `static/app.js`, `static/style.css`.
- `/health`, `/stream`, `/transcribe`.
- stdout report block.

Acceptance:

- Hold Space → partial text appears; release → final block in stdout and textarea in < 150 ms.
- "Copiar tudo" copies the full textarea.
- Grammar status visible in UI (`14 202 phrases` or `off`).
- `/transcribe` with a WAV fixture returns the same JSON shape as WS.
- Engine smoke test passes with `models/small` present, skips cleanly otherwise.

### P2 — NLU (1 day)

Deliverables:

- `nlu/numbers.py`, `nlu/idioms.py`, `nlu/intents.py`, `nlu/parser.py`, `nlu/phonetics.py`.
- `tests/fixtures/text_cases.jsonl` (40 legacy + new cases).
- Unit tests for every rule in `03-nlu-spec.md`.

Acceptance:

- Text suite **40/40** on value, `has_addition`, `has_multiplication`.
- Every §3.4, §5.3 and §9 case in `03-nlu-spec.md` passes.
- No false positive from the phonetic pass on the 200-word stoplist.
- `"quero"`, `"sem lance"`, `"dou-lhe uma"`, `"lote um"` produce the documented intents.

### P3 — Corpus + eval (1 day)

Deliverables:

- `tools/tts_corpus.py`, `tools/augment.py`, `tools/eval.py`, `tools/bench_matrix.py`.
- `data/reports/baseline.md` (small, grammar on/off, speeds 1.0/0.8/0.65).
- `data/reports/baseline.csv`.

Acceptance:

- Corpus reproducible from a seed (same hash on re-run).
- Report states model, grammar size, corpus hash, provenance.
- Baseline numbers within ±3 points of the measured §4.9 values.

### P4 — LM rebuild (1–2 days)

Deliverables:

- `tools/build_lm_corpus.py`, `tools/build_graph.py`, `tools/bootstrap_kaldi.sh`.
- `models/small-lm-v1/` with `manifest.json`.
- `data/reports/lm-v1.md` with A/B.

Acceptance:

- `small-lm-v1` improves slot accuracy by ≥ 3 points absolute on the same corpus.
- 40-case text suite unchanged (40/40).
- No latency regression (RTF ≤ 0.05 with grammar).
- If it fails, the report documents the negative result and the model is not adopted.

### P5 — Optional (as needed)

- `tools/build_lexicon.py` for OOV idioms (`small-lm-v2`).
- Pruned "accuracy mode" wiring (`--model pruned`, grammar auto-disabled with warning).
- Spelled-digit mode (`cinco zero` → 50) end-to-end.
- `--replay` CLI mode: feed a WAV through the exact WS pipeline for debugging.

## 2. Definition of done for the demo (P0–P3)

The demo is done when, on a machine with only `uv` and `ffmpeg`:

1. `uv sync && uv run python -m tools.fetch_models small && uv run auction-demo`
2. Browser opens `http://127.0.0.1:5000`.
3. Holding Space and saying `mais vinte mais vinte mais sete` shows:
   - partial text while speaking,
   - `raw: mais vinte mais vinte mais sete`,
   - `value: 47.00`, `has_addition: true`, `has_multiplication: false`, `action_type: BID`,
   - the same block in stdout,
   - a textarea line, and a working "Copiar tudo".
4. `uv run pytest -q` passes.
5. `docs/unity-handoff.md` describes the JSON contract precisely enough for a C# port.

## 3. Test matrix

| Layer | Tool | Model needed | Count target |
|---|---|---|---|
| Number parsing | pytest | no | 80+ cases |
| Intents | pytest | no | 40+ |
| Idioms/slang | pytest | no | 60+ |
| Phonetics (no false positives) | pytest | no | 200-word stoplist + 40 explicit |
| Grammar builder | pytest | optional | size, accents, OOV report |
| WS protocol | pytest + fake ASR | no | 10 |
| Engine smoke | pytest | yes (skip) | 5 |
| TTS corpus | eval | yes | ~5 000 clips |
| Text suite | eval | no | 40 |

## 4. Timeline

| Phase | Effort | Depends on |
|---|---|---|
| P0 | 0.5 d | — |
| P1 | 1 d | P0 |
| P2 | 1 d | P0 |
| P3 | 1 d | P1, P2 |
| P4 | 1–2 d | P3 |
| P5 | as needed | P4 |

Total to a complete demo with baseline report: **3.5 days of work**, then 1–2 days for the first
adapted model.

## 5. Explicitly out of scope

- Unity/C# code, prefabs, scene wiring, WebGL.
- Packaging for distribution (PyInstaller, Docker images).
- Multi-user/session persistence, database, authentication.
- Wake-word, continuous listening, VAD-based segmentation.
- Acoustic fine-tuning (documented as a research branch only).
