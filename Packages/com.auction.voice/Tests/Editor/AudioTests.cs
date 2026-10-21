using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AuctionVoice.Core;
using NUnit.Framework;

namespace AuctionVoice.Tests
{
    /// <summary>Tests for the UnityEngine-free PCM helpers.</summary>
    [TestFixture]
    public sealed class PcmAudioTests
    {
        [Test]
        public void SilenceHasTheRequestedDuration()
        {
            byte[] silence = Audio.PcmAudio.Silence(100);
            Assert.That(silence.Length, Is.EqualTo(3200));
            Assert.That(Audio.PcmAudio.DurationMs(silence.Length), Is.EqualTo(100f).Within(0.01f));
            Assert.That(Audio.PcmAudio.Rms(silence), Is.EqualTo(0f));
        }

        [Test]
        public void RmsOfFullScaleSquareWaveIsOne()
        {
            byte[] pcm = new byte[320];
            for (int i = 0; i < pcm.Length; i += 2)
            {
                // 32767 = 0x7FFF, little-endian.
                pcm[i] = 0xFF;
                pcm[i + 1] = 0x7F;
            }

            Assert.That(Audio.PcmAudio.Rms(pcm), Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void SilentAudioIsDetected()
        {
            Assert.That(Audio.PcmAudio.IsSilent(Audio.PcmAudio.Silence(500)), Is.True);
        }

        [Test]
        public void ShortLoudBurstInsideSilenceIsNotSilent()
        {
            // 1 s of silence with 200 ms of full-scale audio in the middle: a global RMS would
            // be ~0.45, but the sliding window must clear the threshold regardless of position.
            byte[] pcm = new byte[32000];
            for (int i = 16000; i < 22400; i += 2)
            {
                pcm[i] = 0xFF;
                pcm[i + 1] = 0x7F;
            }

            Assert.That(Audio.PcmAudio.IsSilent(pcm), Is.False);
        }

        [Test]
        public void FloatConversionClampsAndIsLittleEndian()
        {
            var source = new[] { 1.5f, -1.5f, 0.5f };
            var destination = new byte[6];
            int written = Audio.PcmAudio.FloatToPcm16(source, 3, destination);

            Assert.That(written, Is.EqualTo(6));
            Assert.That(BitConverter.ToInt16(destination, 0), Is.EqualTo(32767));
            Assert.That(BitConverter.ToInt16(destination, 2), Is.EqualTo(-32767));
            Assert.That(BitConverter.ToInt16(destination, 4), Is.EqualTo(16383));
        }

        [Test]
        public void ResamplingHalvesTheSampleCount()
        {
            byte[] pcm = Audio.PcmAudio.Silence(100, 32000);
            byte[] resampled = Audio.PcmAudio.Resample(pcm, 32000, 16000);
            Assert.That(resampled.Length, Is.EqualTo(pcm.Length / 2));
        }

        [Test]
        public void ResamplingAtTheSameRateIsANoOp()
        {
            byte[] pcm = Audio.PcmAudio.Silence(50);
            Assert.That(Audio.PcmAudio.Resample(pcm, 16000, 16000), Is.SameAs(pcm));
        }
    }

    /// <summary>Tests for the first-syllable pre-roll ring buffer.</summary>
    [TestFixture]
    public sealed class PreRollBufferTests
    {
        [Test]
        public void ReturnsOnlyTheMostRecentAudio()
        {
            var buffer = new Audio.PreRollBuffer(100);
            Assert.That(buffer.Capacity, Is.EqualTo(3200));

            var first = new byte[1600];
            var second = new byte[1600];
            for (int i = 0; i < first.Length; i++)
            {
                first[i] = 1;
                second[i] = 2;
            }

            buffer.Write(first, first.Length);
            buffer.Write(second, second.Length);

            byte[] all = buffer.Read();
            Assert.That(all.Length, Is.EqualTo(3200));
            Assert.That(all[0], Is.EqualTo(1));
            Assert.That(all[3199], Is.EqualTo(2));
        }

        [Test]
        public void KeepsTheNewestAudioWhenOverflowing()
        {
            var buffer = new Audio.PreRollBuffer(10); // 320 bytes
            var old = new byte[320];
            var fresh = new byte[320];
            for (int i = 0; i < old.Length; i++)
            {
                old[i] = 9;
                fresh[i] = 7;
            }

            buffer.Write(old, old.Length);
            buffer.Write(fresh, fresh.Length);

            byte[] all = buffer.Read();
            Assert.That(all.Length, Is.EqualTo(320));
            Assert.That(all[0], Is.EqualTo(7));
            Assert.That(all[319], Is.EqualTo(7));
        }

        [Test]
        public void OversizedWriteKeepsTheTail()
        {
            var buffer = new Audio.PreRollBuffer(10); // 320 bytes
            var big = new byte[640];
            for (int i = 0; i < big.Length; i++)
            {
                big[i] = (byte)(i < 320 ? 1 : 2);
            }

            buffer.Write(big, big.Length);
            byte[] all = buffer.Read();
            Assert.That(all.Length, Is.EqualTo(320));
            Assert.That(all[0], Is.EqualTo(2));
        }

        [Test]
        public void ClearEmptiesTheBuffer()
        {
            var buffer = new Audio.PreRollBuffer(50);
            byte[] chunk = Audio.PcmAudio.Silence(50);
            buffer.Write(chunk, chunk.Length);
            buffer.Clear();
            Assert.That(buffer.Read(), Is.Empty);
        }

        [Test]
        public void ReadMillisecondsTrimsToTheTail()
        {
            var buffer = new Audio.PreRollBuffer(200);
            var chunk = new byte[6400];
            for (int i = 0; i < chunk.Length; i++)
            {
                chunk[i] = (byte)(i < 3200 ? 1 : 2);
            }

            buffer.Write(chunk, chunk.Length);
            byte[] tail = buffer.Read(100);
            Assert.That(tail.Length, Is.EqualTo(3200));
            Assert.That(tail[0], Is.EqualTo(2));
        }
    }

    /// <summary>Tests for the RMS energy endpoint detector.</summary>
    [TestFixture]
    public sealed class EnergyEndpointDetectorTests
    {
        private static byte[] Loud(int milliseconds)
        {
            byte[] pcm = new byte[16000 * milliseconds / 1000 * 2];
            for (int i = 0; i < pcm.Length; i += 2)
            {
                pcm[i] = 0xFF;
                pcm[i + 1] = 0x7F;
            }

            return pcm;
        }

        [Test]
        public void DoesNotFinalizeDuringSpeech()
        {
            var detector = new Audio.EnergyEndpointDetector();
            byte[] loud = Loud(100);
            Assert.That(detector.ShouldFinalize(loud, loud.Length), Is.False);
            Assert.That(detector.SpeechDetected, Is.True);
        }

        [Test]
        public void FinalizesAfterSustainedSilenceFollowingSpeech()
        {
            var detector = new Audio.EnergyEndpointDetector();
            byte[] loud = Loud(100);
            detector.ShouldFinalize(loud, loud.Length);

            byte[] silence = Audio.PcmAudio.Silence(100);
            bool finalized = false;
            for (int i = 0; i < 10 && !finalized; i++)
            {
                finalized = detector.ShouldFinalize(silence, silence.Length);
            }

            Assert.That(finalized, Is.True);
        }

        [Test]
        public void NeverFinalizesBeforeSpeechStarted()
        {
            var detector = new Audio.EnergyEndpointDetector();
            byte[] silence = Audio.PcmAudio.Silence(100);
            for (int i = 0; i < 20; i++)
            {
                Assert.That(detector.ShouldFinalize(silence, silence.Length), Is.False);
            }
        }

        [Test]
        public void ResetClearsSpeechState()
        {
            var detector = new Audio.EnergyEndpointDetector();
            byte[] loud = Loud(100);
            detector.ShouldFinalize(loud, loud.Length);
            detector.Reset();
            Assert.That(detector.SpeechDetected, Is.False);
        }
    }
}
