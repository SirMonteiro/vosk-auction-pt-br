using System;
using System.Collections.Generic;

namespace AuctionVoice.Audio
{
    /// <summary>
    /// Microphone capture that delivers 16 kHz mono 16-bit PCM.
    /// </summary>
    /// <remarks>
    /// Implementations must not call Unity APIs from a background thread, and must hand out
    /// PCM that the caller owns: the engine feeds it to Vosk on a worker thread while capture
    /// continues.
    /// </remarks>
    public interface IAudioCaptureService : IDisposable
    {
        /// <summary>True while the microphone is open and recording.</summary>
        bool IsCapturing { get; }

        /// <summary>Sample rate of the PCM this service emits.</summary>
        int SampleRate { get; }

        /// <summary>Raised on the main thread for every chunk of captured PCM.</summary>
        event Action<byte[]> OnChunkReady;

        /// <summary>Opens the microphone and starts recording.</summary>
        void StartCapture();

        /// <summary>Closes the microphone.</summary>
        void StopCapture();

        /// <summary>
        /// Returns the most recent <paramref name="milliseconds"/> of audio, for the pre-roll
        /// that protects the first syllable.
        /// </summary>
        byte[] GetPreRoll(int milliseconds);

        /// <summary>Forgets buffered audio. Called after a session is finalised.</summary>
        void ClearPreRoll();
    }

    /// <summary>
    /// A circular buffer of recent PCM, used as a first-syllable pre-roll.
    /// </summary>
    /// <remarks>
    /// Vosk drops the first syllable when audio starts abruptly; keeping the last 250 ms of
    /// idle audio and flushing it immediately after the key goes down measurably fixes this
    /// (<c>docs/08</c> section 6). This type is UnityEngine-free so it can be tested headlessly.
    /// </remarks>
    public sealed class PreRollBuffer
    {
        private readonly byte[] _buffer;
        private int _write;
        private int _count;

        /// <summary>Creates a buffer holding <paramref name="milliseconds"/> of 16-bit PCM.</summary>
        public PreRollBuffer(int milliseconds, int sampleRate = PcmAudio.SampleRate)
        {
            int bytes = Math.Max(2, sampleRate * milliseconds / 1000 * PcmAudio.BytesPerSample);
            // Keep an even byte count so samples never straddle the wrap point.
            _buffer = new byte[bytes - (bytes % 2)];
        }

        /// <summary>Capacity in bytes.</summary>
        public int Capacity => _buffer.Length;

        /// <summary>Bytes currently buffered.</summary>
        public int Count => _count;

        /// <summary>Appends a chunk, overwriting the oldest audio when full.</summary>
        public void Write(byte[] pcm, int count)
        {
            if (pcm == null || count <= 0)
            {
                return;
            }

            int length = Math.Min(count, pcm.Length);
            int index = 0;

            // Drop the oldest bytes rather than the newest: the tail is what protects the
            // first syllable of the next utterance.
            if (length >= _buffer.Length)
            {
                index = length - _buffer.Length;
                Array.Copy(pcm, index, _buffer, 0, _buffer.Length);
                _write = 0;
                _count = _buffer.Length;
                return;
            }

            int spaceToEnd = _buffer.Length - _write;
            if (length <= spaceToEnd)
            {
                Array.Copy(pcm, 0, _buffer, _write, length);
            }
            else
            {
                Array.Copy(pcm, 0, _buffer, _write, spaceToEnd);
                Array.Copy(pcm, spaceToEnd, _buffer, 0, length - spaceToEnd);
            }

            _write = (_write + length) % _buffer.Length;
            _count = Math.Min(_buffer.Length, _count + length);
        }

        /// <summary>Copies the buffered audio out in chronological order.</summary>
        public byte[] Read()
        {
            if (_count == 0)
            {
                return Array.Empty<byte>();
            }

            var result = new byte[_count];
            int start = (_write - _count + _buffer.Length) % _buffer.Length;
            int firstRun = Math.Min(_count, _buffer.Length - start);
            Array.Copy(_buffer, start, result, 0, firstRun);
            if (firstRun < _count)
            {
                Array.Copy(_buffer, 0, result, firstRun, _count - firstRun);
            }

            return result;
        }

