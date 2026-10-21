using System;
using UnityEngine;

namespace AuctionVoice.Audio
{
    /// <summary>
    /// <see cref="IAudioCaptureService"/> backed by <see cref="Microphone"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Records at the device rate and resamples to 16 kHz, because most desktop devices refuse a
    /// 16 kHz request. Echo cancellation, noise suppression and AGC are disabled where the
    /// platform allows it: Vosk was trained on unprocessed speech, and the game mic should not
    /// pre-filter it.
    /// </para>
    /// <para>
    /// Chunks are delivered from <see cref="Tick"/>, which must be called from
    /// <c>Update()</c>. Recording itself runs on the audio thread, so no Unity API is touched
    /// off the main thread.
    /// </para>
    /// </remarks>
    public sealed class UnityMicrophoneCapture : IAudioCaptureService
    {
        /// <summary>Device name passed to <see cref="Microphone"/>; null selects the default.</summary>
        public string DeviceName;

        /// <summary>Length of each captured chunk in milliseconds.</summary>
        public int ChunkMs = 100;

        /// <summary>Maximum recording length. Vosk bids are short; this bounds the ring buffer.</summary>
        public int MaxRecordingSeconds = 30;

        /// <summary>Length of the idle pre-roll kept for first-syllable protection.</summary>
        public int PreRollMs = 250;

        private AudioClip _clip;
        private PreRollBuffer _preRoll;
        private byte[] _chunkBuffer;
        private float[] _floatBuffer;
        private float[] _wrapScratch;
        private Resampler _resampler;
        private float[] _leftover;
        private int _leftoverCount;
        private int _lastSamplePosition;
        private int _sampleRate = PcmAudio.SampleRate;
        private bool _disposed;

        /// <inheritdoc />
        public bool IsCapturing { get; private set; }

        /// <inheritdoc />
        public int SampleRate => _sampleRate;

        /// <inheritdoc />
        public event Action<byte[]> OnChunkReady;

        /// <summary>Creates a capture service.</summary>
        public UnityMicrophoneCapture()
        {
            _preRoll = new PreRollBuffer(PreRollMs, PcmAudio.SampleRate);
        }

        /// <inheritdoc />
        public void StartCapture()
        {
            if (IsCapturing)
            {
                return;
            }

            if (Microphone.devices.Length == 0)
            {
                throw new InvalidOperationException(
                    "No microphone was found. Grant microphone permission and try again.");
            }

            Microphone.GetDeviceCaps(DeviceName, out int minRate, out int maxRate);
            int requestedRate = ChooseDeviceRate(minRate, maxRate);

            _clip = Microphone.Start(
                DeviceName, true, Math.Max(1, MaxRecordingSeconds), requestedRate);
            if (_clip == null)
            {
                throw new InvalidOperationException(
                    "Microphone.Start failed. Another application may hold the device.");
            }

            // The clip's ACTUAL rate is authoritative: Unity is free to ignore the request and
            // open the device at its native rate. Sizing buffers from the requested rate while
            // the clip produces a different one silently truncates or over-reads every chunk.
            int actualRate = _clip.frequency > 0 ? _clip.frequency : requestedRate;
            _sampleRate = actualRate;

            // Two different sample rates are in play: the device produces `actualRate`, and Vosk
            // wants 16 kHz. Convert at the DEVICE rate first, then resample.
            //
            // Sizing the PCM buffer for 16 kHz while converting device-rate samples is a silent
            // 3x time-compression on a 48 kHz device: FloatToPcm16 truncates to the destination
            // length, so two thirds of every chunk is discarded, and the surviving third is then
            // resampled as though it were the whole chunk.
            _chunkBuffer = new byte[actualRate * ChunkMs / 1000 * PcmAudio.BytesPerSample];
            _floatBuffer = new float[actualRate * ChunkMs / 1000];
            _resampler = new Resampler(actualRate, PcmAudio.SampleRate);
            _leftoverCount = 0;
            _lastSamplePosition = 0;
            IsCapturing = true;
        }

        private static int ChooseDeviceRate(int minRate, int maxRate)
        {
            // Prefer 16 kHz exactly; otherwise take the highest supported rate and downsample.
            if (minRate <= PcmAudio.SampleRate && (maxRate == 0 || maxRate >= PcmAudio.SampleRate))
            {
                return PcmAudio.SampleRate;
            }

            return maxRate > 0 ? maxRate : PcmAudio.SampleRate;
        }

