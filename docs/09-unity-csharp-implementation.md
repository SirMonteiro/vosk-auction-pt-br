# 09 — Unity C# Package: Implementation Notes

This document records how the C# port is built, verified and shipped. The *plan* is
`unity-csharp-architecture-plan.md`; this file is the implementation record, including the
decisions that were settled while writing the code.

## 1. What exists now

```
Packages/com.auction.voice/
├── package.json                       # UPM manifest, depends on com.unity.inputsystem
├── README.md                          # consumer-facing usage guide
├── Runtime/
│   ├── AuctionVoice.Runtime.asmdef
│   ├── Core/
│   │   ├── Enums.cs                   # ValueMode, BidKind, ActionType, BidRejectReason, wire names
│   │   └── BidResult.cs               # domain result + currency formatting
│   ├── Diagnostics/
│   │   ├── IAuctionLogger.cs          # interface + NullAuctionLogger
│   │   └── AuctionLogger.cs           # Unity logger, zero-alloc when disabled
│   ├── NLU/
│   │   ├── TextNormalization.cs       # accent stripping, normalisation
│   │   ├── Lexicon.cs                 # GENERATED — do not edit
│   │   ├── NumbersParser.cs           # cardinal grammar, chunking, centavos
│   │   ├── IdiomsPass.cs              # longest-match idiom replacement
│   │   ├── PhoneticsPass.cs           # guarded repairs + difflib-compatible fuzzy match
│   │   ├── IntentsDetector.cs         # CHANT→CORRECT→PASS→QUERY→LOT→MULTIPLY→BID
│   │   └── AuctionBidParser.cs        # INluParser implementation
│   ├── ASR/
│   │   ├── ISpeechRecognitionEngine.cs
│   │   ├── JsonValue.cs               # dependency-free JSON reader
│   │   ├── VoskSpeechEngine.cs        # engine + streaming session
│   │   └── NativeVosk/VoskPInvoke.cs  # P/Invoke surface
│   ├── Audio/
│   │   ├── PcmAudio.cs                # Unity-free PCM maths
│   │   ├── IAudioCaptureService.cs    # interface, PreRollBuffer, endpoint detector
│   │   └── UnityMicrophoneCapture.cs  # Microphone-backed capture
│   └── Service/
│       ├── BidProcessorService.cs     # capture→ASR→NLU orchestration
│       └── PushToTalkController.cs    # 250 ms grace period
├── Tests/Editor/                      # NUnit suite (4 fixtures)
└── Samples~/BasicAuctionDemo/         # bootstrap + debug HUD
```

## 2. How the port was verified without Unity

The authoring workstation has no Unity editor and no `dotnet` CLI. Three tools close that gap.
All three live under `tools/` and are **not** part of the package.

### 2.1 `tools/verify_csharp.sh` — compile + test

