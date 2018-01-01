using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace ProtoBuf
{
    readonly struct DoubleBufferedReadOnlyBuffer
    {
        private readonly ReadOnlyBuffer _buffer;
        private readonly ReadOnlyMemory<byte> _first;
        private readonly int _consumed;
        private readonly long _length;

        public DoubleBufferedReadOnlyBuffer(ReadOnlyBuffer buffer) : this(buffer, buffer.First, 0, buffer.Length) { }
        private DoubleBufferedReadOnlyBuffer(ReadOnlyBuffer buffer, ReadOnlyMemory<byte> first, int consumed, long length)
        {
            _buffer = buffer;
            _first = first;
            _consumed = consumed;
            _length = length;
        }
        public long Length => _length;
        public bool IsEmpty => _length == 0;
        public bool IsSingleSpan => _buffer.IsSingleSpan;
        public ReadOnlyMemory<byte> First => _first;
        static void ThrowArgumentOutOfRange(string paramName) => throw new ArgumentOutOfRangeException(paramName);
        public DoubleBufferedReadOnlyBuffer Slice(long start)
        {
            if (start < 0) ThrowArgumentOutOfRange(nameof(start));
            if (start == 0) return this;

            // if this will cross into a new buffer, we'll use the parent's slice, compensating for anything
            // we've already logically consumed
            if (start >= _first.Length)
            {
                return new DoubleBufferedReadOnlyBuffer(_buffer.Slice(start + _consumed));
            }

            // otherwise we're going to be staying in the same buffer \o/
            return new DoubleBufferedReadOnlyBuffer(_buffer, _first.Slice((int)start), _consumed + (int)start, _length - start);
        }
    }
}
