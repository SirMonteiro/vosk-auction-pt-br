# 04 — ASR and Runtime Grammar

## 1. Model choice

| Model | Size (zip / unpacked) | Load | RSS | Grammar support | Role |
|---|---|---|---|---|---|
| `vosk-model-small-pt-0.3` | 31 MB / 53 MB | 0.19–0.28 s | 145 MB | **yes** (dynamic `HCLr.fst` + `Gr.fst`) | base |
| `small-lm-v1` (rebuilt `Gr.fst`) | 31 MB / 40 MB | 0.16–0.18 s | 145 MB | **yes** | **default** |
| `vosk-model-pt-fb-v0.1.1-pruned` | 1.1 GB / 1.7 GB | 1.1–5.8 s | 1.84 GB | **no** (static `HCLG.fst`, silently ignored) | optional accuracy mode |
| `vosk-model-pt-fb-v0.1.1-20220516_2113` | 1.6 GB / ~2.4 GB | ~6 s | >2.4 GB | no | not shipped |

Rationale: the demo must run beside a browser on 16 GB total. `small` + rebuilt LM + grammar beats
`pruned` on latency by ~4×, uses 12× less memory, and (measured) **beats it on accuracy**:
37–38/40 versus 34–36/40. `pruned` also cannot use grammar at all.

**Verification at startup:** `HCLr.fst` and `Gr.fst` must exist in the model directory for grammar
mode. If missing, `--grammar` is downgraded to `off` with a warning (never silently).


Model download: `tools/fetch_models.py` (see §7). Models are gitignored.

## 2. Runtime grammar

### 2.1 Form

A **flat JSON array of phrase strings**, accented, `ensure_ascii=False`, always ending with `"[unk]"`.

```python
json.dumps(phrases + ["[unk]"], ensure_ascii=False)
```

Hard rules learned from measurement:

- **Phrases, not words.** Word lists remove `e`/`de` and truncate (`cem mil` → `mil`).
- **Accents required.** `garca` is ignored, `garça` works. `ensure_ascii=True` breaks it.
- **No nesting.** `[["a","b"]]` raises at recognizer creation.
- **Every entry validated** with `Model.vosk_model_find_word()`; failures are dropped and logged.
- **Size cap 15 000 phrases**; above that the builder raises instead of degrading.

### 2.2 Generation (deterministic, seeded, reproducible)

`nlu/grammar.py` (new module; listed in `02-architecture.md` under `nlu/`):

1. **Numbers** (unweighted, exhaustive up to 999 999):
   - 0–999: units, teens, tens, `tens e units`, `cem`, hundreds, `hundred e below100`.
   - `N mil` for all N in 0–999.
   - `N mil e M` **only** when N is a round multiple of 10 or 100 (limits combinatorial blowup).
2. **Currency frames** (weighted, top N by frequency):
   - prefixes: `""`, `mais`, `cobre`, `vou de`, `dou`, `subo`, `aumento`, `ofereço`
   - suffixes: `""`, `reais`, `conto`, `pila`, `mil`, `mil reais`
3. **Idioms and slang**: every entry of `IDIOMS`, `BINGO`, `MAGNITUDES`, `DENOMINATIONS`, with
   their natural articles (`uma onça`, `duas onças`, `um barão`).
4. **Intents**: every `PASS`, `CORRECT`, `QUERY`, `CHANT`, `MULTIPLY` phrase.
5. **Chants/fillers**: `redondo`, `na lata`, `sem troco`, `tá dado`, `e fechando`, `mil em cima`.
6. **Spelled digits**: `zero`–`nove` pairs used by the digit-reading mode.

Priority when trimming to 15 000: intents/idioms (never trimmed) → numbers ≤ 999 → `N mil` →
currency frames → spelled digits. Trimming is deterministic (sorted) and reported.

Measured sizes: 14 202 phrases / 494 KB / 22 ms build. Accuracy does not improve above this.

**Numbers are mandatory.** An intent-only grammar (103 phrases) scored **18/40** on the 40-case
suite, because the grammar replaces the LM entirely rather than biasing it: `quarenta e sete` →
empty, `duzentos e cinquenta mil` → `cinquenta mil`. Any grammar must therefore contain the number
phrases, even when the model's LM has been rebuilt with numbers.

**With a rebuilt LM, grammar is optional.** Measured:

| Config | value |
|---|---|
| base model + curated grammar (2 379) | 37/40 |
| LM rebuilt, plain | 37–38/40 |
| LM rebuilt + curated grammar (2 379) | 37/40 |
| LM rebuilt + full number grammar (33 120) | 36/40 |
| LM rebuilt + intent-only grammar (103) | 18/40 |

Default in the demo: **LM rebuilt + curated grammar** (keeps OOD rejection, no accuracy loss).


### 2.3 Validation report

- `tools/build_grammar.py` (new, planned) wraps the generator and prints the validation report.

```
grammar: 14202 phrases, 494 KB
  numbers      11800
  idioms         210
  intents         64
  frames        2100
  dropped (OOV)   16  → data/reports/grammar-oov.txt
  build           22 ms
```

OOV entries are expected for `small` (`oitão`, `xuxa`, `goiás`, `quadruplico`…). They are covered
by NLU fallbacks (`três oitão` → 38) rather than by the grammar.

### 2.4 Grammar on/off

Grammar is a dev toggle in the UI and a CLI flag. Off is useful to compare raw model behavior and
to run the `pruned` model (where it is a no-op).

## 3. Audio pipeline

### 3.1 Browser side

