using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ProtoBuf
{
    enum v2Result
    {
        Success,
        SwitchToAsync // for an open-ended sub-object
    }
    interface ISer2<T>
    {
        v2Result Deserialize(ref BufferReader reader, ref T value);
    }

    static class Ser2Extensions
    {
        static uint ThrowOverflow() => throw new OverflowException();
        static bool ThrowNotSupported(WireType wireType) => throw new NotSupportedException(wireType.ToString());

        public static bool TrySkip(ref this BufferReader reader, int length)
        {
            try
            {
                reader.Skip(length);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
        public static bool TrySkipField(ref this BufferReader reader, WireType wireType)
        {
            switch (wireType)
            {
                case WireType.Varint:
                    return reader.TryReadVarint().HasValue;
                case WireType.Fixed64:
                    return reader.TrySkip(8);
                case WireType.String:
                    var len = reader.TryReadVarint();
                    return len != null && reader.TrySkip(checked((int)len.GetValueOrDefault()));
                case WireType.Fixed32:
                    return reader.TrySkip(4);
                default:
                    return ThrowNotSupported(wireType);
            }
        }
        private static uint? TryReadRawUInt32(ref this BufferReader reader)
        {
            uint Fast(ref BufferReader buffer)
            {
                int index = buffer.Index;
                var span = buffer.Span;
                var val = (uint)(span[index++] | (span[index++] << 8) | (span[index++] << 16) | (span[index] << 24));
                buffer.Skip(4);
                return val;
            }

            uint? Slow(ref BufferReader buffer)
            {
                int a = buffer.Take(), b = buffer.Take(), c = buffer.Take(), d = buffer.Take();
                if (a < 0 || b < 0 || c < 0 || d < 0) return null;
                return (uint)(a | (b << 8) | (c << 16) | (d << 24));
            }

            return reader.HasSpan(4) ? Fast(ref reader) : Slow(ref reader);
        }

        private static bool HasSpan(ref this BufferReader reader, int bytes)
           => reader.Span.Length >= reader.Index + bytes;

        private static ulong? TryReadRawUInt64(ref this BufferReader reader)
        {
            ulong Fast(ref BufferReader buffer)
            {
                int index = buffer.Index;
                var span = buffer.Span;
                var lo = (uint)(span[index++] | (span[index++] << 8) | (span[index++] << 16) | (span[index++] << 24));
                var hi = (uint)(span[index++] | (span[index++] << 8) | (span[index++] << 16) | (span[index] << 24));
                buffer.Skip(8);
                return (ulong)lo | ((ulong)hi) << 32;
            }

            ulong? Slow(ref BufferReader buffer)
            {
                int a = buffer.Take(), b = buffer.Take(), c = buffer.Take(), d = buffer.Take(),
                    e = buffer.Take(), f = buffer.Take(), g = buffer.Take(), h = buffer.Take();
                if (a < 0 || b < 0 || c < 0 || d < 0
                    || e < 0 || f < 0 || g < 0 || h < 0) return null;
                var lo = (uint)(a | (b << 8) | (c << 16) | (d << 24));
                var hi = (uint)(e | (f << 8) | (g << 16) | (h << 24));
                return (ulong)lo | ((ulong)hi) << 32;
            }

            return reader.HasSpan(8) ? Fast(ref reader) : Slow(ref reader);
        }
        private static int? CheckedInt32(this ulong? value)
        {
            if (value == null) return null;
            long l = (long)value.GetValueOrDefault();
            return checked((int)l);
        }

        public static int? TryReadInt32(ref this BufferReader reader, WireType wireType)
        {
            switch (wireType)
            {
                case WireType.Varint:
                    return reader.TryReadVarint().CheckedInt32();
                case WireType.Fixed32:
                    return (int?)reader.TryReadRawUInt32();
                case WireType.Fixed64:
                    return reader.TryReadRawUInt64().CheckedInt32();
                default:
                    ThrowNotSupported(wireType);
                    return default;
            }
        }


        private static unsafe float? ToSingle(this uint? value)
        {
            if (value == null) return null;
            var x = value.GetValueOrDefault();
            return *(float*)(&x);
        }

        private static unsafe double? ToDouble(this ulong? value)
        {
            if (value == null) return null;
            var x = value.GetValueOrDefault();
            return *(double*)(&x);
        }

        public static double? TryReadDouble(ref this BufferReader reader, WireType wireType)
        {
            switch (wireType)
            {
                case WireType.Varint:
                    return (long?)reader.TryReadVarint();
                case WireType.Fixed32:
                    return reader.TryReadRawUInt32().ToSingle();
                case WireType.Fixed64:
                    return reader.TryReadRawUInt64().ToDouble();
                default:
                    ThrowNotSupported(wireType);
                    return default;
            }
        }

        static readonly Encoding Encoding = Encoding.UTF8;
        public static string TryReadString(ref this BufferReader reader, WireType wireType)
        {
            unsafe string Fast(ref BufferReader buffer, int bytes)
            {
                string s;
                fixed (byte* ptr = &MemoryMarshal.GetReference(buffer.Span))
                {
                    s = Encoding.GetString(ptr + buffer.Index, bytes);
                }
                buffer.Skip(bytes);
                return s;
            }
            unsafe string Slow(ref BufferReader buffer, int bytes)
            {
                throw new NotImplementedException();
            }

            if (wireType != WireType.String) ThrowNotSupported(wireType);

            var nlen = reader.TryReadVarint().CheckedInt32();
            if (nlen == null) return null;
            int len = nlen.GetValueOrDefault();
            if (len == 0) return "";
            return reader.HasSpan(len) ? Fast(ref reader, len) : Slow(ref reader, len);
        }
        public static ulong? TryReadVarint(ref this BufferReader reader)
        {
            ulong Fast(ref BufferReader buffer)
            {
                var span = buffer.Span;
                ulong val = 0;
                int shift = 0, x;
                int index = buffer.Index;
                for (int i = 0; i < 9; i++)
                {
                    x = span[index++];
                    val |= ((ulong)(x & 127)) << shift;
                    if ((x & 128) == 0)
                    {
                        buffer.Skip(i + 1);
                        return val;
                    }
                    shift += 7;
                }
                x = span[index];
                buffer.Skip(10);
                switch (x)
                {
                    case 0:
                        return val;
                    case 1:
                        return val | (1UL << 63);
                }
                return ThrowOverflow();
            }
            ulong? Slow(ref BufferReader buffer)
            {
                var x = buffer.Take();
                if (x < 0) return null;

                ulong val = (ulong)x;
                if ((val & 128) == 0) return val;

                int shift = 7;
                for (int i = 0; i < 8; i++)
                {
                    x = buffer.Take();
                    if (x < 0) return null;
                    val |= ((ulong)(x & 127)) << shift;
                    if ((x & 128) == 0)
                    {
                        return val;
                    }
                    shift += 7;
                }
                x = buffer.Take();
                switch (buffer.Take())
                {
                    case 0:
                        return val;
                    case 1:
                        return val | (1UL << 63);
                }
                if (x < 0) return null;
                return ThrowOverflow();
            }
            return reader.HasSpan(10) ? Fast(ref reader) : Slow(ref reader);
        }
        private static (v2Result Status, long ConsumedBytes) DeserializeSync<T>(ISer2<T> serializer, ref ReadOnlyBuffer buffer, ref T value)
        {
            var br = new BufferReader(buffer);
            var status = serializer.Deserialize(ref br, ref value);
            buffer = buffer.Slice(br.Cursor);
            return (status, br.ConsumedBytes);

        }
        public static async ValueTask<T> DeserializeAsync<T>(this ISer2<T> serializer, IPipeReader reader, T value = default, long maxBytes = long.MaxValue)
        {
            while (maxBytes != 0)
            {
                var result = await reader.ReadAsync();

                var buffer = result.Buffer;
                if (buffer.IsEmpty && result.IsCompleted)
                    break;

                if (buffer.Length > maxBytes)
                {
                    buffer = buffer.Slice(0, maxBytes);
                }
                var tuple = DeserializeSync<T>(serializer, ref buffer, ref value);
                maxBytes -= tuple.ConsumedBytes;

                // if we need to switch to async, then we know nothing about what we've examined - so advertise Start;
                // otherwise, we've examined to the end

                if (tuple.Status == v2Result.SwitchToAsync)
                {
                    // don't claim anything about what has/hasn't been examined - we're just
                    // changing our approach
                    reader.Advance(buffer.Start, buffer.Start);

                    throw new NotImplementedException();
                }
                else
                {
                    // we've looked at the buffer and decided that we're a little stuck
                    reader.Advance(buffer.Start, buffer.End);
                }
            }
            return value;
        }
    }
}
