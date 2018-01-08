//using System;
//using System.Runtime.CompilerServices;
//using System.Runtime.InteropServices;
//using System.Threading;
//using System.Threading.Tasks;

//namespace ProtoBuf
//{
//    internal sealed class MemoryReader : SyncProtoReader
//    {

//        public override bool PreferSync => _preferSync;
//        private ReadOnlyMemory<byte> _active, _original;
//        private bool _useNewTextEncoder, _preferSync;

//        private static MemoryReader _last;
//        private MemoryReader() { }
//        internal static SyncProtoReader Create(ReadOnlyMemory<byte> buffer, bool useNewTextEncoder, bool preferSync = true, long position = 0)
//        {
//            if (buffer.Length == 0) return Null;
//            var obj = Interlocked.Exchange(ref _last, null) ?? new MemoryReader();
//            obj.Reset(position, buffer.Length);
//            obj._active = obj._original = buffer;
//            obj._useNewTextEncoder = useNewTextEncoder;
//            obj._preferSync = preferSync;
//            return obj;
//        }
//        //internal override T ReadSubMessage<T>(ISyncSerializer<T> serializer, T value = default)
//        //{
//        //    var pair = BeginSubObject();
//        //    SkipBytes(pair.Length);
//        //    return value;
//        //}
//        //internal override ValueTask<T> ReadSubMessageAsync<T>(IAsyncSerializer<T> serializer, T value = default)
//        //{
//        //    var pair = BeginSubObject();
//        //    SkipBytes(pair.Length);
//        //    return AsTask(value);
//        //}
//        public override void Dispose()
//        {
//            _original = _active = default;
//            Interlocked.CompareExchange(ref _last, this, null);
//        }

//        protected override void SkipBytes(int bytes)
//        {
//            _active = _active.Slice(bytes);
//            Advance(bytes);
//        }
//        static MemoryReader()
//        {
//            if (!BitConverter.IsLittleEndian)
//                throw new NotImplementedException("big endian");
//        }
//        private T ReadLittleEndian<T>() where T : struct
//        {
//            T val = _active.Span.NonPortableCast<byte, T>()[0];
//            _active = _active.Slice(Unsafe.SizeOf<T>());
//            Advance(Unsafe.SizeOf<T>());
//            return val;
//        }

//        protected override uint ReadFixedUInt32() => ReadLittleEndian<uint>();

//        protected override ulong ReadFixedUInt64() => ReadLittleEndian<ulong>();

//        protected override byte[] ReadBytes(int bytes)
//        {
//            var arr = _active.Slice(0, bytes).ToArray();
//            _active = _active.Slice(bytes);
//            return arr;
//        }
//        internal static unsafe string GetUtf8String(ReadOnlyMemory<byte> buffer, int bytes)
//        {
//            if (MemoryMarshal.TryGetArray(buffer, out var segment))
//            {
//                return Encoding.GetString(segment.Array, segment.Offset, bytes);
//            }

//            fixed (byte* pointer = &MemoryMarshal.GetReference(buffer.Span))
//            {
//                return Encoding.GetString(pointer, bytes);
//            }
//        }
//        protected override string ReadString(int bytes)
//        {
//            string text;
//            if (_useNewTextEncoder)
//            {
//                //bool result = Encoder.TryDecode(_active.Slice(0, bytes).Span, out text, out int consumed);
//                //Debug.Assert(result, "TryDecode failed");
//                //Debug.Assert(consumed == bytes, "TryDecode used wrong count");

//                throw new NotImplementedException();
//            }
//            else
//            {
//                text = GetUtf8String(_active, bytes);
//            }
//            _active = _active.Slice(bytes);
//            Advance(bytes);
//            return text;
//        }
//        public override bool AssertNextField(int fieldNumber)
//        {
//            var result = PipeReader.TryPeekVarintSingleSpan(_active.Span);
//            if (result.consumed != 0 && (result.value >> 3) == fieldNumber)
//            {
//                SetFieldHeader(result.value);
//                _active = _active.Slice(result.consumed);
//                Advance(result.consumed);
//                return true;
//            }
//            return false;
//        }
//        protected override int? TryReadVarintInt32(bool consume)
//        {
//            var result = PipeReader.TryPeekVarintSingleSpan(_active.Span);
//            if (result.consumed == 0)
//            {
//                return null;
//            }
//            if (consume)
//            {
//                _active = _active.Slice(result.consumed);
//                Advance(result.consumed);
//            }
//            return result.value;
//        }
//        protected override void ApplyDataConstraint()
//        {
//            if (End != long.MaxValue)
//            {
//                _active = _original.Slice((int)Position, (int)(End - Position));
//            }
//        }
//        protected override void RemoveDataConstraint()
//        {
//            _active = _original.Slice((int)Position);
//        }
//    }
//}
