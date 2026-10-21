using System;

namespace AuctionVoice.Audio
{
    /// <summary>
    /// Reassembles arbitrary-sized reads of float samples into fixed-size chunks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Capture hands out whatever the device has produced since the last tick, which is almost
    /// never a whole number of chunks. This class accumulates those reads and releases complete
    /// chunks, keeping the remainder for the next call.
    /// </para>
    /// <para>
    /// It exists as a separate, UnityEngine-free type because the equivalent inline logic in
    /// <see cref="UnityMicrophoneCapture"/> produced three separate bugs that no test could reach:
    /// a remainder plus a full read overflowing the buffer, the remainder being dropped on
    /// release, and an off-by-one in the wrap handling. The invariant is simple and now enforced
    /// here: <see cref="Count"/> never exceeds <see cref="ChunkSamples"/>, and <see cref="Append"/>
    /// never writes past the buffer.
    /// </para>
    /// </remarks>
    public sealed class ChunkAssembler
    {
        private readonly float[] _buffer;
        private int _count;

        /// <summary>Creates an assembler for chunks of <paramref name="chunkSamples"/> samples.</summary>
        public ChunkAssembler(int chunkSamples)
        {
            if (chunkSamples <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSamples));
            }

            _buffer = new float[chunkSamples];
        }

        /// <summary>Samples per emitted chunk.</summary>
        public int ChunkSamples => _buffer.Length;

        /// <summary>Samples currently buffered (always <c>&lt;= ChunkSamples</c>).</summary>
        public int Count => _count;

        /// <summary>Free space before the buffer is full.</summary>
        public int Room => _buffer.Length - _count;

        /// <summary>True when a whole chunk is ready to emit.</summary>
        public bool IsFull => _count == _buffer.Length;

        /// <summary>
        /// The internal buffer. Only the first <see cref="Count"/> samples are meaningful.
        /// </summary>
        public float[] Buffer => _buffer;

        /// <summary>
        /// Appends samples, never writing past the buffer.
        /// </summary>
        /// <param name="source">Samples to append.</param>
        /// <param name="count">How many to append.</param>
        /// <returns>The number actually appended, which is at most <see cref="Room"/>.</returns>
        /// <remarks>
        /// Returning the accepted count rather than throwing is deliberate: the caller reads from
        /// a device ring buffer and should ask for exactly <see cref="Room"/> samples, but a
        /// defensive clamp here means a miscalculation degrades to "chunk emitted slightly later"
        /// instead of an <see cref="ArgumentException"/> on the main thread.
        /// </remarks>
        public int Append(float[] source, int count)
        {
            if (source == null || count <= 0 || _count >= _buffer.Length)
            {
                return 0;
            }

            int take = Math.Min(Math.Min(count, source.Length), Room);
            Array.Copy(source, 0, _buffer, _count, take);
            _count += take;
            return take;
        }

        /// <summary>
        /// Fills the remaining space with silence so the buffer becomes a full chunk.
        /// </summary>
        /// <remarks>
        /// Used when an utterance ends mid-chunk: the recogniser still receives a complete
        /// window, and the buffered speech is not lost.
        /// </remarks>
        public void PadWithSilence()
        {
            if (_count < _buffer.Length)
            {
                Array.Clear(_buffer, _count, _buffer.Length - _count);
                _count = _buffer.Length;
            }
        }

        /// <summary>Empties the buffer.</summary>
        public void Reset()
        {
            _count = 0;
        }
    }
}
