"""CLI entry point for the demo server.

Kept deliberately thin: argument parsing and startup diagnostics only. All
runtime behaviour lives in :mod:`auction_demo.server.app`.
"""

from __future__ import annotations

import argparse
import sys

from auction_demo.config import load_settings


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="auction-demo",
        description="Voice auction demo: hold Space, speak a bid, see it parsed.",
    )
    parser.add_argument(
        "--model",
        default=None,
        help="Model name under models/ (small, small-lm-v1, pruned) or a path",
    )
    parser.add_argument(
        "--grammar",
        choices=("on", "off"),
        default=None,
        help="Runtime phrase grammar (default: on; ignored by static-graph models)",
    )
    parser.add_argument("--host", default=None, help="Bind host (default 127.0.0.1)")
    parser.add_argument("--port", type=int, default=None, help="Bind port (default 5000)")
    parser.add_argument("--debug", action="store_true", help="Verbose parser trace")
    parser.add_argument("--quiet", action="store_true", help="Suppress the stdout report block")
    parser.add_argument(
        "--no-browser",
        action="store_true",
        help="Do not open a browser tab on startup",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)

    overrides: dict[str, object] = {}
    if args.model is not None:
        overrides["model"] = args.model
    if args.grammar is not None:
        overrides["use_grammar"] = args.grammar == "on"
    if args.host is not None:
        overrides["host"] = args.host
    if args.port is not None:
        overrides["port"] = args.port
    if args.debug:
        overrides["debug"] = True
    if args.quiet:
        overrides["quiet"] = True

    settings = load_settings(**overrides)

    # Imported here so ``--help`` works without the ASR dependencies installed.
    from auction_demo.server.app import serve

    try:
        serve(settings, open_browser=not args.no_browser)
    except FileNotFoundError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
