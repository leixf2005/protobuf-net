using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ProtoBuf;
using Customer = ProtoBuf.Customer;
using Order = ProtoBuf.Order;
// import only a few specific types, to avoid extension method collisions
using WireType = ProtoBuf.WireType;

namespace AggressiveNamespace // just to calm down some warnings
{
    // no holds barred, aggressive, no tentative "can I do this" melarky: just do it or die trying
    static class AggressiveDeserializer
    {
        static uint ThrowOverflow() => throw new OverflowException();
        static uint ThrowEOF() => throw new EndOfStreamException();
        static bool ThrowNotSupported(WireType wireType) => throw new NotSupportedException(wireType.ToString());

        public static (int FieldNumber, WireType WireType) ReadNextField(ref this BufferReader reader)
        {
            var fieldHeader = reader.ReadVarint();

            return (checked((int)(fieldHeader >> 3)), (WireType)(int)(fieldHeader & 7));
        }
        private static bool HasSpan(ref this BufferReader reader, int bytes)
                => reader.Span.Length >= reader.Index + bytes;
        public static ulong ReadVarint(ref this BufferReader reader)
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
            ulong Slow(ref BufferReader buffer)
            {
                var x = buffer.Take();
                if (x < 0) return ThrowEOF();

                ulong val = (ulong)x;
                if ((val & 128) == 0) return val;

                int shift = 7;
                for (int i = 0; i < 8; i++)
                {
                    x = buffer.Take();
                    if (x < 0) return ThrowEOF();
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
                if (x < 0) return ThrowEOF();
                return ThrowOverflow();
            }
            return reader.HasSpan(10) ? Fast(ref reader) : Slow(ref reader);
        }

        static void SkipField(ref this BufferReader reader, WireType wireType)
        {
            switch (wireType)
            {
                case WireType.Varint:
                    reader.ReadVarint();
                    break;
                case WireType.Fixed64:
                    reader.Skip(8);
                    break;
                case WireType.String:
                    var len = checked((int)reader.ReadVarint());
                    reader.Skip(len);
                    break;
                case WireType.Fixed32:
                    reader.Skip(4);
                    break;
                default:
                    ThrowNotSupported(wireType);
                    break;
            }
        }
        public static int ReadInt32(ref this BufferReader reader, WireType wireType)
        {
            switch (wireType)
            {
                case WireType.Varint:
                    return checked((int)(unchecked((long)reader.ReadVarint())));
                case WireType.Fixed32:
                    return (int)reader.ReadRawUInt32();
                case WireType.Fixed64:
                    return checked((int)(unchecked((long)reader.ReadRawUInt64())));
                default:
                    ThrowNotSupported(wireType);
                    return default;
            }
        }
        private static uint ReadRawUInt32(ref this BufferReader reader)
        {
            uint Fast(ref BufferReader buffer)
            {
                int index = buffer.Index;
                var span = buffer.Span;
                var val = (uint)(span[index++] | (span[index++] << 8) | (span[index++] << 16) | (span[index] << 24));
                buffer.Skip(4);
                return val;
            }

            uint Slow(ref BufferReader buffer)
            {
                int a = buffer.Take(), b = buffer.Take(), c = buffer.Take(), d = buffer.Take();
                if (a < 0 || b < 0 || c < 0 || d < 0) return ThrowEOF();
                return (uint)(a | (b << 8) | (c << 16) | (d << 24));
            }

            return reader.HasSpan(4) ? Fast(ref reader) : Slow(ref reader);
        }
        private static ulong ReadRawUInt64(ref this BufferReader reader)
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

            ulong Slow(ref BufferReader buffer)
            {
                int a = buffer.Take(), b = buffer.Take(), c = buffer.Take(), d = buffer.Take(),
                    e = buffer.Take(), f = buffer.Take(), g = buffer.Take(), h = buffer.Take();
                if (a < 0 || b < 0 || c < 0 || d < 0
                    || e < 0 || f < 0 || g < 0 || h < 0) return ThrowEOF();
                var lo = (uint)(a | (b << 8) | (c << 16) | (d << 24));
                var hi = (uint)(e | (f << 8) | (g << 16) | (h << 24));
                return (ulong)lo | ((ulong)hi) << 32;
            }

