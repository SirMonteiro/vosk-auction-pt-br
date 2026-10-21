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
        /// Adequate for speech at these rates and free of a DSP dependency, matching the
        /// reference implementation's <c>_resample</c>.
        /// </remarks>
        public static byte[] Resample(byte[] pcm, int sourceRate, int targetRate)
        {
            if (pcm == null || pcm.Length == 0 || sourceRate == targetRate)
            {
                return pcm;
            }

            int sourceSamples = pcm.Length / BytesPerSample;
            double duration = sourceSamples / (double)sourceRate;
            int targetSamples = (int)Math.Round(duration * targetRate);
            var result = new byte[targetSamples * BytesPerSample];

            for (int i = 0; i < targetSamples; i++)
            {
                double sourcePosition = i * (double)sourceRate / targetRate;
                int index = (int)sourcePosition;
                double fraction = sourcePosition - index;
                float a = ReadSample(pcm, index * BytesPerSample);
                float b = index + 1 < sourceSamples
                    ? ReadSample(pcm, (index + 1) * BytesPerSample)
                    : a;
                short sample = (short)(a + (b - a) * fraction);
                result[i * 2] = (byte)(sample & 0xFF);
                result[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            return result;
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
}
