using System;

namespace AuctionVoice.Audio
{
    /// <summary>
    /// Pure PCM helpers: conversion, silence padding, RMS gating and resampling.
    /// </summary>
    /// <remarks>
    /// Deliberately free of any UnityEngine reference so the audio maths can be unit tested
    /// headlessly and reused on a worker thread. All buffers are 16 kHz mono signed 16-bit
    /// little-endian, which is what Vosk expects.
    /// </remarks>
    public static class PcmAudio
    {
        /// <summary>Bytes per 16-bit sample.</summary>
        public const int BytesPerSample = 2;

        /// <summary>Sample rate the recogniser is created with.</summary>
        public const int SampleRate = 16000;

        /// <summary>Returns <paramref name="ms"/> of digital silence as 16-bit mono PCM.</summary>
        public static byte[] Silence(int ms, int sampleRate = SampleRate)
        {
            int samples = sampleRate * ms / 1000;
            return new byte[samples * BytesPerSample];
        }

        /// <summary>Duration of a PCM buffer in milliseconds.</summary>
        public static float DurationMs(int byteCount, int sampleRate = SampleRate)
        {
            return byteCount / (float)BytesPerSample / sampleRate * 1000f;
        }

        /// <summary>Root-mean-square amplitude of 16-bit PCM, normalised to [0, 1].</summary>
        public static float Rms(byte[] pcm, int count = -1)
        {
            if (pcm == null)
            {
                return 0f;
            }

            int length = count < 0 ? pcm.Length : Math.Min(count, pcm.Length);
            int samples = length / BytesPerSample;
            if (samples <= 0)
            {
                return 0f;
            }

            double sum = 0d;
            for (int i = 0; i < samples; i++)
            {
                float sample = ReadSample(pcm, i * BytesPerSample) / 32768f;
                sum += sample * sample;
            }

            return (float)Math.Sqrt(sum / samples);
        }

        /// <summary>
        /// Maximum RMS amplitude across any sliding window of <paramref name="windowMs"/>.
        /// </summary>
        /// <remarks>
        /// Used by the silence gate for buffers longer than 250 ms: a global RMS would let
        /// silence on either side of a short utterance dilute real speech below the threshold.
        /// </remarks>
        public static float MaxWindowRms(byte[] pcm, int windowMs = 100, int sampleRate = SampleRate)
        {
            if (pcm == null || pcm.Length == 0)
            {
                return 0f;
            }

            int totalSamples = pcm.Length / BytesPerSample;
            int window = sampleRate * windowMs / 1000;
            if (totalSamples <= window)
            {
                return Rms(pcm);
            }

            int stride = Math.Max(1, window / 2);
            float max = 0f;
            for (int start = 0; start + window <= totalSamples; start += stride)
            {
                double sum = 0d;
                for (int i = 0; i < window; i++)
                {
                    float sample = ReadSample(pcm, (start + i) * BytesPerSample) / 32768f;
                    sum += sample * sample;
                }

                float value = (float)Math.Sqrt(sum / window);
                if (value > max)
                {
                    max = value;
                }
            }

            return max;
        }

        /// <summary>
        /// True when the buffer is too quiet to contain speech.
        /// </summary>
        /// <remarks>
        /// For short buffers (250 ms or less) global RMS is used. For longer buffers the sliding
        /// maximum prevents speech from being diluted by surrounding silence when push-to-talk
        /// is held before or after speaking.
        /// </remarks>
        public static bool IsSilent(byte[] pcm, float threshold = 0.004f, int sampleRate = SampleRate)
        {
            if (pcm == null || pcm.Length == 0)
            {
                return true;
            }

            if (DurationMs(pcm.Length, sampleRate) <= 250f)
            {
                return Rms(pcm) < threshold;
            }

            return MaxWindowRms(pcm, 100, sampleRate) < threshold;
        }

