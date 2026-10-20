# auction-vosk-demo — Planning Index

Status: **planning only** (no runtime code written yet).
Date: 2026-10-20.
Target: a Python demo that opens a web page, records while **Space is held**, and prints the raw
ASR text plus the parsed auction bid (`value`, `has_addition`, `has_sum`, `has_multiplication`,
`action_type`) to **stdout** and to a **textarea with a "copy all" button**.

This folder is the written plan that the implementation will follow. Nothing here is
authoritative over measured behavior: every number below was produced on this machine on
2026-10-19/20 and is reproducible with the commands in `08-measurements-appendix.md`.

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

Replace the Whisper-on-GPU pipeline with **Vosk + `vosk-model-small-pt-0.3` (31 MB, CPU only)**
and a **runtime phrase grammar** limited to auction vocabulary. Measured: 145 MB RSS, 0.2 s load,
RTF 0.02, 9 ms max chunk latency with grammar — versus 150–250 ms spikes plain, and versus
1.8 GB RSS for the `pruned` model (which additionally **ignores** runtime grammar). The NLU layer
is ported from `vosk-python/src/vosk_python/evaluator.py` (the only implementation that scores
40/40 on the text-level suite) and extended with an explicit intent layer, correction handling,
idioms and OOV fallbacks. Fine-tuning is staged after the demo baseline: LM rebuild first
(Kaldi/openfst/opengrm), lexicon/graph second, acoustic last (not viable on `small`).

## Non-goals (explicit)

- No Unity code, no C# artifacts, no export pipeline in this project.
- No GPU/Vulkan dependency at runtime.
- No acoustic fine-tune of `vosk-model-small-pt-0.3` (no published recipe; see `05-finetuning-plan.md`).
- No human-recorded corpus yet — TTS corpus only, schema kept human-ready.
