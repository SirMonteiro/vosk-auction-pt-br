using System;
using NUnit.Framework;

namespace AuctionVoice.Tests
{
    /// <summary>
    /// Tests for the chunk assembler that turns arbitrary device reads into fixed chunks.
    /// </summary>
    /// <remarks>
    /// The inline version of this logic in <c>UnityMicrophoneCapture</c> threw
    /// <c>ArgumentException: Destination array was not long enough</c> on the Unity main thread
    /// whenever a buffered remainder met a full read. Extracting it made the invariant testable:
    /// <see cref="Audio.ChunkAssembler.Count"/> never exceeds the chunk size, and
    /// <see cref="Audio.ChunkAssembler.Append"/> never writes past the buffer.
    /// </remarks>
    [TestFixture]
    public sealed class ChunkAssemblerTests
    {
        private static float[] Fill(int count, float value)
        {
            var buffer = new float[count];
            for (int i = 0; i < count; i++)
            {
                buffer[i] = value;
            }

            return buffer;
        }

        [Test]
        public void BuffersUntilAFullChunkIsAvailable()
        {
            var assembler = new Audio.ChunkAssembler(100);
            Assert.That(assembler.Append(Fill(40, 1f), 40), Is.EqualTo(40));
            Assert.That(assembler.IsFull, Is.False);
            Assert.That(assembler.Count, Is.EqualTo(40));

            Assert.That(assembler.Append(Fill(60, 2f), 60), Is.EqualTo(60));
            Assert.That(assembler.IsFull, Is.True);
            Assert.That(assembler.Count, Is.EqualTo(100));
        }

        /// <summary>
        /// The exact crash: a partial remainder followed by a full-size read. The old inline code
        /// copied <c>count</c> samples to offset <c>_leftoverCount</c>, overflowing the buffer.
        /// </summary>
        [Test]
        public void PartialRemainderPlusFullReadDoesNotOverflow()
        {
            var assembler = new Audio.ChunkAssembler(4800);

            // A remainder from the previous tick...
            Assert.That(assembler.Append(Fill(100, 1f), 100), Is.EqualTo(100));

            // ...then a full chunk arrives. Only the room that is left may be accepted.
            Assert.That(assembler.Room, Is.EqualTo(4700));
            int accepted = assembler.Append(Fill(4800, 2f), 4800);

            Assert.That(accepted, Is.EqualTo(4700), "must clamp to the remaining room");
            Assert.That(assembler.Count, Is.EqualTo(4800), "buffer must be exactly full, never over");
            Assert.That(assembler.IsFull, Is.True);
        }

        [Test]
        public void NeverWritesPastTheBufferForAnyReadSize()
        {
            var assembler = new Audio.ChunkAssembler(100);
            var random = new Random(20261021);

            for (int iteration = 0; iteration < 500; iteration++)
            {
                int request = random.Next(1, 250);
                int accepted = assembler.Append(Fill(request, 3f), request);

                Assert.That(accepted, Is.LessThanOrEqualTo(assembler.ChunkSamples));
                Assert.That(assembler.Count, Is.LessThanOrEqualTo(assembler.ChunkSamples),
                    $"count overflowed after a {request}-sample read");
                Assert.That(accepted, Is.LessThanOrEqualTo(request));

                if (assembler.IsFull)
                {
                    assembler.Reset();
                }
            }
        }

        [Test]
        public void PreservesSampleOrderAcrossAppends()
        {
            var assembler = new Audio.ChunkAssembler(10);
            assembler.Append(Fill(3, 1f), 3);
            assembler.Append(Fill(4, 2f), 4);
            assembler.Append(Fill(3, 3f), 3);

            float[] buffer = assembler.Buffer;
            Assert.That(buffer[0], Is.EqualTo(1f));
            Assert.That(buffer[2], Is.EqualTo(1f));
            Assert.That(buffer[3], Is.EqualTo(2f));
            Assert.That(buffer[6], Is.EqualTo(2f));
            Assert.That(buffer[7], Is.EqualTo(3f));
            Assert.That(buffer[9], Is.EqualTo(3f));
        }

        [Test]
        public void PadWithSilenceCompletesTheChunk()
        {
            var assembler = new Audio.ChunkAssembler(10);
            assembler.Append(Fill(4, 5f), 4);
            assembler.PadWithSilence();

            Assert.That(assembler.IsFull, Is.True);
            Assert.That(assembler.Buffer[3], Is.EqualTo(5f), "buffered speech must survive");
            Assert.That(assembler.Buffer[4], Is.EqualTo(0f), "the rest must be silence");
            Assert.That(assembler.Buffer[9], Is.EqualTo(0f));
        }

        [Test]
        public void ResetEmptiesTheBuffer()
        {
            var assembler = new Audio.ChunkAssembler(10);
            assembler.Append(Fill(10, 1f), 10);
            assembler.Reset();
            Assert.That(assembler.Count, Is.EqualTo(0));
            Assert.That(assembler.Room, Is.EqualTo(10));
        }

        [Test]
        public void AppendRejectsNullAndNonPositiveCounts()
        {
            var assembler = new Audio.ChunkAssembler(10);
            Assert.That(assembler.Append(null, 5), Is.EqualTo(0));
            Assert.That(assembler.Append(Fill(5, 1f), 0), Is.EqualTo(0));
            Assert.That(assembler.Append(Fill(5, 1f), -3), Is.EqualTo(0));
            Assert.That(assembler.Count, Is.EqualTo(0));
        }

        [Test]
        public void AppendClampsToTheSourceLength()
        {
            var assembler = new Audio.ChunkAssembler(100);
            Assert.That(assembler.Append(Fill(5, 1f), 50), Is.EqualTo(5),
                "cannot read more samples than the source holds");
        }

        [Test]
        public void RejectsANonPositiveChunkSize()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Audio.ChunkAssembler(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Audio.ChunkAssembler(-1));
        }
    }
}
