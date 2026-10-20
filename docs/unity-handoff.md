# Unity / C# Handoff Contract

This document exists so that the later Unity port has **one** source of truth. No Unity code is
produced in this project (decision D-13). Everything below is what the Python demo will guarantee.

## 1. What Unity will consume

Either of these, with identical payloads:

1. **In-process Python sidecar** — Unity starts the Python demo server and talks to it over
   `http://127.0.0.1:<port>`. Simplest, and keeps a single implementation of the NLU.
2. **Reimplemented in C#** — port `nlu/` (numbers, intents, idioms) and keep the same JSON contract.
   Only viable once the Python suite is 40/40 and the tables are frozen.

The recommendation for the first Unity integration is option 1: it removes the risk of two parsers
drifting apart while the game rules are still being designed.

## 2. Transport

### 2.1 WebSocket `/stream`

| Direction | Payload | Notes |
|---|---|---|
| client → server | `{"type":"start"}` | resets the recognizer for this session |
| client → server | binary | 16 kHz, mono, signed 16-bit little-endian PCM |
| client → server | `{"type":"stop"}` | finalize; server flushes and replies with `final` |
| server → client | `{"type":"ready"}` | after `start` |
| server → client | `{"type":"partial","text":"mais vin"}` | may arrive many times per utterance |
| server → client | `{"type":"final", ...BidResult}` | one per utterance |
| server → client | `{"type":"error","message":"..."}` | recoverable |

Frame size: **3200 bytes = 100 ms**. Smaller frames work; larger frames increase latency spikes.
Unity side: `AudioClip`/`Microphone` → `float[]` → `short[]` → `byte[]` → `ClientWebSocket.SendAsync`.

### 2.2 HTTP `/transcribe` (fallback / debugging)

`multipart/form-data` with field `audio` containing a WAV. Returns the same `BidResult` JSON.

## 3. BidResult JSON

```json
{
  "raw_transcript": "mais vinte mais vinte mais sete",
  "normalized_text": "+ vinte + vinte + sete",
  "nbest": [
    {"text": "mais vinte mais vinte mais sete", "score": 47.0, "chosen": true},
    {"text": "mais vinte mais vinte e sete", "score": 7.0, "chosen": false}
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
  "closing": false,
  "target_value": null,
  "lot": null
}
```

Field notes:

| Field | Type | Contract |
|---|---|---|
| `value` | float | reais; delta when `value_mode == "delta"` |
| `value_mode` | enum | `absolute` \| `delta` \| `sum` \| `multiply` |
| `has_addition` | bool | legacy; `true` for delta and for summed `+` chains |
| `has_sum` | bool | alias of `has_addition` (kept identical) |
| `has_multiplication` | bool | multiplier applied |
| `action_type` | enum | `BID` \| `PASS` \| `MULTIPLY_ONLY` \| `NONE` \| `CHANT` \| `QUERY` \| `CORRECT` \| `LOT` |
| `bid_kind` | enum | authoritative; see `03-nlu-spec.md` §5.1 |
| `closing` | bool | `e fechando` detected |
| `target_value` | float? | for `CORRECT` with an explicit new value |
| `lot` | int? | for `LOT` |
| `reason` | string? | why `NONE` (`silence`, `too_short`, `needs_current_bid`, `unknown`) |

Unity must switch on `bid_kind`, not on `action_type` (the latter is legacy compatibility).

## 4. C# DTO sketch

```csharp
[Serializable]
public class NBestItem { public string text; public float score; public bool chosen; }

[Serializable]
public class BidResult {
    public string raw_transcript;
    public string normalized_text;
    public NBestItem[] nbest;
    public float value;
    public int int_value;
    public string currency;
    public string value_mode;
    public bool has_addition;
    public bool has_sum;
    public bool has_multiplication;
    public string action_type;
    public string bid_kind;
    public float[] numbers_found;
    public string[] idioms_found;
    public float confidence;
    public float latency_ms;
    public string reason;
    public bool closing;
    public float? target_value;
    public int? lot;
}
```

