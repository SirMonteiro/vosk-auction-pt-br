# 02 — Architecture

## 1. Project layout

```
auction-vosk-demo/
├── pyproject.toml                 # uv project, python >= 3.12
├── uv.lock
├── README.md
├── .gitignore
├── models/                        # gitignored, downloaded by tools/fetch_models.py
│   ├── small/                     # vosk-model-small-pt-0.3
│   │   └── manifest.json          # md5, source url, date, word count
│   └── pruned/                    # optional accuracy mode
├── src/auction_demo/
│   ├── __init__.py
│   ├── config.py
│   ├── cli.py
│   ├── asr/
│   │   ├── __init__.py
│   │   ├── base.py
│   │   ├── vosk_engine.py
│   │   └── audio.py
│   ├── nlu/
│   │   ├── __init__.py
│   │   ├── lexicon.py
│   │   ├── numbers.py
│   │   ├── idioms.py
│   │   ├── intents.py
│   │   ├── parser.py
│   │   ├── phonetics.py
│   │   ├── grammar.py             # phrase grammar builder (ASR-facing, but pure text)
│   │   └── models.py
│   └── server/
│       ├── __init__.py
│       ├── app.py
│       ├── templates/index.html
│       └── static/{app.js,style.css}
├── tests/
│   ├── test_numbers.py
│   ├── test_intents.py
│   ├── test_idioms.py
│   ├── test_phonetics.py
│   ├── test_grammar_builder.py
│   ├── test_vosk_engine.py        # skips if model missing
│   ├── test_streaming_ws.py       # fake ASR, no model
│   └── fixtures/text_cases.jsonl  # the 40-case text suite
├── tools/
│   ├── fetch_models.py
│   ├── build_grammar.py           # dump/inspect the runtime phrase grammar
│   ├── record.py                  # future human corpus (stub, documented)
│   ├── tts_corpus.py
│   ├── augment.py
│   ├── build_lm_corpus.py
│   ├── build_graph.py
│   ├── build_lexicon.py
│   ├── eval.py
│   └── bench_matrix.py
├── data/
│   ├── corpus/                    # generated, gitignored
│   └── reports/                   # generated, gitignored
└── docs/
    ├── README.md … 08-measurements-appendix.md
    └── unity-handoff.md
```

Rules:

- `src/` layout, importable as `auction_demo` after `uv sync`.
- No module imports another module's internals; `nlu` never imports `asr` and vice versa.
- `tools/` may import `auction_demo`, never the reverse.
- Generated data (`models/`, `data/`) is gitignored except `manifest.json` templates.

## 2. Runtime data flow

```
Browser (index.html + app.js)
  │  keydown Space  ──► getUserMedia → AudioContext(16 kHz)
  │                     AudioWorklet/ScriptProcessor → 100 ms Int16 frames
  │                     250 ms pre-roll ring buffer (kept while idle)
  │  WS /stream  ─────► binary frames (3200 bytes)
  │                     {"type":"start"} / {"type":"stop"}
  ▼
server/app.py  ──► StreamingSession (asr/vosk_engine.py)
                      │  feeds KaldiRecognizer(AcceptWaveform)
                      │  PartialResult() → {"type":"partial"}
                      ▼
                    on stop: pad 400 ms silence → FinalResult()
                      │
                      ▼
                nlu/parser.parse(text) → BidResult
                      │
        ┌─────────────┼───────────────────────────┐
        ▼             ▼                           ▼
   stdout block   WS {"type":"final", ...}   textarea line
```

Latency budget per utterance (measured, `small` + grammar):

| Stage | Budget |
|---|---|
| Frame decode (`AcceptWaveform`, 100 ms) | ≤ 10 ms |
| Final decode after keyup | ≤ 30 ms |
| Parser | ≤ 2 ms |
| WS round trip (localhost) | ≤ 5 ms |
| **Keyup → rendered result** | **< 150 ms** |

The 250 ms pre-roll exists because Vosk drops the first syllable if audio starts abruptly; it also
matches the measured benefit of leading silence.

## 3. Module contracts

### 3.1 `config.Settings` (dataclass, env-overridable)

```python
model: str = "small"            # "small" | "pruned" | path
sample_rate: int = 16000
frame_ms: int = 100
pre_roll_ms: int = 250
lead_silence_ms: int = 300
tail_silence_ms: int = 400
min_utterance_ms: int = 250
min_rms: float = 0.004
use_grammar: bool = True
grammar_max_phrases: int = 15_000
nbest: int = 5
host: str = "127.0.0.1"
port: int = 5000
```

Env overrides: `AUCTION_MODEL`, `AUCTION_GRAMMAR`, `AUCTION_PORT`, `AUCTION_NBEST`.

### 3.2 `asr.base.StreamingASR` (Protocol)

```python
class StreamingASR(Protocol):
    name: str
    def start(self) -> None: ...
    def feed(self, pcm: bytes) -> str | None: ...      # partial text or None
    def finish(self) -> ASRResult: ...                 # final text + nbest + timing
    def reset(self) -> None: ...
```

`ASRResult`: `text`, `alternatives: list[Alternative(text, score, words)]`, `duration_s`,
`decode_ms`, `model`.

### 3.3 `asr.vosk_engine.VoskEngine`

- Loads `Model` once (module-level cache keyed by resolved path).
- `StreamingSession` wraps one `KaldiRecognizer`; recognizers are **not** thread-safe and are
  created per WS connection.