            return reader.HasSpan(8) ? Fast(ref reader) : Slow(ref reader);
        }
        public static double ReadDouble(ref this BufferReader reader, WireType wireType)
        {
            switch (wireType)
            {
                case WireType.Varint:
                    return (long)reader.ReadVarint();
                case WireType.Fixed32:
                    return reader.ReadRawUInt32().ToSingle();
                case WireType.Fixed64:
                    return reader.ReadRawUInt64().ToDouble();
                default:
                    ThrowNotSupported(wireType);
                    return default;
            }
        }
        private static unsafe float ToSingle(this uint value) => *(float*)&value;
        private static unsafe double ToDouble(this ulong value) => *(double*)&value;

        static readonly Encoding Encoding = Encoding.UTF8;
        public static string ReadString(ref this BufferReader reader, WireType wireType)
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
                var decoder = Encoding.GetDecoder();
                int bytesLeft = bytes, charCount = 0;

                var c = stackalloc char[bytes]; // good enough for now

                var cPtr = c;
                while (bytesLeft > 0 && !buffer.End)
                {
                    var span = buffer.Span;
                    int bytesThisSpan = Math.Min(bytesLeft, span.Length - buffer.Index);

                    fixed (byte* ptr = &MemoryMarshal.GetReference(buffer.Span))
                    {
                        int charsWritten = decoder.GetChars(ptr + buffer.Index, bytesThisSpan, cPtr, charCount, false);
                        cPtr += charsWritten;
                        charCount += charsWritten;
                    }
                    buffer.Skip(bytesThisSpan); // move to the next span, if one
                    bytesLeft -= bytesThisSpan;
                }

                if (bytesLeft != 0) ThrowEOF();

                return new string(c, 0, charCount);
            }

            if (wireType != WireType.String) ThrowNotSupported(wireType);

            var len = checked((int)reader.ReadVarint());
            if (len == 0) return "";
            return reader.HasSpan(len) ? Fast(ref reader, len) : Slow(ref reader, len);
        }

        internal static Customer DeserializeCustomer(ref ReadOnlyBuffer buffer)
        {
            var reader = new BufferReader(buffer);
            return DeserializeCustomer(ref reader);
        }

        public static Customer DeserializeCustomer(ref BufferReader reader, Customer value = null)
        {
            Customer Create(ref Customer obj) => obj ?? (obj = new Customer());

            while (!reader.End)
            {
                (var fieldNumber, var wireType) = reader.ReadNextField();
HaveField:
                switch (fieldNumber)
                {
                    case 1:
                        Create(ref value).Id = reader.ReadInt32(wireType);
                        break;
                    case 2:
                        Create(ref value).Name = reader.ReadString(wireType);
                        break;
                    case 3:
                        Create(ref value).Notes = reader.ReadString(wireType);
                        break;
                    case 4:
                        Create(ref value).MarketValue = reader.ReadDouble(wireType);
                        break;
                    case 5:
                        var orders = Create(ref value).Orders;
                        while(true)
                        {
                            // we're going to assume length-prefixed here
                            int len = checked((int)reader.ReadVarint());
                            var from = reader.Cursor;
                            reader.Skip(len);
                            var subReader = new BufferReader(new ReadOnlyBuffer(from, reader.Cursor));
                            orders.Add(DeserializeOrder(ref subReader));

                            if (reader.End) break;
                            (fieldNumber, wireType) = reader.ReadNextField();
                            if (fieldNumber != 5) goto HaveField;
                        }
                        break;
                    default:
                        reader.SkipField(wireType);
                        break;
                }
            }
            return Create(ref value);
        }
        public static Order DeserializeOrder(ref BufferReader reader, Order value = null)
        {
            Order Create(ref Order obj) => obj ?? (obj = new Order());

            while (!reader.End)
            {
                (var fieldNumber, var wireType) = reader.ReadNextField();

                switch (fieldNumber)
                {
                    case 1:
                        Create(ref value).Id = reader.ReadInt32(wireType);
                        break;
                    case 2:
                        Create(ref value).ProductCode = reader.ReadString(wireType);
                        break;
                    case 3:
                        Create(ref value).Quantity = reader.ReadInt32(wireType);
                        break;
                    case 4:
                        Create(ref value).UnitPrice = reader.ReadDouble(wireType);
                        break;
                    case 5:
                        Create(ref value).Notes = reader.ReadString(wireType);
                        break;
                    default:
                        reader.SkipField(wireType);
                        break;
                }
            }
            return Create(ref value);
        }
    }
}
