using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ProtoBuf
{
    internal sealed class MemoryReader : AsyncProtoReader
    {
        private ReadOnlyMemory<byte> _active, _original;
        private readonly bool _useNewTextEncoder;
        internal MemoryReader(ReadOnlyMemory<byte> buffer, bool useNewTextEncoder) : base(buffer.Length)
        {
            _active = _original = buffer;
            _useNewTextEncoder = useNewTextEncoder;
        }
        protected override Task SkipBytesAsync(int bytes)
        {
            _active = _active.Slice(bytes);
            Advance(bytes);
            return Task.CompletedTask;
        }
        static MemoryReader()
        {
            if (!BitConverter.IsLittleEndian)
                throw new NotImplementedException("big endian");
        }
        private ValueTask<T> ReadLittleEndian<T>() where T : struct
        {
            T val = _active.Span.NonPortableCast<byte, T>()[0];
            _active = _active.Slice(Unsafe.SizeOf<T>());
            Advance(Unsafe.SizeOf<T>());
            return AsTask(val);
        }

        protected override ValueTask<uint> ReadFixedUInt32Async()
            => ReadLittleEndian<uint>();

        protected override ValueTask<ulong> ReadFixedUInt64Async()
            => ReadLittleEndian<ulong>();

        protected override ValueTask<byte[]> ReadBytesAsync(int bytes)
        {
            var arr = _active.Slice(0, bytes).ToArray();
            _active = _active.Slice(bytes);
            return AsTask(arr);
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
        protected override ValueTask<string> ReadStringAsync(int bytes)
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
            return AsTask(text);
        }
        public override Task<bool> AssertNextField(int fieldNumber)
        {
            var result = PipeReader.TryPeekVarintSingleSpan(_active.Span);
            if (result.consumed != 0 && (result.value >> 3) == fieldNumber)
            {
                SetFieldHeader(result.value);
                _active = _active.Slice(result.consumed);
                Advance(result.consumed);
                return True;
            }
            return False;
        }
        protected override ValueTask<int?> TryReadVarintInt32Async(bool consume)
        {
            var result = PipeReader.TryPeekVarintSingleSpan(_active.Span);
            if (result.consumed == 0)
            {
                return new ValueTask<int?>((int?)null);
            }
            if (consume)
            {
                _active = _active.Slice(result.consumed);
                Advance(result.consumed);
            }
            return AsTask<int?>(result.value);
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
