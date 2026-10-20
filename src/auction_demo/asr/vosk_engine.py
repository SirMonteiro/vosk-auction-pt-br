"""Vosk engine: model lifecycle, streaming sessions, n-best decoding.

One :class:`VoskEngine` is shared by the process (Vosk models are read-only and
safe to share). Each WebSocket connection gets its own :class:`VoskSession`,
because ``KaldiRecognizer`` is not thread-safe.
"""

from __future__ import annotations

import json
import logging
import threading
import time

from auction_demo.asr.base import Alternative, ASRResult
from auction_demo.config import Settings
from auction_demo.nlu.grammar import GrammarReport, build_grammar

logger = logging.getLogger(__name__)


class VoskEngine:
    """Loaded Vosk model plus the optional runtime grammar."""

    def __init__(self, settings: Settings) -> None:
        from vosk import Model, SetLogLevel

        self.settings = settings
        self.model_path = settings.resolve_model_path()
        self.name = self.model_path.name
        self._supports_grammar = (
            (self.model_path / "HCLr.fst").is_file() and (self.model_path / "Gr.fst").is_file()
        )

        SetLogLevel(0 if settings.debug else -1)
        started = time.perf_counter()
        self._model = Model(str(self.model_path))
        self.load_seconds = time.perf_counter() - started

        self.grammar: GrammarReport | None = None
        if settings.use_grammar and not self._supports_grammar:
            logger.warning(
                "%s uses a static graph and ignores runtime grammars; continuing without one",
                self.name,
            )
        elif settings.use_grammar:
            self.grammar = build_grammar(
                max_phrases=settings.grammar_max_phrases,
                hard_limit=settings.grammar_hard_limit,
                is_known_word=self._model.vosk_model_find_word,
            )
            logger.info(
                "grammar: %d phrases, %d dropped as OOV",
                self.grammar.size,
                len(self.grammar.dropped),
            )

        self._grammar_json = self.grammar.to_json() if self.grammar else None

    # ------------------------------------------------------------------
    @property
    def grammar_phrases(self) -> int:
        return self.grammar.size if self.grammar else 0

    @property
    def grammar_enabled(self) -> bool:
        return self._grammar_json is not None

    @property
    def grammar_dropped(self) -> list[str]:
        return self.grammar.dropped if self.grammar else []

    def create_session(self) -> VoskSession:
        return VoskSession(self)

    def transcribe_pcm(self, pcm: bytes) -> ASRResult:
        """One-shot transcription, used by ``/transcribe`` and the tools."""
        session = self.create_session()
        session.start()
        for offset in range(0, len(pcm), self.settings.frame_bytes()):
            session.feed(pcm[offset : offset + self.settings.frame_bytes()])
        return session.finish()

    # ------------------------------------------------------------------
    def _new_recognizer(self):
        from vosk import KaldiRecognizer

        if self._grammar_json is not None:
            recognizer = KaldiRecognizer(self._model, self.settings.sample_rate, self._grammar_json)
        else:
            recognizer = KaldiRecognizer(self._model, self.settings.sample_rate)
        recognizer.SetWords(True)
        if self.settings.nbest > 1:
            recognizer.SetMaxAlternatives(self.settings.nbest)
        return recognizer


class VoskSession:
    """Streaming session bound to one connection."""

    def __init__(self, engine: VoskEngine) -> None:
        self.engine = engine
        self.name = engine.name
        self._recognizer = engine._new_recognizer()
        self._lock = threading.Lock()
        self._started = time.perf_counter()
        self._last_partial = ""
        self._first_partial_at: float | None = None
        self._final_at: float | None = None

    # ------------------------------------------------------------------
    def start(self) -> None:
        with self._lock:
            self._recognizer.Reset()
            self._started = time.perf_counter()
            self._last_partial = ""
            self._first_partial_at = None
            self._final_at = None

    def feed(self, pcm: bytes) -> str | None:
        """Consume PCM and return a partial transcript only when it changed."""
        if not pcm:
            return None
        with self._lock:
            finished = self._recognizer.AcceptWaveform(pcm)
            if finished:
                # An endpoint fired mid-stream; the text is already final for
                # that segment, so surface it as a partial rather than losing it.
                payload = json.loads(self._recognizer.Result())
                text = payload.get("text", "").strip()
                if text and text != self._last_partial:
                    self._last_partial = text
                    self._first_partial_at = self._first_partial_at or time.perf_counter()
                    return text
                return None

            payload = json.loads(self._recognizer.PartialResult())
            partial = payload.get("partial", "").strip()
            if partial and partial != self._last_partial:
                self._last_partial = partial
                self._first_partial_at = self._first_partial_at or time.perf_counter()
                return partial
            return None

    def finish(self) -> ASRResult:
        with self._lock:
            payload = json.loads(self._recognizer.FinalResult())
            self._final_at = time.perf_counter()

        alternatives = _parse_alternatives(payload)
        text = payload.get("text", "").strip()
        if not text and alternatives:
            text = alternatives[0].text

        decode_ms = (self._final_at - self._started) * 1000
        return ASRResult(
            text=text,
            alternatives=alternatives,
            duration_s=0.0,
            decode_ms=decode_ms,
            model=self.engine.name,
        )

    def reset(self) -> None:
        self.start()


def _parse_alternatives(payload: dict) -> list[Alternative]:
    """Extract n-best hypotheses from a Vosk final result.

    Vosk returns ``{"text": ...}`` normally, or ``{"alternatives": [...]}`` when
    ``SetMaxAlternatives`` is active. Confidence is reported but is *not*
    trustworthy for ranking (see ``docs/08-measurements-appendix.md`` §8), so the
    parser re-scores every alternative.
    """
    raw = payload.get("alternatives")
    if not raw:
        text = payload.get("text", "").strip()
        return [Alternative(text=text)] if text else []

    alternatives: list[Alternative] = []
    for item in raw:
        text = item.get("text", "").strip()
        if not text:
            continue
        alternatives.append(
            Alternative(
                text=text,
                confidence=float(item.get("confidence", 0.0)),
                words=item.get("result", []),
            )
        )
    return alternatives
