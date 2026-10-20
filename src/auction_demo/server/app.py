"""Flask application: page, health, WebSocket streaming and WAV upload."""

from __future__ import annotations

import json
import logging
import threading
import webbrowser
from pathlib import Path

from flask import Flask, jsonify, render_template, request

from auction_demo.asr.audio import (
    duration_ms,
    is_silent,
    pad_utterance,
    trim_trailing_silence,
    wav_to_pcm,
)
from auction_demo.asr.base import ASRResult
from auction_demo.asr.vosk_engine import VoskEngine
from auction_demo.config import Settings, load_settings
from auction_demo.nlu.parser import parse_nbest

logger = logging.getLogger(__name__)

# simple_websocket is the transport under flask_sock; a normal close is code 1000.
try:  # pragma: no cover - the dependency is declared
    from simple_websocket.ws import ConnectionClosed
except ImportError:  # pragma: no cover - fall back to a broad base class
    class ConnectionClosed(Exception):  # type: ignore[no-redef]
        """Placeholder when simple_websocket is unavailable."""

CONTRACT_VERSION = "1"


def create_app(engine: VoskEngine | None = None, settings: Settings | None = None) -> Flask:
    """Build the Flask app. ``engine`` is injectable for tests."""
    settings = settings or load_settings()
    engine = engine or VoskEngine(settings)

    app = Flask(
        __name__,
        template_folder=str(Path(__file__).parent / "templates"),
        static_folder=str(Path(__file__).parent / "static"),
    )
    app.config["ENGINE"] = engine
    app.config["SETTINGS"] = settings

    # flask_sock is optional so the page and /transcribe still work without it.
    try:
        from flask_sock import Sock
    except ImportError:  # pragma: no cover - dependency is declared
        Sock = None  # type: ignore[assignment]

    @app.get("/")
    def index():
        return render_template(
            "index.html",
            model=engine.name,
            grammar=engine.grammar_phrases,
            grammar_enabled=engine.grammar_enabled,
            contract_version=CONTRACT_VERSION,
        )

    @app.get("/health")
    def health():
        return jsonify(
            {
                "status": "ready",
                "contract_version": CONTRACT_VERSION,
                "model": engine.name,
                "grammar_enabled": engine.grammar_enabled,
                "grammar_phrases": engine.grammar_phrases,
                "grammar_dropped": engine.grammar_dropped[:50],
                "sample_rate": settings.sample_rate,
                "load_seconds": round(engine.load_seconds, 3),
            }
        )

    @app.post("/transcribe")
    def transcribe():
        upload = request.files.get("audio")
        if upload is None:
            return jsonify({"error": "Nenhum arquivo de áudio enviado"}), 400

        payload = upload.read()
        if not payload:
            return jsonify({"error": "Arquivo de áudio vazio"}), 400

        try:
            pcm = wav_to_pcm(payload, settings.sample_rate)
        except RuntimeError as exc:
            return jsonify({"error": str(exc)}), 400

        # Gate on the original audio first: a silent upload must report
        # `silence`, not `too_short` after trimming removes its only content.
        result = _evaluate(engine, settings, pcm)
        _print_report(result, settings)
        return jsonify(result.to_dict())

    if Sock is not None:
        sock = Sock(app)

        @sock.route("/stream")
        def stream(ws):  # pragma: no cover - exercised by integration tests
            session = engine.create_session()
            buffer = bytearray()
            started = False

            try:
                while True:
                    message = ws.receive()
                    if message is None:
                        break

                    if isinstance(message, bytes):
                        if not started:
                            started = True
                            session.start()
                        buffer.extend(message)
                        partial = session.feed(message)
                        if partial:
                            ws.send(json.dumps({"type": "partial", "text": partial}))
                        continue

                    try:
                        command = json.loads(message)
                    except json.JSONDecodeError:
                        continue

                    if command.get("type") == "start":
                        buffer.clear()
                        started = True
                        session.start()
                        ws.send(json.dumps({"type": "ready"}))
                    elif command.get("type") == "stop":
                        result = _finalize(engine, settings, session, bytes(buffer))
                        ws.send(json.dumps({"type": "final", **result.to_dict()}))
                        buffer.clear()
                        started = False
                        session.reset()
            except ConnectionClosed:
                # A normal client disconnect (close code 1000) is not an error.
                logger.debug("client disconnected")
            except Exception as exc:  # noqa: BLE001 - a broken socket must not kill the server
                logger.exception("stream error: %s", exc)
                try:
                    ws.send(json.dumps({"type": "error", "message": str(exc)}))
                except Exception:  # noqa: BLE001
                    pass

    return app


