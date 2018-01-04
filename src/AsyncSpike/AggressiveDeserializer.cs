using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
// import only a few specific types, to avoid extension method collisions
using Customer = ProtoBuf.Customer;
using Order = ProtoBuf.Order;
using WireType = ProtoBuf.WireType;

namespace AggressiveNamespace // just to calm down some warnings
{
    static class AggressiveDeserializerExtensions
    {
        public static T Deserialize<T>(this IResumableDeserializer<T> serializer, ReadOnlyBuffer buffer, T value = default)
        {
            var ctx = SimpleCache<DeserializationContext>.Get();
            serializer.Deserialize(buffer, ref value, ctx);
            SimpleCache<DeserializationContext>.Recycle(ctx);
            return value;
        }
        static uint ThrowOverflow() => throw new OverflowException();
        static uint ThrowEOF() => throw new EndOfStreamException();
        static bool ThrowNotSupported(WireType wireType) => throw new NotSupportedException(wireType.ToString());


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

        internal static void SkipField(ref this BufferReader reader, WireType wireType)
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


        static async ValueTask<T> DeserializeAsync<T>(this IResumableDeserializer<T> serializer, IPipeReader reader, T value = default, long maxBytes = long.MaxValue, CancellationToken cancellationToken = default)
        {
            var ctx = SimpleCache<DeserializationContext>.Get();
            var rootFrame = ctx.Resume(serializer, value, 0, maxBytes);

            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken);
                if (result.IsCancelled) throw new TaskCanceledException();

                var buffer = result.Buffer;
                ctx.Execute(ref buffer);

                if (result.IsCompleted && buffer.IsEmpty)
                {
                    SimpleCache<DeserializationContext>.Recycle(ctx);
                    return rootFrame.Get<T>();
                }
            }
        }
    }
    // no holds barred, aggressive, no tentative "can I do this" melarky: just do it or die trying
    class AggressiveDeserializer : IResumableDeserializer<Order>, IResumableDeserializer<Customer>
    {
        public static AggressiveDeserializer Instance { get; } = new AggressiveDeserializer();
        private AggressiveDeserializer() { }




        void IResumableDeserializer<Customer>.Deserialize(ReadOnlyBuffer buffer, ref Customer value, DeserializationContext ctx)
        {
            // no inheritance or factory, so can do simple creation
            if (value == null) value = new Customer();

            var reader = new BufferReader(buffer);
            while (true)
            {
                (var fieldNumber, var wireType) = ctx.ReadNextField(ref reader);
                HaveField:
                switch (fieldNumber)
                {
                    case 0:
                        return;
                    case 1:
                        value.Id = reader.ReadInt32(wireType);
                        break;
                    case 2:
                        value.Name = reader.ReadString(wireType);
                        break;
                    case 3:
                        value.Notes = reader.ReadString(wireType);
                        break;
                    case 4:
                        value.MarketValue = reader.ReadDouble(wireType);
                        break;
                    case 5:
                        var orders = value.Orders;
                        while (true)
                        {
                            orders.Add(ctx.DeserializeSubItem<Order>(ref reader, Instance, fieldNumber, wireType));

                            (fieldNumber, wireType) = ctx.ReadNextField(ref reader);
                            if (fieldNumber != 5) goto HaveField;
                        }
                    default:
                        reader.SkipField(wireType);
                        break;
                }
            }
        }

        void IResumableDeserializer<Order>.Deserialize(ReadOnlyBuffer buffer, ref Order value, DeserializationContext ctx)
        {
            // no inheritance or factory, so can do simple creation
            if (value == null) value = new Order();

            var reader = new BufferReader(buffer);
            while (true)
            {
                (var fieldNumber, var wireType) = ctx.ReadNextField(ref reader);

                switch (fieldNumber)
                {
                    case 0:
                        return;
                    case 1:
                        value.Id = reader.ReadInt32(wireType);
                        break;
                    case 2:
                        value.ProductCode = reader.ReadString(wireType);
                        break;
                    case 3:
                        value.Quantity = reader.ReadInt32(wireType);
                        break;
                    case 4:
                        value.UnitPrice = reader.ReadDouble(wireType);
                        break;
                    case 5:
                        value.Notes = reader.ReadString(wireType);
                        break;
                    default:
                        reader.SkipField(wireType);
                        break;
                }
            }
        }
    }
}
