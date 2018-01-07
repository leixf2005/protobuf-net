using System;
using System.Buffers;
using System.Diagnostics;
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

    static class Verbose
    {
        [Conditional("VERBOSE")]
        public static void WriteLine(string message) => Console.WriteLine(message);
    }
    static class AggressiveDeserializerExtensions
    {
        public static T Deserialize<T>(this IResumableDeserializer<T> serializer, ReadOnlyBuffer buffer, T value = default)
        {
            var ctx = SimpleCache<DeserializationContext>.Get();
            ctx.ResetStart(0, buffer.Start);
            serializer.Deserialize(buffer, ref value, ctx);
            SimpleCache<DeserializationContext>.Recycle(ctx);
            return value;
        }
        static uint ThrowOverflow() => throw new OverflowException();
        static uint ThrowEOF(long moreBytesNeeded, string message)
        {
            throw string.IsNullOrWhiteSpace(message) ? new MoreDataNeededException(moreBytesNeeded) : new MoreDataNeededException(message, moreBytesNeeded);
        }
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
                if (x < 0) return ThrowEOF(1, nameof(ReadVarint));

                ulong val = (ulong)(x & 127);
                if ((x & 128) == 0) return val;

                int shift = 7;
                for (int i = 0; i < 8; i++)
                {
                    x = buffer.Take();
                    if (x < 0) return ThrowEOF(1, nameof(ReadVarint));
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
                if (x < 0) return ThrowEOF(1, nameof(ReadVarint));
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
                if ((a | b | c | d) < 0) return ThrowEOF(a < 0 ? 4 : b < 0 ? 3 : c < 0 ? 2 : 1, nameof(ReadRawUInt64));
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
                if ((a | b | c | d | e | f | g | h) < 0) return ThrowEOF(a < 0 ? 8 : b < 0 ? 7 : c < 0 ? 6 : d < 0 ? 5 : e < 0 ? 4 : f < 0 ? 3 : g < 0 ? 2 : 1, nameof(ReadRawUInt64));
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
                    return BitConverter.Int32BitsToSingle((int)reader.ReadRawUInt32());
                case WireType.Fixed64:
                    return BitConverter.Int64BitsToDouble((long)reader.ReadRawUInt64());
                default:
                    ThrowNotSupported(wireType);
                    return default;
            }
        }
        
        static readonly Encoding Encoding = Encoding.UTF8;
        [ThreadStatic]
        static Decoder _decoder;
        static Decoder Decoder => _decoder ?? (_decoder = Encoding.GetDecoder());
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
            const int MaxStackAllocSize = 8096; // no special reason, but limit needs to be somewhere
            unsafe string Slow(ref BufferReader buffer, int bytes)
                => bytes <= MaxStackAllocSize ? SlowStackAlloc(ref buffer, bytes) : SlowDoubleReadPreallocString(ref buffer, bytes);

            unsafe string SlowStackAlloc(ref BufferReader buffer, int bytes)
            {
                var decoder = Decoder;
                decoder.Reset();
                int bytesLeft = bytes, charCount = 0;

                var c = stackalloc char[bytes]; // good enough for now

                var cPtr = c;
                while (bytesLeft > 0 && !buffer.End)
                {
                    var span = buffer.Span;
                    int bytesThisSpan = Math.Min(bytesLeft, span.Length - buffer.Index);

                    fixed (byte* ptr = &MemoryMarshal.GetReference(buffer.Span))
                    {
                        int charsWritten = decoder.GetChars(ptr + buffer.Index, bytesThisSpan, cPtr, bytes - charCount, false);
                        cPtr += charsWritten;
                        charCount += charsWritten;
                    }
                    buffer.Skip(bytesThisSpan); // move to the next span, if one
                    bytesLeft -= bytesThisSpan;
                }

                if (bytesLeft != 0)
                {
#if DEBUG
                    ThrowEOF(bytesLeft, $"{nameof(ReadString)}, length {bytes}");
#else
                    ThrowEOF(bytesLeft, nameof(ReadString));
#endif
                }

                return new string(c, 0, charCount);
            }
            unsafe string SlowDoubleReadPreallocString(ref BufferReader buffer, int bytes)
            {
                throw new NotImplementedException("huge string support");
                // what this needs to do is: try to read the buffer once to compute the string length (decoder.GetCharCount),
                // then allocate a new string('\0', charCount), then fix the string and iterate over the buffer a second time,
                // using GetChars to write into the freshly allocated string
            }

            if (wireType != WireType.String) ThrowNotSupported(wireType);

            var len = checked((int)reader.ReadVarint());
            if (len == 0) return "";

            return reader.HasSpan(len) ? Fast(ref reader, len) : Slow(ref reader, len);
        }


        public static async ValueTask<(T Value, int AwaitCount)> DeserializeAsync<T>(this IResumableDeserializer<T> serializer, IPipeReader reader, T value = default, long maxBytes = long.MaxValue, CancellationToken cancellationToken = default)
        {
            var ctx = SimpleCache<DeserializationContext>.Get();
            var rootFrame = ctx.Resume(serializer, value, maxBytes);

            long bytesNeeded = 0, position = 0;
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken);
                ctx.IncrementAwaitCount();
                if (result.IsCancelled) throw new TaskCanceledException();
                var buffer = result.Buffer;
                try
                {
                    Verbose.WriteLine($"Awaits: {ctx.AwaitCount}, {buffer.Length} bytes");
                    if (bytesNeeded != 0 && bytesNeeded > buffer.Length)
                    {
                        Verbose.WriteLine($"not enough data to try deserializing; needs {bytesNeeded} bytes, has {buffer.Length}");
                    }
                    else
                    {
                        bool madeProgress = !ctx.Execute(ref buffer, ref position, out long moreBytesNeeded);
                        Verbose.WriteLine($"failed to make progress; needs {moreBytesNeeded} more bytes");
                        bytesNeeded = buffer.Length + moreBytesNeeded;
                    }
                }
                finally
                {
                    reader.Advance(buffer.Start, buffer.End); // need to ensure Advance is called even upon exception
                }

                if (result.IsCompleted && buffer.IsEmpty)
                {
                    Verbose.WriteLine("we're done here!");
                    int awaitCount = ctx.AwaitCount;
                    SimpleCache<DeserializationContext>.Recycle(ctx);
                    return (rootFrame.Get<T>(), awaitCount);
                }
            }
        }
    }
    // no holds barred, aggressive, no tentative "can I do this" melarky: just do it or die trying
    class AggressiveDeserializer : IResumableDeserializer<Order>, IResumableDeserializer<Customer>,
        IResumableDeserializer<ProtoBuf.CustomerMagicWrapper>
    {
        public static AggressiveDeserializer Instance { get; } = new AggressiveDeserializer();
        private AggressiveDeserializer() { }


        void IResumableDeserializer<ProtoBuf.CustomerMagicWrapper>.Deserialize(ReadOnlyBuffer buffer, ref ProtoBuf.CustomerMagicWrapper value, DeserializationContext ctx)
        {
            // no inheritence or factory - simple init
            if (value == null) value = new ProtoBuf.CustomerMagicWrapper();

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
                        var items = value.Items;
                        while (true)
                        {
                            Verbose.WriteLine($"adding customer...");
                            items.Add(ctx.DeserializeSubItem<Customer>(ref reader, Instance, fieldNumber, wireType));

                            (fieldNumber, wireType) = ctx.ReadNextField(ref reader);
                            if (fieldNumber != 1) goto HaveField;
                        }
                    default:
                        reader.SkipField(wireType);
                        break;
                }
            }

        }

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
                            Verbose.WriteLine($"[{value.Id}] now has {orders.Count} orders");
                            (fieldNumber, wireType) = ctx.ReadNextField(ref reader);
                            if (fieldNumber != 5) goto HaveField;
                        }
                    default:
                        reader.SkipField(wireType);
                        break;
                }
            }
        }

        static long GetLength(BufferReader reader)
        {
            long len = 0;
            while (!reader.End)
            {
                int spanLen = reader.Span.Length - reader.Index;
                reader.Skip(spanLen);
                len += spanLen;
            }
            return len;
        }
        void IResumableDeserializer<Order>.Deserialize(ReadOnlyBuffer buffer, ref Order value, DeserializationContext ctx)
        {
            // no inheritance or factory, so can do simple creation
            if (value == null) value = new Order();

            var reader = new BufferReader(buffer);
            while (true)
            {
                (var fieldNumber, var wireType) = ctx.ReadNextField(ref reader);
                Verbose.WriteLine($"[{ctx.Position(reader.Cursor)}] reading field {fieldNumber} ({wireType}), {GetLength(reader)} remaining...");

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