Compiles `Runtime/` and `Samples~/` against the **netstandard2.1** reference pack (Unity
2022.3's exact API profile) with C# 9, then compiles the package's real NUnit tests and runs them
through a small reflection runner. Unity-facing files are type-checked against minimal
`UnityEngine` stubs (`tools/csharp-headless/UnityStubs.cs`), so typos and signature errors are
caught here instead of in the editor.

```bash
./tools/verify_csharp.sh            # 91 tests
./tools/verify_csharp.sh --compile  # fast syntax gate
```

Bootstrapping the SDK (already gitignored under `.toolchain/`):

```bash
mkdir -p .toolchain && cd .toolchain
curl -sSL https://dot.net/v1/dotnet-install.sh \
  | bash -s -- --channel 8.0 --install-dir "$PWD/dotnet" --no-path
```

> **Sandbox note:** `dotnet build` fails here with `IOException: NuGet-Migrations` because the
> sandbox blocks POSIX named mutexes. `verify_csharp.sh` therefore invokes the SDK's `csc.dll`
> through `dotnet exec`, which skips first-run configuration entirely.

### 2.2 `tools/gen_csharp_lexicon.py` — table generation

Emits `Runtime/NLU/Lexicon.cs` and `Tests/Editor/ReferenceCases.g.cs` **from the Python
lexicon and fixtures**. Hand-transcribing ~700 accented entries is how silent drift starts; the
generator makes drift impossible.

```bash
uv run python -m tools.gen_csharp_lexicon           # regenerate
uv run python -m tools.gen_csharp_lexicon --check   # fail if stale (CI gate)
```

Never edit the two generated files by hand.

### 2.3 `tools/check_csharp_parity.py` — differential testing

Generates a corpus from the domain tables (every number 0-999 in its spoken forms, framed and
chained; every slang term, idiom, intent and multiplier; 4 000 random vocabulary combinations),
runs both implementations, and diffs **every field**.

```bash
uv run python -m tools.check_csharp_parity           # 4 374 utterances
uv run python -m tools.check_csharp_parity --quick   # fixtures + hand-picked only
```

Current result: **4 374 utterances, every field identical** (value, mode, kind, action, both
legacy flags, currency string, confidence, reason, closing, target_value, lot, numbers_found,
idioms_found).

## 3. Bugs the differential test caught

These are worth recording because the 50-case fixture suite passes with all of them present.

### 3.1 `float` is not precise enough for reais

`BidResult.Value` was originally `float`. Single precision has ~7 significant digits, so a bid
above ~16.7 million reais cannot be represented to the nearest real:

```
Python: R$ 16.000.019,00     C# (float): R$ 16.000.020,00
```

The Python reference uses a double. `Value`, `NumbersFound`, `Confidence`, `TargetValue` and
`NBestItem.Score` are now `double`. The fixture suite never caught this because no fixture is
large enough.

### 3.2 `difflib` is not "roughly Levenshtein"

The phonetic fuzzy fallback depends on Python's `SequenceMatcher.ratio()` (Ratcliff/Obershelp)
and on the 0.80 similarity / 0.08 margin thresholds. A Levenshtein approximation would change
which tokens get repaired. `PhoneticsPass.SequenceMatcherRatio` is a faithful port of the
recursion, including the "earliest match in `a`, then in `b`" tie-break.

## 4. Design decisions settled during implementation

| # | Decision | Reason |
|---|---|---|
| C-1 | `Value` and friends are `double` | See §3.1 |
| C-2 | Lexicon is generated, never hand-written | See §2.2 |
| C-3 | `PcmAudio` and `PreRollBuffer` are UnityEngine-free | Headlessly testable; safe on worker threads |
| C-4 | `JsonValue` instead of `JsonUtility` or Newtonsoft | Vosk's result shape varies; a UPM package should not force a JSON dependency |
| C-5 | `INluParser` exposes `ParseNBest` | The service needs it; a cast to the concrete type would be a leaky abstraction |
| C-6 | `AuctionLogger.LogInfo` cannot be `[Conditional]` | An interface implementation cannot carry it; `LogInfoIfDebug` is the guarded overload |
| C-7 | `PushToTalkController` is not `sealed` | `BuildDefaultService` is a documented extension point for injecting a pre-loaded engine |
| C-8 | Capture is ticked from `Update()` | `Microphone.GetData` is a main-thread API; decoding stays off-thread |
| C-9 | Endpoint detector evaluates 40 ms sub-windows inside a chunk | Otherwise a 100 ms chunk is treated as one window and the onset rule never fires |

## 5. What is not done

| Item | Status |
|---|---|
| `Plugins/x86_64/{vosk.dll, libvosk.so, libvosk.dylib}` | **Not vendored.** Platform binaries must be copied in; see the package README |
| `Samples~/BasicAuctionDemo/DemoAuctionScene.unity` | Not authored. Unity scene files are binary-ish YAML and cannot be generated reliably without the editor; wire two GameObjects manually |
| On-device (Android/iOS) support | Out of scope; `StreamingAssets` is not a real directory on Android and the model must be extracted first |
| IL2CPP AOT verification | Not verifiable without Unity. `VoskPInvoke` uses only blittable signatures and `Marshal.PtrToStringUTF8`, which is AOT-safe |
| Realtime VAD mode | Designed in the plan (§6) and `EnergyEndpointDetector` is implemented, but not wired into `PushToTalkController`; push-to-talk is the shipped mode (`D-11`) |

## 6. Acceptance status

| Criterion | Status |
|---|---|
| Compiles without Unity or Python dependencies | **Verified** (netstandard2.1 + C# 9) |
| 40/40 reference cases pass identically | **Verified** — 50/50, fixtures grew |
| Field-level parity with Python | **Verified** — 4 374 utterances, all fields |
| All ASR on background tasks | **Verified by design** — `Task.Run` in `EndSpeechSessionAsync`, `Initialize` awaited off-thread |
| Push-to-talk returns within 150 ms | **Not measured on device** — decode is 10-13 ms/chunk on this machine; the end-to-end number needs a Unity run |
| Zero main-thread audio hiccups | **Not measured on device** — capture ticks per frame; no allocation in the release path |
