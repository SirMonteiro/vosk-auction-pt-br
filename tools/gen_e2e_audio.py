"""Generate a small real-speech corpus with the local Piper voice.

This is a smoke-test helper for the end-to-end pipeline, not the P3 corpus tool
(which will support multiple voices, speeds and augmentation). It writes WAVs and
a JSONL manifest into ``data/corpus``.

Usage::

    artifacts/v312/bin/python tools/gen_e2e_audio.py
"""

from __future__ import annotations

import io
import json
import math
import wave
from pathlib import Path

import numpy as np
from piper import PiperVoice, SynthesisConfig

PROJECT_ROOT = Path(__file__).resolve().parents[1]
VOICE = PROJECT_ROOT / "artifacts" / "piper-voices" / "pt_BR-faber-medium.onnx"
OUT_DIR = PROJECT_ROOT / "data" / "corpus" / "audio"
MANIFEST = PROJECT_ROOT / "data" / "corpus" / "e2e.jsonl"

PHRASES = [
    "mais vinte mais vinte mais sete",
    "cinquenta reais",
    "duzentos e cinquenta mil",
    "duas onças",
    "passo",
    "um milhão e duzentos mil",
    "quarenta e sete",
    "cem mil",
    "meia dúzia",
    "três placas",
]


def synthesize(voice: PiperVoice, text: str, length_scale: float = 0.9) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as handle:
        voice.synthesize_wav(text, handle, syn_config=SynthesisConfig(length_scale=length_scale))
    buffer.seek(0)
    reader = wave.open(buffer)
    rate = reader.getframerate()
    samples = np.frombuffer(reader.readframes(reader.getnframes()), dtype=np.int16)
    audio = samples.astype(np.float32) / 32768.0
    divisor = math.gcd(rate, 16_000)
    resampled = np.zeros(int(audio.size * 16_000 // rate), dtype=np.float32)
    if rate != 16_000 and audio.size:
        source = np.arange(audio.size) / rate
        target = np.arange(resampled.size) / 16_000
        resampled = np.interp(target, source, audio).astype(np.float32)
    else:
        resampled = audio
    divisor = divisor  # keep the gcd import meaningful for older numpy
    return (np.clip(resampled, -1, 1) * 32767).astype(np.int16).tobytes()


def write_wav(path: Path, pcm: bytes) -> None:
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(16_000)
        handle.writeframes(pcm)


def main() -> int:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    voice = PiperVoice.load(str(VOICE))

    rows: list[dict] = []
    for index, phrase in enumerate(PHRASES):
        pcm = synthesize(voice, phrase)
        path = OUT_DIR / f"e2e-{index:03d}.wav"
        write_wav(path, pcm)
        rows.append(
            {"id": f"e2e-{index:03d}", "audio": str(path.relative_to(PROJECT_ROOT)), "text": phrase}
        )
        print(f"  {path.name}  {len(pcm) / 32000:.2f}s  {phrase!r}")

    MANIFEST.write_text(
        "\n".join(json.dumps(row, ensure_ascii=False) for row in rows) + "\n", encoding="utf-8"
    )
    print(f"\n{len(rows)} clips -> {MANIFEST}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
