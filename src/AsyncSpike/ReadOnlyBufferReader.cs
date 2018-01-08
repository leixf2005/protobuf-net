//using System;
//using System.Buffers;
//using System.Collections.Generic;
//using System.Runtime.CompilerServices;
//using System.Text;
//using System.Threading;

//namespace ProtoBuf
//{
//    class ReadableReadableBufferReader : SyncProtoReader
//    {
//        private DoubleBufferedReadableBuffer _buffer, _original;
//        private static ReadableReadableBufferReader _last;
//        private ReadableBufferReader() {}
//        internal static SyncProtoReader Create(ReadableBuffer buffer, long position = 0)
//            => Create(new DoubleBufferedReadableBuffer(buffer), position);
//        internal static SyncProtoReader Create(DoubleBufferedReadableBuffer buffer, long position = 0)
//        {
//            if (buffer.Length == 0) return Null;
//            if (buffer.IsSingleSpan)
//            {
//                return MemoryReader.Create(buffer.First, false, position: position);
//            }
//            var obj = Interlocked.Exchange(ref _last, null) ?? new ReadableBufferReader();
//            obj.Reset(position, buffer.Length);
//            obj._buffer = obj._original = buffer;
//            return obj;
//        }
//        public override void Dispose()
//        {
//            _original = default;
//            _buffer = default;
//            Interlocked.CompareExchange(ref _last, this, null);
//        }

//        protected override string ReadString(int bytes)
//        {
//            string s = PipeReader.ReadString(ref _buffer, bytes);
//            Advance(bytes);
//            return s;
//        }
//        protected override uint ReadFixedUInt32() => ReadLittleEndian<uint>();
//        protected override ulong ReadFixedUInt64() => ReadLittleEndian<ulong>();
//        private T ReadLittleEndian<T>() where T : struct
//        {
//            T val = PipeReader.ReadLittleEndian<T>(ref _buffer);
//            Advance(Unsafe.SizeOf<T>());
//            return val;
//        }
//        protected override void SkipBytes(int bytes)
//        {
//            _buffer = _buffer.Slice(bytes);
//            Advance(bytes);
//        }
//        protected override byte[] ReadBytes(int bytes)
//        {
//            var arr = _buffer.ToArray(bytes);
//            _buffer = _buffer.Slice(bytes);
//            Advance(bytes);
//            return arr;
//        }
//        protected override int? TryReadVarintInt32(bool consume = true)
//        {
//            var read = PipeReader.TryPeekVarintInt32(ref _buffer);

//            if (read.consumed != 0)
//            {
//                if (consume)
//                {
//                    _buffer = _buffer.Slice(read.consumed);
//                    Advance(read.consumed);
//                }
//                return read.value;
//            }
//            if (_buffer.Length == 0) return null;
//            return ThrowEOF<int?>();
//        }

//        protected override void RemoveDataConstraint()
//        {
//            if (_buffer.End != _original.End)
//            {
//                var wasForConsoleMessage = _buffer.Length;
//                // change back to the original right hand boundary
//                _buffer = _original.Slice(_buffer.Start);
//                Trace($"Data constraint removed; {_buffer.Length} bytes available (was {wasForConsoleMessage})");
//            }
//        }
//        protected override void ApplyDataConstraint()
//        {
//            if (End != long.MaxValue && checked(Position + _buffer.Length) > End)
//            {
//                int allow = checked((int)(End - Position));
//                var wasForConsoleMessage = _buffer.Length;
//                _buffer = _buffer.Slice(0, allow);
//                Trace($"Data constraint imposed; {_buffer.Length} bytes available (was {wasForConsoleMessage})");
//            }
//        }
//    }
//}
