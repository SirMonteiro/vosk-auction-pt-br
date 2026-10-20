# auction-vosk-demo — Planning Index

Status: **planning only** (no runtime code written yet).
Date: 2026-10-20.
Target: a Python demo that opens a web page, records while **Space is held**, and prints the raw
ASR text plus the parsed auction bid (`value`, `has_addition`, `has_sum`, `has_multiplication`,
`action_type`) to **stdout** and to a **textarea with a "copy all" button**.

This folder is the written plan that the implementation will follow. Nothing here is
authoritative over measured behavior: every number below was produced on this machine on
2026-10-19/20 and is reproducible with the commands in `08-measurements-appendix.md`.

> **Planning spike outcome:** the Vosk LM-rebuild pipeline was built and executed during planning
> (openfst 1.8.4 + opengrm 1.3.17), and a rebuilt `Gr.fst` moved the suite from **26–28/40 to
> 37–38/40** value accuracy while making decoding **~8× faster**. That changes the roadmap: the LM
> rebuild is part of P1, not a later research phase. Details in `01` §5 and `08` §15.

## Documents

| File | Purpose |
|---|---|
| `01-context-and-findings.md` | What exists in the three sibling projects, what was measured, what is broken |
| `02-architecture.md` | Target project layout, runtime data flow, module contracts |
| `03-nlu-spec.md` | Number grammar, lexicon, intents, idioms, correction, output contract |
| `04-asr-and-grammar.md` | Vosk model choice, runtime grammar construction, audio pipeline, latency budget |
| `05-finetuning-plan.md` | Adaptation ladder (LM rebuild, lexicon, acoustic), corpus schema, tooling |
| `06-roadmap-and-acceptance.md` | Phases P0–P5, acceptance criteria, test matrix |
| `07-risks-and-decisions.md` | Locked decisions, open questions, risks |
| `08-measurements-appendix.md` | Raw benchmark tables and reproduction commands |
| `unity-handoff.md` | Contract for the later Unity/C# port (no Unity artifacts now) |

## One-paragraph summary

Replace the Whisper-on-GPU pipeline with **Vosk + `vosk-model-small-pt-0.3` (31 MB, CPU only)**,
a **rebuilt `Gr.fst` language model** trained on auction speech, and a **runtime phrase grammar**
limited to auction vocabulary. Measured: 145 MB RSS, 0.18 s load, RTF 0.026, 10–13 ms max chunk
latency, and **37–38/40** value accuracy on the 40-case suite (base model: 26–28/40). The NLU layer
is ported from `vosk-python/src/vosk_python/evaluator.py` (the only implementation that scores
40/40 at text level) and extended with an explicit intent layer, correction handling, idioms and
OOV fallbacks. Fine-tuning is staged inside the demo work: LM rebuild first (proven, `05`),
lexicon/graph second, acoustic last (not viable on `small`).

## Non-goals (explicit)

- No Unity code, no C# artifacts, no export pipeline in this project.
- No GPU/Vulkan dependency at runtime.
- No acoustic fine-tune of `vosk-model-small-pt-0.3` (no published recipe; see `05-finetuning-plan.md`).
- No human-recorded corpus yet — TTS corpus only, schema kept human-ready.
- No lexicon/graph rebuild in P1 (only LM rebuild). Level 2 stays optional (P5).

## Measured headline numbers (TL;DR)

| | base `small` | `small-lm-v1` |
|---|---|---|
| Value accuracy (40 cases, TTS) | 26–28/40 | **37–38/40** |
| `has_addition` / `has_multiplication` | 40/40 / 38/40 | 40/40 / 39/40 |
| RTF (plain) | 0.18–0.30 | **0.026–0.031** |
| Max chunk latency | 130–205 ms | **10–13 ms** |
| Load / RSS | 0.20 s / 498 MB | 0.18 s / 498 MB |
| `Gr.fst` size | 16.0 MB | 4.1 MB |

All audio synthetic (Piper `pt_BR-faber-medium`); these are regression numbers, not field accuracy.

