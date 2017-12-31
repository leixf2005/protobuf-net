using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ProtoBuf
{
    internal sealed class MemoryReader : SyncProtoReader
    {
        public override bool PreferSync { get; }
        private ReadOnlyMemory<byte> _active, _original;
        private readonly bool _useNewTextEncoder;
        internal MemoryReader(ReadOnlyMemory<byte> buffer, bool useNewTextEncoder, bool preferSync = true) : base(buffer.Length)
        {
            _active = _original = buffer;
            _useNewTextEncoder = useNewTextEncoder;
            PreferSync = preferSync;
        }
        protected override void SkipBytes(int bytes)
        {
            _active = _active.Slice(bytes);
            Advance(bytes);
        }
        static MemoryReader()
        {
            if (!BitConverter.IsLittleEndian)
                throw new NotImplementedException("big endian");
        }
        private T ReadLittleEndian<T>() where T : struct
        {
            T val = _active.Span.NonPortableCast<byte, T>()[0];
            _active = _active.Slice(Unsafe.SizeOf<T>());
            Advance(Unsafe.SizeOf<T>());
            return val;
        }

        protected override uint ReadFixedUInt32() => ReadLittleEndian<uint>();

        protected override ulong ReadFixedUInt64() => ReadLittleEndian<ulong>();

        protected override byte[] ReadBytes(int bytes)
        {
            var arr = _active.Slice(0, bytes).ToArray();
            _active = _active.Slice(bytes);
            return arr;
        }
        internal static unsafe string GetUtf8String(ReadOnlyMemory<byte> buffer, int bytes)
        {
            if (MemoryMarshal.TryGetArray(buffer, out var segment))
            {
                return Encoding.GetString(segment.Array, segment.Offset, bytes);
            }

            fixed (byte* pointer = &MemoryMarshal.GetReference(buffer.Span))
            {
                return Encoding.GetString(pointer, bytes);
            }
        }
        protected override string ReadString(int bytes)
        {
            string text;
            if (_useNewTextEncoder)
            {
                //bool result = Encoder.TryDecode(_active.Slice(0, bytes).Span, out text, out int consumed);
                //Debug.Assert(result, "TryDecode failed");
                //Debug.Assert(consumed == bytes, "TryDecode used wrong count");

                throw new NotImplementedException();
            }
            else
            {
                text = GetUtf8String(_active, bytes);
            }
            _active = _active.Slice(bytes);
            Advance(bytes);
            return text;
        }
        public override bool AssertNextField(int fieldNumber)
        {
            var result = PipeReader.TryPeekVarintSingleSpan(_active.Span);
            if (result.consumed != 0 && (result.value >> 3) == fieldNumber)
            {
                SetFieldHeader(result.value);
                _active = _active.Slice(result.consumed);
                Advance(result.consumed);
                return true;
            }
            return false;
        }
        protected override int? TryReadVarintInt32(bool consume)
        {
            var result = PipeReader.TryPeekVarintSingleSpan(_active.Span);
            if (result.consumed == 0)
            {
                return null;
            }
            if (consume)
            {
                _active = _active.Slice(result.consumed);
                Advance(result.consumed);
            }
            return result.value;
        }
        protected override void ApplyDataConstraint()
        {
            if (End != long.MaxValue)
            {
                _active = _original.Slice((int)Position, (int)(End - Position));
            }
        }
        protected override void RemoveDataConstraint()
        {
            _active = _original.Slice((int)Position);
        }
    }
}