        /// <summary>Returns at most the most recent <paramref name="milliseconds"/>.</summary>
        public byte[] Read(int milliseconds, int sampleRate = PcmAudio.SampleRate)
        {
            byte[] all = Read();
            int wanted = sampleRate * milliseconds / 1000 * PcmAudio.BytesPerSample;
            if (wanted >= all.Length)
            {
                return all;
            }

            wanted -= wanted % 2;
            var result = new byte[wanted];
            Array.Copy(all, all.Length - wanted, result, 0, wanted);
            return result;
        }

        /// <summary>Clears the buffer.</summary>
        public void Clear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _write = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// Detects when a speech session should be considered finished.
    /// </summary>
    /// <remarks>
    /// Push-to-talk is the shipped mode (<c>D-11</c>), so this is only used by the optional
    /// realtime extension. The hybrid design combines an RMS gate with Kaldi's own acoustic
    /// endpoint.
    /// </remarks>
    public interface IEndpointDetector
    {
        /// <summary>Feeds a chunk and returns true when the utterance should be finalised.</summary>
        bool ShouldFinalize(byte[] pcm, int count);
    }

    /// <summary>
    /// A sliding-window RMS energy endpoint detector.
    /// </summary>
    /// <remarks>
    /// Cheap (well under 0.1% of a core) and deterministic. Silence to speech needs
    /// <see cref="SpeechOnsetWindows"/> consecutive windows above the noise floor; speech to
    /// finalise needs <see cref="SilenceToFinalizeMs"/> of sustained silence.
    /// </remarks>
    public sealed class EnergyEndpointDetector : IEndpointDetector
    {
        private readonly int _sampleRate;
        private int _consecutiveSpeech;
        private int _silenceMs;

        /// <summary>RMS threshold above which a window counts as speech.</summary>
        public float SpeechThreshold = 0.035f;

        /// <summary>Consecutive windows above threshold needed to declare speech started.</summary>
        public int SpeechOnsetWindows = 3;

        /// <summary>Sustained silence before finalising.</summary>
        public int SilenceToFinalizeMs = 400;

        /// <summary>Window length used for the energy estimate.</summary>
        public int WindowMs = 40;

        /// <summary>True once speech has been detected in the current utterance.</summary>
        public bool SpeechDetected { get; private set; }

        /// <summary>Creates a detector.</summary>
        public EnergyEndpointDetector(int sampleRate = PcmAudio.SampleRate)
        {
            _sampleRate = sampleRate;
        }

        /// <inheritdoc />
        public bool ShouldFinalize(byte[] pcm, int count)
        {
            if (pcm == null || count <= 0)
            {
                return false;
            }

            int length = Math.Min(count, pcm.Length);
            int bytesPerWindow = _sampleRate * WindowMs / 1000 * PcmAudio.BytesPerSample;

            // Evaluate every whole window in the chunk, so a detector fed 100 ms chunks still
            // applies its 40 ms window rules instead of treating the chunk as one window.
            bool finalized = false;
            for (int offset = 0; offset + bytesPerWindow <= length; offset += bytesPerWindow)
            {
                if (EvaluateWindow(pcm, offset, bytesPerWindow))
                {
                    finalized = true;
                }
            }

            int remainder = length % bytesPerWindow;
            if (remainder > 0 && EvaluateWindow(pcm, length - remainder, remainder))
            {
                finalized = true;
            }

            return finalized;
        }

        private bool EvaluateWindow(byte[] pcm, int offset, int length)
        {
            double sum = 0d;
            int samples = length / PcmAudio.BytesPerSample;
            for (int i = 0; i < samples; i++)
            {
                int index = offset + i * PcmAudio.BytesPerSample;
                short sample = (short)(pcm[index] | (pcm[index + 1] << 8));
                double normalized = sample / 32768d;
                sum += normalized * normalized;
            }

            float rms = samples > 0 ? (float)Math.Sqrt(sum / samples) : 0f;

            if (rms >= SpeechThreshold)
            {
                _consecutiveSpeech++;
                _silenceMs = 0;
                if (_consecutiveSpeech >= SpeechOnsetWindows)
                {
                    SpeechDetected = true;
                }

                return false;
            }

            _consecutiveSpeech = 0;
            if (!SpeechDetected)
            {
                return false;
            }

            _silenceMs += (int)PcmAudio.DurationMs(length, _sampleRate);
            return _silenceMs >= SilenceToFinalizeMs;
        }

        /// <summary>Resets the detector for a new utterance.</summary>
        public void Reset()
        {
            _consecutiveSpeech = 0;
            _silenceMs = 0;
            SpeechDetected = false;
        }
    }
}