- `getUserMedia({audio: {channelCount:1, echoCancellation:false, noiseSuppression:false, autoGainControl:false}})` — game voice should not be pre-processed.
- `AudioContext({sampleRate: 16000})`; if the browser refuses, capture at device rate and resample in
  JS (linear or `OfflineAudioContext`).
- Capture: `AudioWorkletNode` (preferred) with `ScriptProcessorNode(4096)` fallback.
- Convert Float32 → Int16 in JS, send 100 ms frames (`1600 samples = 3200 bytes`).
- **Pre-roll**: keep the last 250 ms in a ring buffer while idle; on `start`, send it first.
- **Trailing silence**: on `stop`, send 400 ms of zeros before `{"type":"stop"}`.
- Short utterances (< 250 ms) are rejected client-side with a visible message.

### 3.2 Server side

- Binary frames are fed directly to `AcceptWaveform` (already 16 kHz s16le).
- Leading/trailing silence is enforced server-side too (idempotent), so `/transcribe` and the WS
  path behave identically.
- RMS gate: if the whole utterance RMS < 0.004, return `action_type=NONE, reason="silence"`.
- Duration gate: < 250 ms → `reason="too_short"`.
- `/transcribe` accepts WAV/WebM/OGG; if not 16 kHz mono s16le, convert with `ffmpeg` (already a
  system dependency) via `asr/audio.py`.

### 3.3 Timing

| Stage | Measured (`small` + grammar) |
|---|---|
| 100 ms chunk decode (p95) | 9–10 ms |
| Final flush | 8–30 ms |
| Parser | < 2 ms |
| **Total keyup → result** | **~40 ms typical, < 150 ms budget** |

Plain (no grammar) spikes to 150–250 ms per chunk — another reason grammar is the default.

## 4. Decoding options

| Option | Setting | Reason |
|---|---|---|
| `SetWords` | `True` | word timings for trace + n-best scoring |
| `SetMaxAlternatives` | `5` | n-best panel and parser-scored selection |
| `SetPartialWords` | `True` | live partial with timings (cheap) |
| `SetNLSML` | `False` | not needed |
| `SetSpkModel` | never | no speaker ID |
| Recognizer reuse | one per WS session, `Reset()` between utterances | creation is cheap but grammar rebuild is not |

**n-best policy:** Vosk's confidence ranking is unreliable (for `"mais vinte"` the best-ranked
alternative was `"mas vindo de"`). The demo therefore:

1. takes the raw top text,
2. parses **every** alternative,
3. picks the alternative with the highest `(parse_confidence, decoder_confidence)` tuple,
4. reports all alternatives with their parser score and which one won.

`parse_confidence` is a small deterministic score: valid bid (+1.0), all tokens known (+0.3),
no phonetic repair needed (+0.2), intent match (+0.2), minus 0.5 per dropped token.

## 5. Failure modes observed and their handling

| Observation | Cause | Handling |
|---|---|---|
| `"dobro de cinquenta"` → `"cinquenta"` | `dobro` short and unstressed | grammar includes the full phrase; parser also accepts bare `dobro` + number |
| `"duas onças"` → empty | `onça` nasal + short | grammar includes `duas onças`; NLU fallback accepts `duas` alone as 2 only if `onças` present in n-best |
| `"meia dúzia"` → `"doze"` | homophone | grammar phrase + explicit repair `doze` after `meia` |
| `"um milhão"` → `"um"`/`"união"` | rare word | grammar phrase + `união`→`milhão` explicit repair (guarded) |
| `"cem mil"` → `"mil"` | word-list grammar | phrase grammar fixes it |
| `"mais vinte mais vinte mais sete"` loses a `mais` | fast repetition | parser sums what it sees; n-best scoring recovers the full form |
| `"três oitão"` → `"leitão"` | OOV | NLU fallback maps `leitão`→38 **only** in bid context with no other number |
| `"passo"` → empty | short, unstressed | grammar includes `passo`; PASS detection also accepts `tô fora`, `desisto` |

## 6. Resource budget

| Item | Budget |
|---|---|
| Model RSS | 145 MB |
| Process RSS (server + numpy) | ~250 MB |
| Peak with Piper loaded (tools only) | ~500 MB |
| Disk (models) | 53 MB small (+1.7 GB optional pruned) |
| Disk (corpus) | < 500 MB for 5 000 TTS clips |
| CPU during decode | 1 core, 2–10 % duty cycle |

Leaves > 13 GB for browser + Unity on the 16 GB machine.

## 7. Model management

`tools/fetch_models.py`:

```bash
uv run python -m tools.fetch_models small          # 31 MB
uv run python -m tools.fetch_models pruned         # 1.1 GB, optional
```

- Downloads to `models/<name>/`, verifies md5 from `model-list.json`, writes `manifest.json`.
- Idempotent; `--force` re-downloads.
- Never downloads at server startup (explicit step, so offline runs work).

`manifest.json`:

```json
{
  "name": "vosk-model-small-pt-0.3",
  "url": "https://alphacephei.com/vosk/models/vosk-model-small-pt-0.3.zip",
  "md5": "458c69371c5a0b9ab6ee8fa417bf89da",
  "size_bytes": 32453112,
  "unpacked_bytes": 53552717,
  "dynamic_graph": true,
  "fetched_at": "2026-10-20T00:00:00Z"
}
```

## 8. What is deliberately not done

- No VAD model. PTT gives exact boundaries; a VAD would add latency and failure modes.
- No streaming partial re-parsing per frame. Partials are displayed raw; the parser runs on final
  only (partial parsing caused the `7.0` bug in the old code when numbers were still incomplete).
- No diarization, no language switching, no punctuation restoration.
- No WebRTC/Opus. Raw PCM over localhost WS is simpler and lossless.
