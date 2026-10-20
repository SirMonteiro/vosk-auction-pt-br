"""ASR interface and result types.

The demo has exactly one implementation (:class:`auction_demo.asr.vosk_engine.VoskEngine`)
but tests use fakes, so the boundary is explicit.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Protocol, runtime_checkable


@dataclass(slots=True)
class Alternative:
    """One decoder hypothesis."""

    text: str
    confidence: float = 0.0
    words: list[dict] = field(default_factory=list)


@dataclass(slots=True)
class ASRResult:
    """Everything the decoder produced for one utterance."""

    text: str
    alternatives: list[Alternative] = field(default_factory=list)
    duration_s: float = 0.0
    decode_ms: float = 0.0
    model: str = ""

    def nbest_texts(self) -> list[str]:
        """Distinct alternative texts, best first, always including ``text``."""
        seen: list[str] = []
        for candidate in [self.text, *(alt.text for alt in self.alternatives)]:
            normalized = " ".join(candidate.split())
            if normalized and normalized not in seen:
                seen.append(normalized)
        return seen


@runtime_checkable
class StreamingASR(Protocol):
    """A per-connection streaming recogniser."""

    name: str

    def start(self) -> None:
        """Reset for a new utterance."""

    def feed(self, pcm: bytes) -> str | None:
        """Consume PCM; return the current partial transcript when it changed."""

    def finish(self) -> ASRResult:
        """Flush and return the final result."""

    def reset(self) -> None:
        """Drop any buffered audio and prepare for the next utterance."""


@runtime_checkable
class ASREngine(Protocol):
    """A loaded model that can create streaming sessions."""

    name: str

    def create_session(self) -> StreamingASR:
        """Create a session. Sessions are not thread-safe; one per connection."""

    def transcribe_pcm(self, pcm: bytes) -> ASRResult:
        """Convenience one-shot transcription for the HTTP upload path."""
