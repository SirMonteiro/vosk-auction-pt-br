"""Audio helper tests: silence, RMS, padding, WAV decoding, gating."""

from __future__ import annotations

import numpy as np
import pytest

from auction_demo.asr import audio


def tone(milliseconds: int, frequency: float = 200.0, amplitude: float = 0.5) -> bytes:
    samples = int(16000 * milliseconds / 1000)
    t = np.arange(samples) / 16000
    values = amplitude * np.sin(2 * np.pi * frequency * t)
    return (values * 32767).astype(np.int16).tobytes()


def test_silence_length() -> None:
    assert len(audio.silence(100)) == 3200
    assert audio.silence(100) == b"\x00" * 3200


def test_duration_and_rms() -> None:
    pcm = tone(250)
    assert audio.duration_ms(pcm) == pytest.approx(250.0, abs=0.5)
    assert audio.rms(pcm) == pytest.approx(0.354, abs=0.02)
    assert audio.rms(b"") == 0.0


def test_pad_utterance() -> None:
    padded = audio.pad_utterance(tone(100), lead_ms=300, tail_ms=400)
    assert audio.duration_ms(padded) == pytest.approx(800.0, abs=1.0)
    assert audio.rms(padded[:300 * 32]) == 0.0


def test_is_silent() -> None:
    assert audio.is_silent(audio.silence(100), threshold=0.004)
    assert not audio.is_silent(tone(100), threshold=0.004)


def test_trim_trailing_silence() -> None:
    padded = audio.pad_utterance(tone(300), lead_ms=0, tail_ms=900)
    trimmed = audio.trim_trailing_silence(padded)
    assert audio.duration_ms(trimmed) < audio.duration_ms(padded)
    assert audio.duration_ms(trimmed) >= 300  # speech preserved


def test_trim_keeps_short_buffers_intact() -> None:
    assert audio.trim_trailing_silence(tone(50)) == tone(50)


def _wav(samples: bytes, channels: int, width: int, rate: int) -> bytes:
    import io
    import wave

    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as handle:
        handle.setnchannels(channels)
        handle.setsampwidth(width)
        handle.setframerate(rate)
        handle.writeframes(samples)
    return buffer.getvalue()


def test_wav_to_pcm_mono_16k() -> None:
    pcm = tone(200)
    assert audio.wav_to_pcm(_wav(pcm, 1, 2, 16000))[: len(pcm)] == pcm


def test_wav_to_pcm_stereo_is_downmixed() -> None:
    mono = tone(200)
    stereo = b"".join(mono[i : i + 2] * 2 for i in range(0, len(mono), 2))
    converted = audio.wav_to_pcm(_wav(stereo, 2, 2, 16000))
    assert audio.duration_ms(converted) == pytest.approx(200.0, abs=1.0)
    assert audio.rms(converted) == pytest.approx(audio.rms(mono), rel=0.05)


def test_wav_to_pcm_resamples_from_44k() -> None:
    samples = int(44100 * 0.2)
    t = np.arange(samples) / 44100
    wav = _wav((0.5 * np.sin(2 * np.pi * 200 * t) * 32767).astype(np.int16).tobytes(), 1, 2, 44100)
    converted = audio.wav_to_pcm(wav, target_rate=16000)
    assert audio.duration_ms(converted) == pytest.approx(200.0, abs=2.0)


def test_wav_to_pcm_8bit_unsigned() -> None:
    data = bytes([128 + 32] * 1600)
    converted = audio.wav_to_pcm(_wav(data, 1, 1, 16000))
    assert audio.rms(converted) > 0.0


def test_wav_to_pcm_rejects_garbage() -> None:
    with pytest.raises(RuntimeError, match="ffmpeg"):
        audio.wav_to_pcm(b"not audio at all")
