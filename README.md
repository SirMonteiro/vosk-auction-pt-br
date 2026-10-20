# auction-vosk-demo

Voice-driven auction demo: hold **Space**, speak a bid in Brazilian Portuguese, and see the raw
ASR transcript plus the parsed bid (`value`, `has_addition`, `has_sum`, `has_multiplication`,
`action_type`) printed to **stdout** and to a **textarea with a "copy all" button**.

Vosk runs on the CPU with a 31 MB pt-BR model — no GPU, no VRAM, no Vulkan.

## Quick start

```bash
uv sync
uv run python -m tools.fetch_models small   # ~31 MB, verifies md5
uv run auction-demo                         # opens http://127.0.0.1:5000
```

Then hold **Space** in the page and say something like `mais vinte mais vinte mais sete`.

## Options

```bash
uv run auction-demo --model small        # base model (no rebuilt LM)
uv run auction-demo --model pruned       # 1.1 GB accuracy mode; runtime grammar is ignored
uv run auction-demo --grammar off        # disable the phrase grammar
uv run auction-demo --port 5001 --no-browser
uv run auction-demo --debug              # per-stage parser trace in the JSON
```

## Development

```bash
uv run pytest -q                 # unit tests (no model needed)
uv run pytest -q -m model        # engine tests (needs models/ downloaded)
uv run ruff check .
```

## Documentation

The full design lives in [`docs/`](docs/README.md):

- [`docs/02-architecture.md`](docs/02-architecture.md) — layout, data flow, module contracts
- [`docs/03-nlu-spec.md`](docs/03-nlu-spec.md) — number grammar, intents, bid semantics
- [`docs/04-asr-and-grammar.md`](docs/04-asr-and-grammar.md) — model choice, runtime grammar
- [`docs/05-finetuning-plan.md`](docs/05-finetuning-plan.md) — LM rebuild pipeline
- [`docs/unity-handoff.md`](docs/unity-handoff.md) — JSON contract for the later Unity port

## Layout

```
src/auction_demo/
  config.py        settings (env-overridable)
  cli.py           entry point
  asr/             Vosk engine, audio helpers
  nlu/             lexicon, numbers, intents, parser, grammar
  server/          Flask app, WS endpoint, templates, static assets
tools/             model fetch, corpus generation, LM rebuild, evaluation
tests/             unit tests + WS integration tests
docs/              the plan this implementation follows
artifacts/         benchmark scratch (gitignored)
```

## Status

Phases P0 (scaffold) and P1 (streaming engine + UI) are implemented. The NLU parser is P2.
See [`docs/06-roadmap-and-acceptance.md`](docs/06-roadmap-and-acceptance.md).
