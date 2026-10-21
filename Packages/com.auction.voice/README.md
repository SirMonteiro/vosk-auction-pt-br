# Auction Voice — Unity C# Package

In-process Vosk speech recognition and Brazilian Portuguese auction NLU for Unity. No Python
sidecar, no external runtime dependency.

This is a direct C# port of the verified Python engine in `src/auction_demo/`. The parser is
**byte-for-byte equivalent** to the Python reference over a 4 374-utterance differential corpus,
not merely "close enough".

## Requirements

| | |
|---|---|
| Unity | 2022.3 LTS or Unity 6 |
| Scripting backend | IL2CPP or Mono |
| Input | `com.unity.inputsystem` (for `PushToTalkController`) |
| Platform | Windows, Linux, macOS desktop standalone |
| CPU | any x86-64; no GPU, no VRAM |

## Install

1. Copy `Packages/com.auction.voice/` into your project's `Packages/` folder (or add it as a
   local UPM package).
2. Copy the Vosk model directory into `Assets/StreamingAssets/vosk/small-lm-v1/`.
3. Copy the native library for your platform into `Packages/com.auction.voice/Plugins/x86_64/`:
   - Windows: `vosk.dll`
   - Linux: `libvosk.so`
   - macOS: `libvosk.dylib`

   These are **not** vendored in this repository (they are platform binaries). Take them from the
   Vosk release you trust, or from `vosk`'s Python wheel: the `.so`/`.dll` inside it is the same
   library.

4. Add `AuctionVoiceBootstrap` and `PushToTalkController` to a GameObject.

## Quick start

```csharp
using AuctionVoice.Core;
using AuctionVoice.NLU;

// Text-level use, no audio, no model:
var parser = new AuctionBidParser();
BidResult result = parser.Parse("mais vinte mais vinte mais sete");
// result.Kind   == BidKind.BidDelta
// result.Value  == 47
// result.Mode   == ValueMode.Delta
```

Wire the component instead for the full pipeline:

```csharp
// On the same GameObject as AuctionVoiceBootstrap, or assign its controller field.
public sealed class MyGame : UnityEngine.MonoBehaviour
{
    public PushToTalkController controller;

    private void OnEnable()
    {
        controller.OnBidRecognized.AddListener(Apply);
    }

    private void Apply(BidResult bid)
    {
        switch (bid.Kind)
        {
            case BidKind.BidAbsolute:
                currentBid = bid.Value;
                break;
            case BidKind.BidDelta:
                currentBid += bid.Value;   // "mais vinte"
                break;
            case BidKind.BidSum:
                currentBid = bid.Value;    // "vinte mais vinte" is an absolute 40
                break;
            case BidKind.MultiplyOnly:
                currentBid *= bid.Value;   // "dobro"
                break;
            case BidKind.Pass:
                PlayerPassed();
                break;
        }
    }
}
```

## The one semantic trap

`HasAddition` is **not** "the bidder did arithmetic". It is true only for `ValueMode.Delta`,
i.e. an incremental bid on the lot.

| Spoken | `Kind` | `Value` | `Mode` | `HasAddition` | `IsArithmeticSum` |
|---|---|---|---|---|---|
| `cinquenta` | `BidAbsolute` | 50 | `Absolute` | false | false |
| `mais vinte` | `BidDelta` | 20 | `Delta` | **true** | false |
| `mais vinte mais vinte mais sete` | `BidDelta` | 47 | `Delta` | **true** | false |
| `vinte mais vinte` | `BidSum` | 40 | `Sum` | false | **true** |
| `dobro` | `MultiplyOnly` | 2 | `Multiply` | false | false |
| `dobro de cinquenta` | `BidMultiply` | 100 | `Multiply` | false | false |

**Switch on `BidKind`.** `ActionType` is the legacy four-value vocabulary and exists only for
wire compatibility with the Python implementation.

## Slang magnitudes

Confirmed with the domain owner (`docs/07` D-8, D-8b, D-8c):

| Term | Factor |
|---|---|
| `barão`, `pau` | ×1 000 |
| `placa` | **×100 000** |
| `conto`, `pila`, `prata` | ×1 (currency synonyms) |

## Push-to-talk timing

`PushToTalkController` implements hold-and-grace-release:

1. `started` opens the session immediately.
2. Audio streams into Vosk as it is captured.
3. `canceled` starts a **250 ms** grace timer (configurable).
4. Re-pressing inside the grace window cancels the timer and resumes seamlessly.
5. When the timer expires, trailing silence is flushed and the utterance is finalised.

Without the grace period, terminal consonants are clipped: `"cinquenta"` decodes as
`"cinquen"`. Partials are display-only; only the final result changes game state.

## Threading

- `VoskSpeechEngine.Initialize` blocks for ~0.2 s. `AuctionVoiceBootstrap` runs it on a
  `Task.Run`.
- `KaldiRecognizer` is **not** thread-safe. One session at a time; the engine creates a fresh
  recogniser per utterance and disposes it.
- Events are raised on the main thread. `PushToTalkController` additionally queues them if a
  custom service raises from a worker.

## Diagnostics

`AuctionLogger.IsDebugEnabled` gates every message before a string is built, so release builds
allocate nothing. `LogInfoIfDebug` is `[Conditional("ENABLE_AUCTION_DEBUG")]`, which removes the
call site entirely unless the symbol is defined.

## Tests

The package ships its NUnit suite in `Tests/Editor/`:

- `ReferenceCasesTests` — the authoritative parity gate, 50 utterances.
- `NumbersParserTests` — every rule in `docs/03-nlu-spec.md` §3.
- `AudioTests` — PCM maths, pre-roll ring buffer, endpoint detector.
- `BidProcessorServiceTests` — orchestration, audio gates, n-best selection.

Run them with **Window → General → Test Runner → EditMode**.

Without a Unity editor they can be compiled and run headlessly from the repository root:

```bash
./tools/verify_csharp.sh          # compile + run all C# tests
uv run python -m tools.check_csharp_parity   # diff C# against the Python reference
```

## Model files

Ship the model directory unchanged:

| File | Size | Notes |
|---|---|---|
| `final.mdl` | 13.5 MB | acoustic model |
| `HCLr.fst` | 15.5 MB | decoding graph |
| `Gr.fst` | 4.1 MB | rebuilt language model |
| `ivector/*` | 8.5 MB | speaker/CMVN |

Total ~40 MB. Only `small`-family models accept a runtime grammar; the pruned model uses a static
graph and silently ignores one.

## License

Model files are distributed under their own licences (Apache 2.0 for the Vosk models). The
`libvosk` native library is Apache 2.0.
