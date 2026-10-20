"""Audio helpers: PCM validation, silence padding, RMS gating, WAV decoding.

The demo receives 16 kHz mono signed 16-bit little-endian PCM over a WebSocket.
Everything here is pure bytes/numpy manipulation so it can be unit tested
without a model or a microphone.
"""

from __future__ import annotations

import io
import shutil
import subprocess
import tempfile
import wave
from pathlib import Path

import numpy as np

#: PCM frame size in bytes: one 16-bit sample.
BYTES_PER_SAMPLE = 2


def silence(ms: int, sample_rate: int = 16_000) -> bytes:
    """Return ``ms`` of digital silence as 16-bit mono PCM."""
    samples = int(sample_rate * ms / 1000)
    return b"\x00" * (samples * BYTES_PER_SAMPLE)


def duration_ms(pcm: bytes, sample_rate: int = 16_000) -> float:
    """Duration of a PCM buffer in milliseconds."""
    return len(pcm) / BYTES_PER_SAMPLE / sample_rate * 1000


def rms(pcm: bytes) -> float:
    """Root-mean-square amplitude of 16-bit PCM, normalised to [0, 1]."""
    if not pcm:
        return 0.0
    samples = np.frombuffer(pcm, dtype=np.int16).astype(np.float32) / 32768.0
    if samples.size == 0:
        return 0.0
    return float(np.sqrt(np.mean(samples**2)))


def pad_utterance(
    pcm: bytes,
    lead_ms: int = 300,
    tail_ms: int = 400,
    sample_rate: int = 16_000,
) -> bytes:
    """Add leading and trailing silence.

    Measured to improve accuracy: Vosk drops the first syllable when audio starts
    abruptly (``docs/08-measurements-appendix.md`` §6).
    """
    return silence(lead_ms, sample_rate) + pcm + silence(tail_ms, sample_rate)


def is_silent(pcm: bytes, threshold: float = 0.004) -> bool:
    """True when the buffer is too quiet to contain speech."""
    return rms(pcm) < threshold


def trim_trailing_silence(pcm: bytes, sample_rate: int = 16_000, keep_ms: int = 100) -> bytes:
    """Remove trailing near-silence, keeping ``keep_ms`` for the decoder.

    Used by the HTTP upload path, where browsers sometimes append long silent
    tails that slow decoding without adding information.
    """
    if not pcm:
        return pcm
    samples = np.frombuffer(pcm, dtype=np.int16)
    window = max(1, int(sample_rate * 0.02))  # 20 ms windows
    keep = int(sample_rate * keep_ms / 1000)

    end = len(samples)
    while end > keep + window:
        block = samples[end - window : end].astype(np.float32) / 32768.0
        if float(np.sqrt(np.mean(block**2))) >= 0.004:
            break
        end -= window
    return samples[:end].tobytes()


def wav_to_pcm(data: bytes, target_rate: int = 16_000) -> bytes:
    """Decode WAV bytes to mono 16 kHz 16-bit PCM.

    Handles the common cases without ffmpeg (mono/stereo, any integer width,
    any sample rate). Falls back to ``ffmpeg`` for compressed containers such as
    WebM/Opus that the browser may upload to ``/transcribe``.
    """
    try:
        with wave.open(io.BytesIO(data)) as wav:
            channels = wav.getnchannels()
            width = wav.getsampwidth()
            rate = wav.getframerate()
            frames = wav.readframes(wav.getnframes())
    except (wave.Error, EOFError):
        return _ffmpeg_to_pcm(data, target_rate)

    if width == 2:
        samples = np.frombuffer(frames, dtype=np.int16)
    elif width == 1:
        # 8-bit WAV is unsigned.
        samples = (np.frombuffer(frames, dtype=np.uint8).astype(np.int16) - 128) << 8
    elif width == 4:
        samples = (np.frombuffer(frames, dtype=np.int32) >> 16).astype(np.int16)
    else:
        return _ffmpeg_to_pcm(data, target_rate)

    if channels > 1:
        samples = samples.reshape(-1, channels).mean(axis=1).astype(np.int16)

    if rate != target_rate:
        samples = _resample(samples, rate, target_rate)

    return samples.astype(np.int16).tobytes()


def _resample(samples: np.ndarray, source_rate: int, target_rate: int) -> np.ndarray:
    """Linear resampling. Adequate for speech at these rates; no scipy needed."""
    if source_rate == target_rate or samples.size == 0:
        return samples
    duration = samples.size / source_rate
    target_count = int(round(duration * target_rate))
    source_times = np.arange(samples.size) / source_rate
    target_times = np.arange(target_count) / target_rate
    return np.interp(target_times, source_times, samples).astype(np.int16)


def _ffmpeg_to_pcm(data: bytes, target_rate: int) -> bytes:
    """Decode arbitrary audio with ffmpeg. Raises ``RuntimeError`` if unavailable."""
    if shutil.which("ffmpeg") is None:
        raise RuntimeError(
            "Could not decode this audio and ffmpeg is not installed. "
            "Send 16 kHz mono WAV, or install ffmpeg."
        )

    with tempfile.NamedTemporaryFile(suffix=".input", delete=False) as handle:
        handle.write(data)
        source = Path(handle.name)

    try:
        result = subprocess.run(
            [
                "ffmpeg",
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-i",
                str(source),
                "-ar",
                str(target_rate),
                "-ac",
                "1",
                "-f",
                "s16le",
                "-",
            ],
            capture_output=True,
            check=False,
        )
    finally:
        source.unlink(missing_ok=True)

    if result.returncode != 0:
        message = result.stderr.decode("utf-8", errors="replace").strip()
        raise RuntimeError(f"ffmpeg could not decode the audio: {message}")

    return result.stdout