def _finalize(engine: VoskEngine, settings: Settings, session, pcm: bytes):
    """Run the tail padding, decode and parse one finished utterance."""
    from auction_demo.asr.audio import silence

    session.feed(silence(settings.tail_silence_ms, settings.sample_rate))
    asr = session.finish()
    return _build_result(engine, settings, asr, pcm)


def _evaluate(engine: VoskEngine, settings: Settings, pcm: bytes):
    """One-shot path used by ``/transcribe``.

    Gating runs on the original buffer; only the audio handed to the decoder is
    trimmed of its trailing silence and padded.
    """
    duration = duration_ms(pcm, settings.sample_rate)
    if duration < settings.min_utterance_ms:
        return _build_result(engine, settings, ASRResult(text=""), pcm, reason="too_short")
    if is_silent(pcm, settings.min_rms):
        return _build_result(engine, settings, ASRResult(text=""), pcm, reason="silence")

    trimmed = trim_trailing_silence(pcm, settings.sample_rate)
    padded = pad_utterance(
        trimmed,
        lead_ms=settings.lead_silence_ms,
        tail_ms=settings.tail_silence_ms,
        sample_rate=settings.sample_rate,
    )
    asr = engine.transcribe_pcm(padded)
    return _build_result(engine, settings, asr, pcm)


def _build_result(engine: VoskEngine, settings: Settings, asr, pcm: bytes, reason: str | None = None):
    """Turn an :class:`ASRResult` into a :class:`BidResult` with gating.

    Gating uses the *unpadded* buffer: RMS measured on padded audio would clear
    the duration gate first and report ``too_short`` instead of ``silence``.
    """
    from auction_demo.nlu.currency import format_brazilian_currency
    from auction_demo.nlu.models import BidResult

    duration = duration_ms(pcm, settings.sample_rate)

    def rejected(r: str) -> BidResult:
        return BidResult(
            raw_transcript=asr.text,
            action_type="NONE",
            bid_kind="UNKNOWN",
            currency=format_brazilian_currency(0.0),
            reason=r,
            latency_ms=asr.decode_ms,
        )

    if reason is not None:
        return rejected(reason)
    if duration < settings.min_utterance_ms:
        return rejected("too_short")
    if not asr.text and is_silent(pcm, settings.min_rms):
        return rejected("silence")

    candidates = asr.nbest_texts()
    result = parse_nbest(candidates, debug=settings.debug)
    result.latency_ms = round(asr.decode_ms, 1)
    result.trace.setdefault("model", engine.name)
    result.trace.setdefault("decode_ms", round(asr.decode_ms, 1))
    return result


def _print_report(result, settings: Settings) -> None:
    """The one intentional ``print`` in the codebase."""
    if settings.quiet:
        return

    width = 60
    print("\n" + "=" * width)
    print("🏛️  AUCTION BID")
    print("-" * width)
    print(f"raw              {result.raw_transcript!r}")
    print(f"normalized       {result.normalized_text!r}")
    if result.nbest:
        for item in result.nbest:
            marker = "  ← chosen" if item.chosen else ""
            print(f"nbest            {item.score:5.2f}  {item.text!r}{marker}")
    print(f"value            {result.value:.2f}   ({result.currency})")
    print(f"value_mode       {result.value_mode}")
    print(f"has_addition     {result.has_addition}")
    print(f"has_multiplication {result.has_multiplication}")
    print(f"action           {result.action_type} / {result.bid_kind}")
    if result.reason:
        print(f"reason           {result.reason}")
    print(f"confidence       {result.confidence:.2f}")
    print(f"latency          {result.latency_ms:.0f} ms")
    print("=" * width)


def serve(settings: Settings, *, open_browser: bool = True) -> None:
    """Load the model and run the development server."""
    logging.basicConfig(
        level=logging.DEBUG if settings.debug else logging.INFO,
        format="%(levelname)s %(name)s: %(message)s",
    )

    engine = VoskEngine(settings)
    app = create_app(engine, settings)

    url = f"http://{settings.host}:{settings.port}"
    print("\n🏛️  auction-vosk-demo")
    print(f"   model   {engine.name}  ({engine.load_seconds:.2f}s)")
    print(f"   grammar {engine.grammar_phrases or 'off'} phrases")
    print(f"   open    {url}\n")

    if open_browser:
        threading.Timer(0.8, lambda: webbrowser.open(url)).start()

    app.run(host=settings.host, port=settings.port, debug=False, threaded=True)
