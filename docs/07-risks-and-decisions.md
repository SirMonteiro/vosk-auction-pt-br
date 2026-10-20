# 07 — Risks, Decisions and Open Questions

## 1. Locked decisions

| ID | Decision | Rationale |
|---|---|---|
| D-1 | New project at `auction-vosk-demo/` | Sibling projects stay untouched as references; no half-migrated state |
| D-2 | Flask + `flask-sock` | Same stack as the working `vosk-python` prototype; WS binary streaming already proven |
| D-3 | LM rebuild after the demo baseline | The demo produces the corpus + eval harness that make a rebuild provable |
| D-4 | TTS corpus only for now | No human recordings available yet; schema stays human-ready |
| D-5 | Default model = `vosk-model-small-pt-0.3` | 145 MB RSS, grammar works, 0.2 s load, CPU-only |
| D-6 | `pruned` = opt-in only | 1.8 GB RSS, and **it ignores runtime grammar** (measured) |
| D-7 | Grammar = phrase list, accented, ≤ 15 000 entries | Measured: word lists truncate; accents required; above 15 k latency collapses without accuracy gain |
| D-8 | `conto` = ×1000 (R$ 1 000) | Correct Brazilian usage; the old code treated `cem conto` as R$ 100, which is wrong. **This changes a legacy test** (`cem conto` 100 → 100 000) |
| D-9 | Parser semantics from `vosk-python`, not `whisper-auction-pt-br` | 40/40 vs 38/40 on the text suite; `whisper` parser drops chained `mais` |
| D-10 | Phonetic repair rewritten, never applied to intent words | Current code turns `quero`→`zero` and `sem lance`→`cem lance` |
| D-11 | No VAD, no continuous listening | PTT gives exact boundaries; VAD adds latency and failure modes |
| D-12 | n-best scored by the parser, not by Vosk confidence | Vosk ranked `"mas vindo de"` above `"mais vinte"` |
| D-13 | No Unity artifacts | Explicit user constraint for this phase |

## 2. Open questions

| ID | Question | Default if unanswered | Impact |
|---|---|---|---|
| Q-1 | Does the game want `value` as an absolute bid or a delta (`mais vinte`)? | Report both: `value` + `value_mode`; game decides | Low — demo reports both |
| Q-2 | Is `conto` ×1000 in your game's fiction, or is it used as "100"? | ×1000 (D-8) | Medium — changes a test |
| Q-3 | Should `dobro` alone be a valid action (double the current bid) or ignored without a number? | `MULTIPLY_ONLY` with factor, as legacy | Low |
| Q-4 | Minimum bid increment enforced by the game? | Demo does not enforce; reports only | Low |
| Q-5 | Should the demo keep the model dropdown (hot-swap) or fix one model? | Keep dropdown, but `pruned` warns that grammar is off | Low |
| Q-6 | Language of the UI and logs? | pt-BR UI, English code/comments/docs | Low |
| Q-7 | Do you want the spelled-digit mode (`cinco zero` = 50) on by default? | On, but only when no other number parse succeeds | Medium |

## 3. Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R-1 | TTS overstates accuracy vs real mics | High | High | Reports label provenance; human corpus tooling ready; never claim TTS = real |
| R-2 | Grammar over 15 k silently degrades latency | Medium | High | Hard cap + explicit refusal + build-time report |
| R-3 | `pruned` used with grammar by mistake | Medium | Medium | Startup check for `HCLr.fst`; warning in UI and logs |
| R-4 | Phonetic repair creates false positives | High | High | Rewrite with class restriction, margin, stoplist; regression tests |
| R-5 | `conto`/`pau`/`barão` semantics differ from the game's fiction | Medium | Medium | D-8 + Q-2; single table to change |
| R-6 | openfst/Kaldi build fails on GCC 16 | High | Medium | conda-forge openfst 1.8.4 or pinned compiler; documented in `05` |
| R-7 | Disk pressure (models + corpus + toolchain) | Low | Medium | 37 GB free; `pruned` optional; corpus capped at 5 000 clips |
| R-8 | Browser AudioWorklet unavailable / mic permission denied | Low | Medium | ScriptProcessor fallback + clear UI error |
| R-9 | First syllable clipped by Vosk | Medium | Medium | 250 ms pre-roll + 300 ms lead silence (measured gain) |
| R-10 | Partial parsing shows wrong intermediate values | High | Low | Partials displayed raw; parser only on final |
| R-11 | Scope creep into Unity | Medium | High | D-13; `unity-handoff.md` is documentation only |
| R-12 | `small` model cannot pronounce some idioms at all | Certain | Medium | NLU fallbacks (`três oitão` → 38), grammar OOV report, LM/lexicon phase |

## 4. Assumptions

- The game will run on a machine with at least 8 GB RAM free; the demo needs ~250 MB.
- The microphone is a normal headset or laptop mic at 16 kHz+; no array processing required.
- Bids are short (< 5 s); the demo does not need long-form transcription.
- The user speaks Brazilian Portuguese; no pt-PT handling beyond the model's own coverage.
- Localhost only; no TLS, no auth, no network exposure.

## 5. Things that would change the plan

| Trigger | Change |
|---|---|
| Real-mic tests show < 50 % accuracy | Prioritize LM rebuild + lexicon before polishing UI |
| The game needs continuous listening | Add a VAD-based mode; revisit padding and endpointing |
| Unity needs the parser in C# | Port `nlu/` first; keep the Python demo as the reference implementation |
| 16 GB proves too tight with pruned | Drop pruned entirely; `small` + grammar is the shipping path |
| Human corpus becomes available | Re-run P3 with `provenance="human"` and re-baseline before P4 |
