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
            int deviceRate = ChooseDeviceRate(minRate, maxRate);
            _sampleRate = deviceRate;

            _clip = Microphone.Start(
                DeviceName, true, Math.Max(1, MaxRecordingSeconds), deviceRate);
            if (_clip == null)
            {
                throw new InvalidOperationException(
                    "Microphone.Start failed. Another application may hold the device.");
            }

            // Vosk wants 16 kHz; resample from the device rate when it differs.
            _chunkBuffer = new byte[PcmAudio.SampleRate * ChunkMs / 1000 * PcmAudio.BytesPerSample];
            _floatBuffer = new float[_clip.frequency * ChunkMs / 1000];
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
            while (available >= chunkSamples)
            {
                if (!ReadSamples(_lastSamplePosition, chunkSamples))
                {
                    break;
                }

                int bytes = PcmAudio.FloatToPcm16(_floatBuffer, chunkSamples, _chunkBuffer);
                byte[] chunk = PcmAudio.Prepare(
                    Slice(_chunkBuffer, bytes), _sampleRate, PcmAudio.SampleRate);

                _preRoll.Write(chunk, chunk.Length);
                OnChunkReady?.Invoke(chunk);

                _lastSamplePosition = (_lastSamplePosition + chunkSamples) % _clip.samples;
                available -= chunkSamples;
            }
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

        private static byte[] Slice(byte[] source, int count)
        {
            if (count == source.Length)
            {
                return source;
            }

            var result = new byte[count];
            Array.Copy(source, result, count);
            return result;
        }

        /// <inheritdoc />
        public void StopCapture()
        {
            if (!IsCapturing)
            {
                return;
            }

            IsCapturing = false;
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