`JsonUtility` does not handle `null` for value types well; use `Newtonsoft.Json` or map `null` to
`NaN`/`-1` explicitly.

## 5. Audio requirements (Unity side)

- Sample rate **16000 Hz**, mono, 16-bit PCM.
- `Microphone.GetDeviceCaps` → prefer 16000; if unavailable, record at the device rate and resample
  to 16 kHz before sending (linear resampling is acceptable; Vosk expects 16 kHz).
- Keep a **250 ms ring buffer** while idle and send it immediately after `start` (first-syllable
  protection; measured benefit).
- Send **400 ms of zeros** after `stop` before the `stop` message (or let the server pad; both are
  idempotent).
- Do not enable echo cancellation / noise suppression / AGC on the game mic if the platform allows
  disabling it — Vosk was trained on unprocessed speech.
- Reject utterances shorter than 250 ms client-side.

## 6. Threading (Unity)

- `ClientWebSocket` receives on a background task; enqueue messages into a `ConcurrentQueue`.
- Drain the queue in `Update()` and raise C# events on the main thread.
- Never call Unity APIs from the socket thread.
- The Python server is single-process; one WS connection per player is fine for local play.

## 7. Lifecycle

```
Idle ──start──► Listening ──stop──► Finalizing ──final──► Idle
   ▲                                                    │
   └──────────────────── error ◄────────────────────────┘
```

- On `error`, close the socket, recreate it, and re-send `start` on the next utterance.
- The server keeps the model loaded across sessions; reconnects are cheap (< 50 ms).
- `partial` events are display-only; never apply game state from them.

## 8. What the game layer must implement (not the demo)

| Concern | Owner |
|---|---|
| Current bid, minimum increment, turn order | Game |
| Applying `value_mode=delta` to the current bid | Game |
| Timer for the auctioneer chant | Game |
| Rejecting a bid below the minimum | Game |
| Deciding what `closing` and `target_value` mean | Game |
| Voice feedback / TTS of the auctioneer | Game |
| Persistence, scoring, UI | Game |

The demo only reports what was heard and parsed.

## 9. Versioning

- The JSON contract carries `"contract_version": "1"` (to be added in P1; the demo prints it in
  `/health` too).
- Any breaking change bumps the version and is documented here.
- Field names are never renamed silently; legacy fields stay until a major bump.

## 10. Performance expectations for Unity

| Metric | Expected on a mid-range CPU |
|---|---|
| Server RSS | ~250 MB |
| Keyup → final | < 150 ms (measured ~40 ms; 10–13 ms max chunk with `small-lm-v1`) |
| CPU during decode | 2–10 % of one core |
| VRAM | 0 |
| Model load (once) | 0.2 s |

This is the core reason for leaving Whisper: no Vulkan, no VRAM contention, no AMD/older-NVIDIA
driver crashes.

## 11. Model artifacts to ship with the game

| File | Size | Notes |
|---|---|---|
| `small-lm-v1/final.mdl` | 13.5 MB | acoustic model, unchanged |
| `small-lm-v1/HCLr.fst` | 15.5 MB | decoding graph, unchanged |
| `small-lm-v1/Gr.fst` | 4.1 MB | **rebuilt** language model (base: 16.0 MB) |
| `small-lm-v1/ivector/*` | 8.5 MB | speaker/CMVN, unchanged |
| `small-lm-v1/words.txt` | — | symbol table used to build the LM (needed for rebuilds, not runtime) |

Total ~40 MB unpacked. No runtime dependency beyond `libvosk` and the model directory.

## 12. What NOT to port

- The `nbest` parser-scoring heuristic: it exists to compensate for Vosk's unreliable confidence
  ranking during tuning. The game should consume `bid_kind` + `value` and ignore `nbest` unless
  building a debug overlay.
- Phonetic repair: it belongs to the Python NLU. If the parser is ported to C#, the repair tables
  must be ported with it, not re-invented.
- Runtime grammar: it is a server-side ASR concern. Unity sends raw PCM and receives text; it never
  builds or sends a grammar.