        /// <summary>
        /// Drains new audio. Call once per frame from <c>Update()</c>.
        /// </summary>
        public void Tick()
        {
            if (!IsCapturing || _clip == null)
            {
                return;
            }

            int position = Microphone.GetPosition(DeviceName);
            if (position < 0 || position == _lastSamplePosition)
            {
                return;
            }

            int available = position - _lastSamplePosition;
            if (available < 0)
            {
                // The microphone's ring buffer wrapped.
                available += _clip.samples;
            }

            int chunkSamples = _floatBuffer.Length;

            // Read the tail before touching _lastSamplePosition so a wrap stays consistent.
            while (available > 0)
            {
                int take = Math.Min(chunkSamples, available);
                if (!ReadSamples(_lastSamplePosition, take))
                {
                    break;
                }

                AppendToLeftover(take);

                _lastSamplePosition = (_lastSamplePosition + take) % _clip.samples;
                available -= take;

                // Emit whole chunks; the remainder stays buffered for the next tick (or for
                // FlushRemainder when the session ends). Emitting only whole chunks and dropping
                // the remainder would silently cut up to one chunk of speech off the end of
                // every utterance, which is exactly where the final syllable lives.
                while (_leftoverCount >= chunkSamples)
                {
                    EmitChunk(chunkSamples);
                    Array.Copy(
                        _leftover, chunkSamples, _leftover, 0, _leftoverCount - chunkSamples);
                    _leftoverCount -= chunkSamples;
                }
            }
        }

        /// <summary>
        /// Emits any buffered partial chunk. Call before stopping capture.
        /// </summary>
        /// <remarks>
        /// Without this the last few milliseconds of the utterance (up to <see cref="ChunkMs"/>)
        /// never reach the recogniser, so the final consonant of a bid is clipped even though the
        /// player held the key long enough.
        /// </remarks>
        public void FlushRemainder()
        {
            if (!IsCapturing || _clip == null || _leftoverCount <= 0)
            {
                return;
            }

            // Pad the short chunk with silence so the recogniser still receives a full window.
            int chunkSamples = _floatBuffer.Length;
            Array.Clear(_leftover, _leftoverCount, chunkSamples - _leftoverCount);
            EmitChunk(chunkSamples);
            _leftoverCount = 0;
        }

        private void AppendToLeftover(int count)
        {
            if (_leftover == null || _leftover.Length < _floatBuffer.Length)
            {
                _leftover = new float[_floatBuffer.Length];
            }

            Array.Copy(_floatBuffer, 0, _leftover, _leftoverCount, count);
            _leftoverCount += count;
        }

        private void EmitChunk(int chunkSamples)
        {
            int bytes = PcmAudio.FloatToPcm16(_leftover, chunkSamples, _chunkBuffer);
            byte[] converted = _resampler.Process(_chunkBuffer, bytes);

            _preRoll.Write(converted, converted.Length);
            OnChunkReady?.Invoke(converted);
        }

        /// <summary>
        /// Reads <paramref name="count"/> samples starting at <paramref name="offset"/>, wrapping
        /// at the end of the clip.
        /// </summary>
        /// <remarks>
        /// <see cref="AudioClip.GetData"/> does not wrap: a read that crosses the end of the ring
        /// buffer returns false. Without splitting the read, capture would stall permanently the
        /// first time the buffer wrapped.
        /// </remarks>
        private bool ReadSamples(int offset, int count)
        {
            int total = _clip.samples;
            int firstRun = Math.Min(count, total - offset);

            if (firstRun == count)
            {
                return _clip.GetData(_floatBuffer, offset);
            }

            int secondRun = count - firstRun;

            // Read the head into a scratch buffer, the tail into the main buffer, then recombine
            // so the result is contiguous and chronological.
            if (_wrapScratch == null || _wrapScratch.Length < count)
            {
                _wrapScratch = new float[count];
            }

            if (!_clip.GetData(_wrapScratch, offset))
            {
                return false;
            }

            if (!_clip.GetData(_floatBuffer, 0))
            {
                return false;
            }

            // _floatBuffer now holds [0, secondRun) of the tail. Shift it up and put the head first.
            Array.Copy(_floatBuffer, 0, _floatBuffer, firstRun, secondRun);
            Array.Copy(_wrapScratch, 0, _floatBuffer, 0, firstRun);
            return true;
        }

        /// <inheritdoc />
        public void StopCapture()
        {
            if (!IsCapturing)
            {
                return;
            }

            IsCapturing = false;
            _leftoverCount = 0;
            if (!string.IsNullOrEmpty(DeviceName) || Microphone.IsRecording(DeviceName))
            {
                Microphone.End(DeviceName);
            }

            _clip = null;
        }

        /// <inheritdoc />
        public byte[] GetPreRoll(int milliseconds)
        {
            return _preRoll.Read(milliseconds);
        }

        /// <inheritdoc />
        public void ClearPreRoll()
        {
            _preRoll.Clear();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            StopCapture();
            _disposed = true;
        }
    }
}