- `create_recognizer()` applies the grammar (validated at startup) and `SetWords(True)`,
  `SetMaxAlternatives(cfg.nbest)`.
- Logging goes through `logging`, never `print`, except the intentional stdout report block.

### 3.4 `nlu.models.BidResult` (dataclass → JSON)

```python
raw_transcript: str
normalized_text: str
nbest: list[NBestItem]          # text, score, chosen: bool
value: float
int_value: int
currency: str
has_addition: bool
has_sum: bool
has_multiplication: bool
action_type: str                # BID | PASS | MULTIPLY_ONLY | NONE | CHANT | QUERY | CORRECT | LOT
bid_kind: str                   # authoritative enum (see 03-nlu-spec.md)
numbers_found: list[float]
confidence: float
latency_ms: float
trace: dict                     # per-stage debug, only filled when cfg.debug
```

Legacy names (`has_addition`, `has_multiplication`, `action_type`, `value`, `currency`,
`numbers_found`) are preserved so the future Unity client does not inherit two dialects.

### 3.5 `nlu.parser.parse(text, nbest=None) -> BidResult`

Pure function, no I/O, no globals beyond precompiled tables. Deterministic. This is the only
module the Unity port needs to replicate (or call).

### 3.6 `server.app.create_app(engine, parser, settings) -> Flask`

Routes:

| Route | Method | Purpose |
|---|---|---|
| `/` | GET | Page |
| `/health` | GET | `{status, model, grammar_phrases, grammar_ok, version}` |
| `/stream` | WS | Binary PCM in, JSON events out |
| `/transcribe` | POST | WAV upload → same JSON as `/stream` final (for fixtures/tests) |

WS protocol:

| Direction | Message | Meaning |
|---|---|---|
| client→server | `{"type":"start"}` | open session, reset recognizer |
| client→server | binary | 16 kHz mono s16le PCM |
| client→server | `{"type":"stop"}` | flush, finalize |
| server→client | `{"type":"ready"}` | session open |
| server→client | `{"type":"partial","text":…}` | live text |
| server→client | `{"type":"final",…BidResult}` | final |
| server→client | `{"type":"error","message":…}` | recoverable error |

## 4. Threading and lifecycle

- Flask dev server (`threaded=True`); one WS connection = one thread = one recognizer.
- Model is shared (Vosk `Model` is read-only and safe to share); recognizers are not.
- `StreamingSession` holds a lock around `AcceptWaveform` because partials and finals can race on
  a stop event.
- On disconnect: finalize or drop, free recognizer. Model stays loaded.
- Startup fails fast if the model directory is missing, printing the exact `fetch_models.py` command.

## 5. UI (single page, no build step)

Sections:

1. **Header** — model badge, grammar badge (`14 202 phrases`, or `off`), status pill.
2. **Push-to-talk zone** — hold Space (or hold the button for accessibility), timer, live partial.
3. **Result card** — `value`, `currency`, `bid_kind`, three boolean pills (`has_addition`,
   `has_sum`, `has_multiplication`), confidence, latency.
4. **N-best panel** — every alternative with its parser score and the winner highlighted. This is
   the tuning instrument, not decoration.
5. **Log textarea** — one block per utterance (raw → normalized → result), append mode toggle,
   **"Copiar tudo"** button, clear button.
6. **Dev row** — grammar on/off (only enabled when the model supports it), model selector,
   trace toggle.

Vanilla JS, no framework, no bundler. `navigator.clipboard.writeText` with a `document.execCommand`
fallback for non-secure contexts.

## 6. Logging and stdout

Exactly one block per finalized utterance:

```
============================================================
🏛️  AUCTION BID
------------------------------------------------------------
model            vosk-model-small-pt-0.3  (grammar: 14202)
raw              "mais vinte mais vinte mais sete"
normalized       " + vinte + vinte + sete"
nbest            1) 47.00  mais vinte mais vinte mais sete  ← chosen
                 2)  7.00  mais vinte mais vinte e sete
value            47.00   (R$ 47,00)
has_addition     True
has_sum          True
has_multiplication False
action           BID / BID_INCREMENT
confidence       0.82
latency          38 ms
============================================================
```

`logging` is used for diagnostics; the block above is the only `print` path and is switchable with
`--quiet`.

## 7. Error handling

| Situation | Behavior |
|---|---|
| Model missing | exit 2 with the fetch command |
| Grammar over budget | refuse to start, list the overflow count |
| Grammar word OOV | drop it, log a warning, write it to `data/reports/grammar-oov.txt` |
| Empty/short audio | `{"type":"final","action_type":"NONE"}` + reason `too_short` |
| Silent audio (RMS gate) | same, reason `silence` |
| Model crashes mid-stream | close WS with error, recreate session on next `start` |
| Browser without AudioWorklet | fall back to `ScriptProcessorNode` (deprecated but works) |
| Non-16 kHz device | browser resamples via `AudioContext({sampleRate:16000})`; server also validates |

## 8. Testing strategy

- **Unit (no model):** numbers, intents, idioms, phonetics, grammar builder, currency formatting.
- **Text suite:** `tests/fixtures/text_cases.jsonl` — 40 cases, must stay 40/40.
- **WS integration:** fake `StreamingASR` returning canned text; asserts protocol, padding, JSON shape.
- **Engine smoke:** skipped unless `models/small` exists; asserts load, one decode, grammar effect.
- **Golden files:** stdout block snapshot for three utterances.

CI-less by design (local project), but `uv run pytest -q` must pass before any commit.
