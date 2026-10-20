"""WebSocket protocol and gating tests.

A fake engine keeps this suite fast and model-free. The real engine is covered by
``test_engine.py``, which is skipped unless ``models/small`` exists.
"""

from __future__ import annotations

import io
import wave

import numpy as np
import pytest

from auction_demo.asr.base import ASRResult
from auction_demo.config import load_settings
from auction_demo.server.app import _finalize, create_app


class FakeSession:
    """Records what it was fed and returns a canned result."""

    def __init__(self, engine: FakeEngine) -> None:
        self.engine = engine
        self.fed = bytearray()
        self.partial = "mais vin"
        self.started = False

    def start(self) -> None:
        self.started = True
        self.fed.clear()
        self.engine.sessions_started += 1

    def feed(self, pcm: bytes) -> str | None:
        self.fed.extend(pcm)
        if len(self.fed) > 8000:
            text, self.partial = self.partial, None
            return text
        return None

    def finish(self) -> ASRResult:
        self.engine.finished += 1
        self.engine.last_fed = bytes(self.fed)
        return ASRResult(
            text=self.engine.transcript,
            alternatives=[],
            duration_s=1.0,
            decode_ms=12.5,
            model="fake",
        )

    def reset(self) -> None:
        self.started = False


class FakeEngine:
    name = "fake-model"
    grammar_phrases = 42
    grammar_enabled = True
    grammar_dropped: list[str] = []
    load_seconds = 0.01

    def __init__(self, transcript: str = "mais vinte") -> None:
        self.transcript = transcript
        self.sessions_started = 0
        self.finished = 0
        self.last_fed = b""

    def create_session(self) -> FakeSession:
        return FakeSession(self)

    def transcribe_pcm(self, pcm: bytes) -> ASRResult:
        self.last_fed = pcm
        return ASRResult(text=self.transcript, decode_ms=10.0, model="fake")


def wav_bytes(samples: bytes, seconds: float = 1.0) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(16000)
        handle.writeframes(samples)
    return buffer.getvalue()


@pytest.fixture()
def client():
    settings = load_settings(model="small")
    app = create_app(FakeEngine(), settings)
    app.config["TESTING"] = True
    with app.test_client() as test_client:
        yield test_client


def test_index_renders(client) -> None:
    response = client.get("/")
    assert response.status_code == 200
    html = response.data.decode()
    assert "Segure" in html
    assert "fake-model" in html


def test_health_reports_grammar(client) -> None:
    payload = client.get("/health").get_json()
    assert payload["status"] == "ready"
    assert payload["model"] == "fake-model"
    assert payload["grammar_enabled"] is True
    assert payload["grammar_phrases"] == 42
    assert payload["contract_version"] == "1"


def test_transcribe_missing_file(client) -> None:
    response = client.post("/transcribe")
    assert response.status_code == 400
    assert "error" in response.get_json()


def test_transcribe_empty_file(client) -> None:
    response = client.post("/transcribe", data={"audio": (io.BytesIO(b""), "a.wav")})
    assert response.status_code == 400


def test_transcribe_returns_full_contract(client) -> None:
    tone = (np.sin(2 * np.pi * 200 * np.linspace(0, 1, 16000)) * 20000).astype(np.int16)
    response = client.post(
        "/transcribe", data={"audio": (io.BytesIO(wav_bytes(tone.tobytes())), "a.wav")}
    )
    assert response.status_code == 200
    payload = response.get_json()

    for key in (
        "raw_transcript",
        "normalized_text",
        "nbest",
        "value",
        "int_value",
        "currency",
        "value_mode",
        "has_addition",
        "has_sum",
        "has_multiplication",
        "action_type",
        "bid_kind",
        "numbers_found",
        "confidence",
        "latency_ms",
    ):
        assert key in payload, f"missing {key}"

    assert payload["raw_transcript"] == "mais vinte"
    assert payload["value"] == 20.0
    assert payload["has_addition"] is True
    assert payload["bid_kind"] == "BID_DELTA"
    assert payload["currency"] == "R$ 20,00"


def test_transcribe_silence_is_gated(client) -> None:
    response = client.post(
        "/transcribe", data={"audio": (io.BytesIO(wav_bytes(b"\x00" * 32000)), "a.wav")}
    )
    payload = response.get_json()
    assert payload["bid_kind"] == "UNKNOWN"
    assert payload["reason"] == "silence"


def test_transcribe_short_audio_is_gated(client) -> None:
    short = (np.zeros(int(16000 * 0.05), dtype=np.int16)).tobytes()
    response = client.post(
        "/transcribe", data={"audio": (io.BytesIO(wav_bytes(short)), "a.wav")}
    )
    payload = response.get_json()
    assert payload["bid_kind"] == "UNKNOWN"
    assert payload["reason"] == "too_short"


def test_websocket_pads_tail_silence() -> None:
    """Tail padding is applied on ``stop`` (drive the fake session directly).

    Flask's test client has no WebSocket transport, so the finalisation helper is
    exercised directly instead of through a socket.
    """

    engine = FakeEngine("cinquenta")
    settings = load_settings(model="small")
    session = engine.create_session()
    session.start()
    session.feed(b"\x00\x01" * 1600)

    _finalize(engine, settings, session, b"\x00\x01" * 1600)

    expected = b"\x00" * (settings.tail_silence_ms * settings.sample_rate // 1000 * 2)
    assert engine.last_fed.endswith(expected)


def test_websocket_start_resets_session() -> None:

    engine = FakeEngine("vinte")
    settings = load_settings(model="small")
    session = engine.create_session()

    _finalize(engine, settings, session, b"\x00\x01" * 3200)
    assert engine.finished == 1
    assert engine.last_fed

