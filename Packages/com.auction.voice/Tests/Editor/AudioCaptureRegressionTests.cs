using System;
using NUnit.Framework;

namespace AuctionVoice.Tests
{
    /// <summary>
    /// Regression tests for the capture-time conversion bugs found during Unity integration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These all produced the same field symptom: the player held push-to-talk for seconds, the
    /// duration and RMS gates passed, and Vosk still returned an empty transcript, so the result
    /// read <c>kind=UNKNOWN reason=silence</c> with a low confidence. Nothing threw, and no unit
    /// test on the parser could see it, because the damage happened before the audio reached the
    /// recogniser.
    /// </para>
    /// <para>
    /// The root cause was a rate confusion: samples were converted at the <i>device</i> rate into
    /// a buffer sized for <i>16 kHz</i>, then the surviving prefix was resampled as though it were
    /// the whole chunk. On a 48 kHz device that time-compresses speech 3x.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class AudioCaptureRegressionTests
    {
        /// <summary>Builds a ramp so a truncation or a dropped region is detectable by value.</summary>
        private static float[] Ramp(int samples, float from, float to)
        {
            var values = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                values[i] = from + (to - from) * i / Math.Max(1, samples - 1);
            }

            return values;
        }

        [Test]
        public void ConversionAtDeviceRateDoesNotTruncate()
        {
            // 100 ms at 48 kHz is 4800 samples. Converting them needs 9600 bytes. A buffer sized
            // for 16 kHz (3200 bytes) silently keeps only the first 1600 samples.
            const int deviceRate = 48000;
            const int chunkMs = 100;
            int deviceSamples = deviceRate * chunkMs / 1000;

            float[] source = Ramp(deviceSamples, 0f, 0.5f);
            var sizedForDevice = new byte[deviceSamples * 2];
            var sizedFor16k = new byte[16000 * chunkMs / 1000 * 2];

            int writtenDevice = Audio.PcmAudio.FloatToPcm16(source, deviceSamples, sizedForDevice);
            int writtenNarrow = Audio.PcmAudio.FloatToPcm16(source, deviceSamples, sizedFor16k);

            Assert.That(writtenDevice, Is.EqualTo(deviceSamples * 2), "device-sized buffer must hold the whole chunk");
            Assert.That(writtenNarrow, Is.EqualTo(sizedFor16k.Length), "the narrow buffer truncates by design");

            // The truncation is the bug: it silently discards 2/3 of a 48 kHz chunk.
            Assert.That(writtenNarrow, Is.LessThan(writtenDevice));
        }

        [Test]
        public void ResamplingAWholeChunkPreservesDuration()
        {
            // 100 ms in must be 100 ms out, at every common device rate.
            foreach (int deviceRate in new[] { 8000, 16000, 22050, 32000, 44100, 48000, 96000 })
            {
                int deviceSamples = deviceRate * 100 / 1000;
                var pcm = new byte[deviceSamples * 2];
                byte[] resampled = Audio.PcmAudio.Resample(pcm, deviceRate, 16000);

                float outMs = resampled.Length / 2f / 16000f * 1000f;
                Assert.That(outMs, Is.EqualTo(100f).Within(0.5f),
                    $"{deviceRate} Hz: 100 ms became {outMs:0.0} ms");
            }
        }

        [Test]
        public void StreamingResamplerDoesNotDriftAcrossChunks()
        {
            // Ten 100 ms chunks must total 1000 ms. Per-chunk rounding at 44.1 kHz loses a
            // fraction of a sample each time, which accumulates into audible time compression.
            const int deviceRate = 44100;
            int deviceSamples = deviceRate * 100 / 1000;
            var resampler = new Audio.Resampler(deviceRate, 16000);

            int totalBytes = 0;
            var chunk = new byte[deviceSamples * 2];
            for (int i = 0; i < 10; i++)
            {
                totalBytes += resampler.Process(chunk, chunk.Length).Length;
            }

            float outMs = totalBytes / 2f / 16000f * 1000f;
            Assert.That(outMs, Is.EqualTo(1000f).Within(2f),
                $"10 chunks of 100 ms became {outMs:0.0} ms");
        }

        [Test]
        public void StreamingResamplerPreservesSignalShape()
        {
            // A constant signal must stay constant through the resampler; a phase bug shows up
            // immediately as a ramp or as dropped samples.
            const int deviceRate = 48000;
            int deviceSamples = deviceRate / 10; // 100 ms
            var pcm = new byte[deviceSamples * 2];
            for (int i = 0; i < deviceSamples; i++)
            {
                short value = 10000;
                pcm[i * 2] = (byte)(value & 0xFF);
                pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            var resampler = new Audio.Resampler(deviceRate, 16000);
            byte[] output = resampler.Process(pcm, pcm.Length);

            Assert.That(output.Length, Is.EqualTo(1600 * 2));
            for (int i = 0; i < output.Length; i += 2)
            {
                short sample = (short)(output[i] | (output[i + 1] << 8));
                Assert.That(sample, Is.EqualTo(10000), $"sample {i / 2} drifted");
            }
        }

        [Test]
        public void IdentityResamplerReturnsTheSameAudio()
        {
            var resampler = new Audio.Resampler(16000, 16000);
            byte[] input = { 1, 2, 3, 4, 5, 6 };
            byte[] output = resampler.Process(input, input.Length);
            Assert.That(output, Is.EqualTo(input));
        }

        [Test]
        public void PreRollKeepsConvertedAudioNotDeviceRateAudio()
        {
            // The pre-roll is replayed into the recogniser, so it must already be 16 kHz.
            // Storing device-rate bytes there replays 48 kHz audio as if it were 16 kHz, which
            // sounds 3x too slow and decodes as noise.
            var buffer = new Audio.PreRollBuffer(250);
            var resampler = new Audio.Resampler(48000, 16000);

            int deviceSamples = 4800; // 100 ms at 48 kHz
            var devicePcm = new byte[deviceSamples * 2];
            byte[] converted = resampler.Process(devicePcm, devicePcm.Length);
            buffer.Write(converted, converted.Length);

            float storedMs = buffer.Read().Length / 2f / 16000f * 1000f;
            Assert.That(storedMs, Is.EqualTo(100f).Within(1f),
                $"pre-roll held {storedMs:0.0} ms of 16 kHz audio");
        }
    }
}
