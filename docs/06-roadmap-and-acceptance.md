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
- `tools/build_lm_corpus.py`, `tools/build_graph.py`, `tools/bootstrap_kaldi.sh`.
- `models/small-lm-v1/` (LM rebuilt from the first corpus) with `manifest.json`.
- `server/app.py`, `templates/index.html`, `static/app.js`, `static/style.css`.
- `/health`, `/stream`, `/transcribe`.
- stdout report block.

Acceptance:

- Hold Space → partial text appears; release → final block in stdout and textarea in < 150 ms.
- "Copiar tudo" copies the full textarea.
- Grammar status visible in UI (`N phrases` or `off`), and model badge distinguishes
  `small` vs `small-lm-v1`.
- `/transcribe` with a WAV fixture returns the same JSON shape as WS.
- Engine smoke test passes with `models/small` present, skips cleanly otherwise.
- `small-lm-v1` beats base `small` by ≥ 5 points value accuracy on the 40-case suite
  (prototype achieved +10).


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

### P3 — Corpus + eval (Optional / Deferred)

*(Deferred: P0–P2 accuracy and latency are verified and sufficient for first game integration; see P-Unity below).*

Deliverables:

- `tools/tts_corpus.py`, `tools/augment.py`, `tools/eval.py`, `tools/bench_matrix.py`.
- `data/reports/baseline.md` (small, grammar on/off, speeds 1.0/0.8/0.65).
- `data/reports/baseline.csv`.

Acceptance:

- Corpus reproducible from a seed (same hash on re-run).
- Report states model, grammar size, corpus hash, provenance.
- Baseline numbers within ±3 points of the measured §4.9 values.

### P4 — Corpus iteration + second LM (Optional / Deferred)

Deliverables:

- Corpus v2 targeting residual edge cases.
- `models/small-lm-v2/` + A/B report against `small-lm-v1`.

Acceptance:

- ≥ 39/40 value accuracy on the 40-case suite, or a documented negative result.

### P5 — Optional features (As needed)

- Spelled-digit mode (`cinco zero` → 50) end-to-end.
- `--replay` CLI mode: feed a WAV through the exact WS pipeline for debugging.

### P-Unity — Unity C# Package & Direct Integration (ACTIVE MILESTONE)

Deliverables:

- Self-contained Unity Package (`Packages/com.auction.voice` / `Assets/Packages/AuctionVoice`) conforming to SOLID design principles.
- Core Interfaces: `IAudioCaptureService`, `ISpeechRecognitionEngine`, `INluParser`, `IBidProcessorService`.
- Push-to-talk controller integrated with Unity's new Input System (`InputActionReference`) with configurable post-release grace period (default 250ms).
- Native Vosk P/Invoke wrapper loading `small-lm-v1` from `StreamingAssets`.
- Direct C# port of the reference NLU (Lexicon, Numbers, Idioms, Phonetics, Intents, Parser).
- Debug logging conditionally compiled (`#if UNITY_EDITOR || DEVELOPMENT_BUILD`) with inspector toggle.
- Comprehensive C# unit test suite mirroring `text_cases.jsonl` (40/40 test cases).

Status: **source complete and verified off-device.** See `09-unity-csharp-implementation.md`.

- Package, runtime, tests and sample are written.
- 91 C# tests pass; the reference suite is **50/50**.
- Field-level parity with Python is **4 374/4 374 utterances** (`tools/check_csharp_parity.py`).
- Verified to compile against netstandard2.1 / C# 9, which is Unity 2022.3's profile.
- **Not yet verified in a Unity editor**: on-device latency, IL2CPP AOT, and the scene asset.
  Native `libvosk` binaries are not vendored (platform binaries; see the package README).

Acceptance:

- Can be copied into a clean Unity 2022.3+ / Unity 6 project without compile errors or external Python dependencies.
- Zero main-thread audio hiccups; all ASR inference runs on background tasks.
- Push-to-talk releases properly flush audio and return parsed bid within 150ms.
- 40/40 reference test cases pass identically in C#.


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

| Phase | Effort | Depends on | Status |
|---|---|---|---|
| P0 | 0.5 d | — | DONE |
| P1 | 1.5 d | P0 | DONE |
| P2 | 1 d | P0 | DONE |
| P-Unity | 2 d | P2 | DONE (off-device) — branch `csharp` |
| P3 | 1 d | P1, P2 | Optional / Deferred |
| P4 | 1 d | P3 | Optional / Deferred |
| P5 | as needed | P4 | Optional |

Total Python scaffold + demo (P0–P2): **3 days of work** (Complete).
Active focus shifted to Unity C# Package port for direct game integration.


## 5. Explicitly out of scope

- Packaging Python server for distribution (PyInstaller, Docker images).
- Multi-user/session persistence, database, authentication.
- Acoustic fine-tuning (documented as a research branch only).
- WebGL audio capture (Desktop Standalone target initially).
