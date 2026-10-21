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
        private Resampler _resampler;
        private ChunkAssembler _assembler;
        private float[] _readBuffer;
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
            int chunkSamples = actualRate * ChunkMs / 1000;
            _chunkBuffer = new byte[chunkSamples * PcmAudio.BytesPerSample];
            _readBuffer = new float[chunkSamples];
            _assembler = new ChunkAssembler(chunkSamples);
            _resampler = new Resampler(actualRate, PcmAudio.SampleRate);
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

            while (available > 0)
            {
                // Never ask the assembler for more than it has room for: a partial remainder
                // plus a full chunk is one chunk too many, which used to throw
                // "Destination array was not long enough".
                int take = Math.Min(available, _assembler.Room);
                if (take <= 0)
                {
                    break;
                }

                if (!ReadSamples(_lastSamplePosition, take))
                {
                    break;
                }

                int appended = _assembler.Append(_readBuffer, take);
                if (appended <= 0)
                {
                    break;
                }

                _lastSamplePosition = (_lastSamplePosition + appended) % _clip.samples;
                available -= appended;

                // Emit whole chunks; any remainder stays buffered for the next tick (or for
                // FlushRemainder when the session ends). Dropping the remainder would silently
                // cut up to one chunk of speech off the end of every utterance.
                if (_assembler.IsFull)
                {
                    EmitChunk();
                    _assembler.Reset();
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
            if (!IsCapturing || _clip == null || _assembler.Count <= 0)
            {
                return;
            }

            // Pad the short chunk with silence so the recogniser still receives a full window.
            _assembler.PadWithSilence();
            EmitChunk();
            _assembler.Reset();
        }

        private void EmitChunk()
        {
            int bytes = PcmAudio.FloatToPcm16(
                _assembler.Buffer, _assembler.ChunkSamples, _chunkBuffer);
            byte[] converted = _resampler.Process(_chunkBuffer, bytes);

            _preRoll.Write(converted, converted.Length);
            OnChunkReady?.Invoke(converted);
        }

        /// <summary>
        /// Reads <paramref name="count"/> samples starting at <paramref name="offset"/> into
        /// <see cref="_readBuffer"/>, wrapping at the end of the clip.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="AudioClip.GetData"/> reads <b>exactly</b> <c>data.Length</c> samples and does
        /// not wrap: a read that crosses the end of the ring buffer returns false, and a read that
        /// would run past the end fails outright. Passing an oversized array is therefore a bug
        /// even though the extra samples are ignored by the caller.
        /// </para>
        /// <para>
        /// The wrap branch allocates two exactly-sized arrays. That happens at most once per
        /// <see cref="MaxRecordingSeconds"/> of audio, so it is not a per-frame cost, and it keeps
        /// the read exact rather than "usually right".
        /// </para>
        /// </remarks>
        private bool ReadSamples(int offset, int count)
        {
            int total = _clip.samples;
            if (count <= 0 || count > total)
            {
                return false;
            }

            int firstRun = Math.Min(count, total - offset);

            if (firstRun == count)
            {
                EnsureReadBuffer(count);
                return _clip.GetData(_readBuffer, offset);
            }

            // The read crosses the end of the ring buffer: split it into two exact reads.
            int secondRun = count - firstRun;
            var head = new float[firstRun];
            if (!_clip.GetData(head, offset))
            {
                return false;
            }

            var tail = new float[secondRun];
            if (!_clip.GetData(tail, 0))
            {
                return false;
            }

            EnsureReadBuffer(count);
            Array.Copy(head, 0, _readBuffer, 0, firstRun);
            Array.Copy(tail, 0, _readBuffer, firstRun, secondRun);
            return true;
        }

        private void EnsureReadBuffer(int count)
        {
            if (_readBuffer == null || _readBuffer.Length != count)
            {
                _readBuffer = new float[count];
            }
        }

        /// <inheritdoc />
        public void StopCapture()
        {
            if (!IsCapturing)
            {
                return;
            }

            IsCapturing = false;
            _assembler?.Reset();
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
