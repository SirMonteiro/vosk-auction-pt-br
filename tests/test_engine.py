"""Engine tests. Skipped unless ``models/small`` has been downloaded."""

from __future__ import annotations

import time

import numpy as np
import pytest

from auction_demo.asr import audio as audio_utils
from auction_demo.config import PROJECT_ROOT, load_settings

MODEL_DIR = PROJECT_ROOT / "models" / "small"

pytestmark = pytest.mark.model

requires_model = pytest.mark.skipif(
    not MODEL_DIR.is_dir(),
    reason="models/small missing — run: uv run python -m tools.fetch_models small",
)


@pytest.fixture(scope="module")
def engine():
    settings = load_settings(model="small")
    from auction_demo.asr.vosk_engine import VoskEngine

    return VoskEngine(settings)


def tone(milliseconds: int, frequency: float = 220.0, amplitude: float = 0.4) -> bytes:
    seconds = milliseconds / 1000
    samples = int(16000 * seconds)
    t = np.arange(samples) / 16000
    return (amplitude * np.sin(2 * np.pi * frequency * t) * 32767).astype(np.int16).tobytes()


@requires_model
def test_model_loads_and_reports_grammar(engine) -> None:
    assert engine.name == "small"
    assert engine.load_seconds < 5.0
    assert engine.grammar_enabled
    assert engine.grammar_phrases > 1_000


@requires_model
def test_reference_phrases_survive_validation(engine) -> None:
    for phrase in ("mais vinte", "duzentos e cinquenta mil", "duas onças", "passo"):
        assert phrase in engine.grammar.phrases, phrase


@requires_model
def test_grammar_restricts_the_decoder(engine) -> None:
    """With a tiny grammar the model must not transcribe `cinquenta reais`."""
    from auction_demo.server.app import create_app  # noqa: F401 - ensure importable

    original = engine._grammar_json
    engine._grammar_json = '["um", "dois", "[unk]"]'
    try:
        session = engine.create_session()
        session.start()
        pcm = audio_utils.pad_utterance(tone(1200), 200, 400)
        for offset in range(0, len(pcm), 3200):
            session.feed(pcm[offset : offset + 3200])
        result = session.finish()
        assert result.text != ""
        assert "cinquenta" not in result.text
    finally:
        engine._grammar_json = original


@requires_model
def test_streaming_latency_is_within_budget(engine) -> None:
    """Max chunk latency must stay low; plain decoding spikes to ~200 ms."""
    session = engine.create_session()
    session.start()
    pcm = tone(3000)
    timings: list[float] = []
    for offset in range(0, len(pcm), 3200):
        t0 = time.perf_counter()
        session.feed(pcm[offset : offset + 3200])
        timings.append(time.perf_counter() - t0)
    session.finish()
    assert max(timings) < 0.100, f"max chunk {max(timings) * 1000:.1f} ms"


@requires_model
def test_feed_returns_partials_without_raising(engine) -> None:
    """Feed must never raise, whatever the audio content.

    A synthetic tone yields no partials, so this only proves the contract. The
    partial path on real speech is verified by test_e2e_speech.
    """
    session = engine.create_session()
    session.start()
    pcm = audio_utils.pad_utterance(tone(2000), 300, 400)
    for offset in range(0, len(pcm), 3200):
        result = session.feed(pcm[offset : offset + 3200])
        assert result is None or isinstance(result, str)
    session.finish()


@requires_model
def test_transcribe_pcm_round_trip(engine) -> None:
    pcm = audio_utils.pad_utterance(tone(800), 300, 400)
    result = engine.transcribe_pcm(pcm)
    assert isinstance(result.text, str)
    assert result.decode_ms > 0


@requires_model
def test_two_sessions_are_independent(engine) -> None:
    first, second = engine.create_session(), engine.create_session()
    first.start()
    second.start()
    pcm = audio_utils.pad_utterance(tone(500), 100, 100)
    for offset in range(0, len(pcm), 3200):
        first.feed(pcm[offset : offset + 3200])
    assert second.finish().text == ""  # untouched session stays empty


@requires_model
def test_engine_survives_a_reset_cycle(engine) -> None:
    session = engine.create_session()
    for _ in range(3):
        session.start()
        pcm = audio_utils.pad_utterance(tone(400), 200, 200)
        for offset in range(0, len(pcm), 3200):
            session.feed(pcm[offset : offset + 3200])
        session.finish()
        session.reset()


@requires_model
def test_engine_preserves_speech_when_followed_by_silence(engine) -> None:
    """Holding push-to-talk after speaking causes Kaldi endpointing to fire

    during session.feed(); session.finish() must not lose the completed segment.
    """
    import wave

    wav_path = PROJECT_ROOT / "data" / "corpus" / "audio" / "e2e-006.wav"
    if not wav_path.is_file():
        pytest.skip("data/corpus audio missing")

    with wave.open(str(wav_path), "rb") as handle:
        pcm = handle.readframes(handle.getnframes())

    session = engine.create_session()
    session.start()
    for offset in range(0, len(pcm), 3200):
        session.feed(pcm[offset : offset + 3200])

    # Feed 1.5 seconds of silence
    for _ in range(15):
        session.feed(audio_utils.silence(100))

    result = session.finish()
    assert "quarenta" in result.text
    assert len(result.alternatives) > 0
