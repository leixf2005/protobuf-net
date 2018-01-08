//using System;
//using System.Buffers;
//using System.Collections.Generic;
//using System.Collections.Sequences;
//using System.Text;

//namespace ProtoBuf
//{
//    readonly struct DoubleBufferedReadableBuffer
//    {
//        public struct Enumerator
//        {
//            private ReadableBuffer.Enumerator _wrapped;
//            private bool _isFirst;
//            private readonly ReadOnlyMemory<byte> _first;
//            public Enumerator(ReadOnlyMemory<byte> first, ReadableBuffer.Enumerator enumerator) : this()
//            {
//                _first = first;
//                _wrapped = enumerator;
//                _isFirst = true;
//            }

//            public void Reset()
//            {
//                _wrapped.Reset();
//                Current = default;
//                _isFirst = true;
//            }

//            public ReadOnlyMemory<byte> Current { get; private set; }
            
//            public bool MoveNext()
//            {
//                if (!_wrapped.MoveNext())
//                {
//                    Current = default;
//                    return false;
//                }
//                if(_isFirst)
//                {
//                    Current = _first;
//                    _isFirst = false;
//                }
//                else
//                {
//                    Current = _wrapped.Current;
//                }
//                return true;
//            }
//        }

//        public Enumerator GetEnumerator() => new Enumerator(_first, _buffer.GetEnumerator());

//        private readonly ReadOnlyBuffer _buffer;
//        private readonly ReadOnlyMemory<byte> _first;
//        private readonly int _consumed;
//        private readonly long _length;

//        public Position End => _buffer.End;
//        public Position Start
//        {
//            get
//            {
//                var start = _buffer.Start;
//                return new Position(start.Segment, start.Index + _consumed);
//            }
//        }
//        public DoubleBufferedReadableBuffer(ReadableBuffer buffer) : this(buffer, buffer.First, 0, buffer.Length) { }
//        private DoubleBufferedReadableBuffer(ReadableBuffer buffer, ReadOnlyMemory<byte> first, int consumed, long length)
//        {
//            _buffer = buffer;
//            _first = first;
//            _consumed = consumed;
//            _length = length;
//        }

//        internal byte[] ToArray(int bytes)
//        {
//            if (bytes < 0 || bytes > _length) throw new ArgumentOutOfRangeException(nameof(bytes));
//            if (bytes == 0) return Array.Empty<byte>();
//            if (bytes <= _first.Length) return _first.Slice(0, bytes).ToArray();

//            var arr = new byte[bytes];
//            Span<byte> span = arr;
//            var iter = _buffer.GetEnumerator();
//            if (!iter.MoveNext()) throw new ArgumentOutOfRangeException(nameof(bytes));

//            _first.Span.TryCopyTo(span);
//            span = span.Slice(_first.Length);
//            bytes -= _first.Length;

//            while(bytes != 0 && iter.MoveNext())
//            {
//                var current = iter.Current.Span;
//                if(current.Length > bytes)
//                {
//                    current = current.Slice(0, bytes);
//                }
//                current.TryCopyTo(span);
//                span = span.Slice(current.Length);
//                bytes -= current.Length;
//            }

//            return arr;
//        }

//        public long Length => _length;
//        public bool IsEmpty => _length == 0;
//        public bool IsSingleSpan => _buffer.IsSingleSpan;


//        public ReadOnlyMemory<byte> First => _first;
//        static void ThrowArgumentOutOfRange(string paramName) => throw new ArgumentOutOfRangeException(paramName);

//        internal DoubleBufferedReadableBuffer Slice(Position start)
//        {
//            var relativeStart = Start;
//            if (relativeStart.Segment != start.Segment) return new DoubleBufferedReadableBuffer(_buffer.Slice(start));

//            return Slice(start.Index - relativeStart.Index);
//        }
//        internal DoubleBufferedReadableBuffer Slice(int start, int length)
//            => new DoubleBufferedReadableBuffer(_buffer.Slice(start + _consumed, length));

//        public DoubleBufferedReadableBuffer Slice(int start)
//        {
//            if (start < 0) ThrowArgumentOutOfRange(nameof(start));
//            if (start == 0) return this;

//            // if this will cross into a new buffer, we'll use the parent's slice, compensating for anything
//            // we've already logically consumed
//            if (start >= _first.Length)
//            {
//                return new DoubleBufferedReadableBuffer(_buffer.Slice(start + _consumed));
//            }

//            // otherwise we're going to be staying in the same buffer \o/
//            return new DoubleBufferedReadableBuffer(_buffer, _first.Slice((int)start), _consumed + (int)start, _length - start);
//        }
//    }
//}