        /// <summary>
        /// Converts float samples in [-1, 1] to 16-bit PCM, writing into
        /// <paramref name="destination"/> without allocating.
        /// </summary>
        /// <returns>The number of bytes written.</returns>
        public static int FloatToPcm16(float[] source, int sourceCount, byte[] destination)
        {
            if (source == null || destination == null)
            {
                return 0;
            }

            int count = Math.Min(sourceCount, source.Length);
            int maxBytes = Math.Min(destination.Length, count * BytesPerSample);
            int samples = maxBytes / BytesPerSample;

            for (int i = 0; i < samples; i++)
            {
                float value = source[i];
                if (value > 1f)
                {
                    value = 1f;
                }
                else if (value < -1f)
                {
                    value = -1f;
                }

                short sample = (short)(value * 32767f);
                destination[i * 2] = (byte)(sample & 0xFF);
                destination[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            return samples * BytesPerSample;
        }

        /// <summary>
        /// Downsamples or upsamples 16-bit mono PCM by linear interpolation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Adequate for speech at these rates and free of a DSP dependency, matching the
        /// reference implementation's <c>_resample</c>.
        /// </para>
        /// <para>
        /// Per-chunk resampling loses one sample to rounding whenever the ratio is not an
        /// integer. At 48 kHz to 16 kHz the ratio is exactly 3, so a 100 ms chunk maps to exactly
        /// 1600 samples and nothing is lost. For non-integer ratios use
        /// <see cref="Resampler"/>, which carries the fractional position across chunks.
        /// </para>
        /// </remarks>
        public static byte[] Resample(byte[] pcm, int sourceRate, int targetRate)
        {
            if (pcm == null || pcm.Length == 0 || sourceRate == targetRate)
            {
                return pcm;
            }

            var resampler = new Resampler(sourceRate, targetRate);
            return resampler.Process(pcm, pcm.Length);
        }

        /// <summary>
        /// Mixes a mono buffer down to 16 kHz mono, resampling when necessary.
        /// </summary>
        public static byte[] Prepare(byte[] pcm, int sourceRate, int targetRate = SampleRate)
        {
            if (pcm == null)
            {
                return Array.Empty<byte>();
            }

            return sourceRate == targetRate ? pcm : Resample(pcm, sourceRate, targetRate);
        }

        private static short ReadSample(byte[] pcm, int byteOffset)
        {
            if (byteOffset < 0 || byteOffset + 1 >= pcm.Length)
            {
                return 0;
            }

            return (short)(pcm[byteOffset] | (pcm[byteOffset + 1] << 8));
        }
    }

    /// <summary>
    /// Streaming linear resampler that preserves the fractional read position across chunks.
    /// </summary>
    /// <remarks>
    /// Resampling each captured chunk independently and rounding the output length loses a
    /// sample whenever the rate ratio is not an integer (44.1 kHz to 16 kHz is 2.756...). Over a
    /// few seconds those losses accumulate into a drifting, time-compressed signal, which the
    /// decoder hears as clipped, rushed speech. Carrying the remainder makes the output rate
    /// exact in aggregate.
    /// </remarks>
    public sealed class Resampler
    {
        private readonly int _sourceRate;
        private readonly int _targetRate;
        private readonly double _step;
        private double _position;

        /// <summary>Creates a resampler between two rates.</summary>
        public Resampler(int sourceRate, int targetRate)
        {
            if (sourceRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceRate));
            }

            if (targetRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetRate));
            }

            _sourceRate = sourceRate;
            _targetRate = targetRate;
            _step = (double)sourceRate / targetRate;
            _position = 0d;
        }

        /// <summary>True when the two rates are equal, so processing is a copy.</summary>
        public bool IsIdentity => _sourceRate == _targetRate;

        /// <summary>Converts one chunk, carrying the fractional position into the next call.</summary>
        public byte[] Process(byte[] pcm, int count)
        {
            if (pcm == null || count <= 0)
            {
                return Array.Empty<byte>();
            }

            int length = Math.Min(count, pcm.Length);
            int sourceSamples = length / PcmAudio.BytesPerSample;
            if (sourceSamples == 0)
            {
                return Array.Empty<byte>();
            }

            if (IsIdentity)
            {
                var copy = new byte[sourceSamples * PcmAudio.BytesPerSample];
                Array.Copy(pcm, copy, copy.Length);
                return copy;
            }

            // Worst case is upsampling by the ratio; one extra sample covers the carried phase.
            int capacity = (int)Math.Ceiling(sourceSamples / _step) + 2;
            var output = new byte[capacity * PcmAudio.BytesPerSample];
            int written = 0;

            while (_position < sourceSamples)
            {
                int index = (int)_position;
                double fraction = _position - index;

                // The carried phase is always within [0, 1) of this chunk, so index 0 with a
                // non-zero fraction interpolates between sample 0 and sample 1 of THIS chunk.
                // Using a remembered previous sample here would zero the first output sample of
                // every chunk.
                short a = ReadSampleAt(pcm, index);
                short b = index + 1 < sourceSamples ? ReadSampleAt(pcm, index + 1) : a;
                short sample = (short)(a + (b - a) * fraction);

                output[written * 2] = (byte)(sample & 0xFF);
                output[written * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                written++;
                _position += _step;
            }

            // Carry only the fractional phase into the next chunk.
            _position -= sourceSamples;

            if (written * 2 == output.Length)
            {
                return output;
            }

            var trimmed = new byte[written * PcmAudio.BytesPerSample];
            Array.Copy(output, trimmed, trimmed.Length);
            return trimmed;
        }

        private static short ReadSampleAt(byte[] pcm, int sampleIndex)
        {
            int offset = sampleIndex * PcmAudio.BytesPerSample;
            if (offset < 0 || offset + 1 >= pcm.Length)
            {
                return 0;
            }

            return (short)(pcm[offset] | (pcm[offset + 1] << 8));
        }
    }
}
