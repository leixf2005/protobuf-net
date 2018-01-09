using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
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
    public static class AggressiveDeserializerExtensions
    {
        public static T Deserialize<T>(this IResumableDeserializer<T> serializer, in ReadOnlyBuffer buffer, T value = default)
        {
            var ctx = SimpleCache<DeserializationContext>.Get();

            ctx.SetOrigin(0, buffer);
            serializer.Deserialize(buffer, ref value, ctx);
            SimpleCache<DeserializationContext>.Recycle(ctx);
            return value;
        }

        public static long SerializeWithLengthPrefix<TBuffer, T>(this IResumableDeserializer<T> serializer, in TBuffer buffer, in T value, int fieldNumber = 1)
            where TBuffer : IOutput
        {
            var ctx = SimpleCache<DeserializationContext>.Get();

            var result = ctx.SerializeSubItem(buffer, serializer, value, fieldNumber, WireType.String);
            SimpleCache<DeserializationContext>.Recycle(ctx);
            return result;
        }
        public static long Serialize<TBuffer, T>(this IResumableDeserializer<T> serializer, in TBuffer buffer, in T value)
            where TBuffer : IOutput
        {
            var ctx = SimpleCache<DeserializationContext>.Get();

            var result = serializer.Serialize(buffer, value, ctx);
            SimpleCache<DeserializationContext>.Recycle(ctx);
            return result;
        }
        static uint ThrowOverflow() => throw new OverflowException();
        static uint ThrowEOF(long moreBytesNeeded, string message)
        {
            throw string.IsNullOrWhiteSpace(message) ? new MoreDataNeededException(moreBytesNeeded) : new MoreDataNeededException(message, moreBytesNeeded);
        }
        static bool ThrowNotSupported(WireType wireType) => throw new NotSupportedException(wireType.ToString());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasSpan(ref this BufferReader<ReadOnlyBuffer> reader, int bytes)
                => reader.Span.Length >= reader.Index + bytes;

        public static int WriteVarint<TBuffer>(ref this OutputWriter<TBuffer> writer, ulong value)
            where TBuffer : IOutput
        {
            int Fast(ref OutputWriter<TBuffer> w, byte v)
            {
                w.Ensure(1);
                w.Span[0] = v;
                w.Advance(1);
                return 1;
            }
            int Slow(ref OutputWriter<TBuffer> w, ulong v)
            {
                w.Ensure(10);
                var span = w.Span;
                int len = 1;
                span[0] = (byte)(((uint)v & 127U) | 128);
                v >>= 7;
                while (v != 0)
                {
                    span[len++] = (byte)(((uint)v & 127U) | 128);
                    v >>= 7;
                }
                span[len - 1] &= 127;
                w.Advance(len);
                return len;
            }
            if (typeof(TBuffer) == typeof(NilOutput))
            {
                return VarintLength(value);
            }
            else
            {
                return value < 128 ? Fast(ref writer, (byte)value) : Slow(ref writer, value);
            }
        }

        public static unsafe int WriteString<TBuffer>(ref this OutputWriter<TBuffer> writer, string value)
            where TBuffer : IOutput
        {

            int Fast(ref OutputWriter<TBuffer> w, string v, int c, int estimate)
            {
                w.Ensure(estimate + 1);
                var span = w.Span;
                fixed (char* chars = value)
                fixed (byte* bytes = &MemoryMarshal.GetReference(span))
                {
                    estimate = Encoding.GetBytes(chars, c, bytes + 1, span.Length - 1);

                }
                Debug.Assert(estimate <= 127, "we expected a string to fit in a single-length prefix, and it didn't, oops!");
                span[0] = (byte)estimate;

                w.Advance(++estimate);
                return estimate;
            }
            int Slow(ref OutputWriter<TBuffer> w, string v, int c, int knownSize)
            {
                int totalWritten = WriteVarint(ref w, (uint)knownSize);

                w.Ensure(4);
                var span = w.Span;
                fixed (char* chars = value)
                {
                    if (span.Length >= knownSize) // looks like it should fit in the primary span, yay!
                    {
                        int bytesWritten;
                        fixed (byte* bytes = &MemoryMarshal.GetReference(span))
                        {
                            bytesWritten = Encoding.GetBytes(chars, c, bytes, span.Length);

                        }
                        Debug.Assert(bytesWritten == knownSize, "we had the wrong numer of bytes encoding a string, oops!");
                        totalWritten += bytesWritten;
                        w.Advance(bytesWritten);
                    }
                    else
                    {
                        var encoder = GetEncoder();
                        int charsWritten = 0;
                        bool complete = false;
                        while (c > 0 || !complete)
                        {
                            int bytesWritten, charsRead;
                            fixed (byte* bytes = &MemoryMarshal.GetReference(span))
                            {
                                encoder.Convert(chars + charsWritten, c, bytes, span.Length, false, out charsRead, out bytesWritten, out complete);
                            }
                            charsWritten += charsRead;
                            c -= charsRead;
                            knownSize -= bytesWritten;
                            w.Advance(bytesWritten);
                            totalWritten += bytesWritten;

                            if (knownSize > 0)
                            {
                                // we're going to need more space
                                w.Ensure(4);
                                span = w.Span;
                            }
                        }
                        Debug.Assert(knownSize == 0, "we had the wrong numer of bytes encoding a string, oops!");
                        Debug.Assert(c == 0, "we had chars left over encoding a string, oops!");
                    }
                }
                return totalWritten;
            }
            {
                int charCount = value.Length, byteCount;
                if (charCount == 0) return WriteVarint(ref writer, 0);

                // max bytes per char in utf-16 to utf-8 is 4; 4 * 32 = 128, we can fit 127 - so: 31
                if (charCount < 32) return Fast(ref writer, value, charCount, charCount << 2);

                byteCount = Encoding.GetByteCount(value);
                if (byteCount < 128) return Fast(ref writer, value, charCount, byteCount);

                return Slow(ref writer, value, charCount, byteCount);
            }

        }

        //public static int WriteVarint(in this WritableBuffer writer, ulong value)
        //{
        //    int Fast(in WritableBuffer w, byte v)
        //    {
        //        w.Ensure(1);
        //        w.Buffer.Span[0] = v;
        //        w.Advance(1);
        //        return 1;
        //    }
        //    int Slow(in WritableBuffer w, ulong v)
        //    {
        //        w.Ensure(10);
        //        var span = w.Buffer.Span;
        //        int len = 1;
        //        span[0] = (byte)(((uint)v & 127U) | 128);
        //        v >>= 7;
        //        while (v != 0)
        //        {
        //            span[len++] = (byte)(((uint)v & 127U) | 128);
        //            v >>= 7;
        //        }
        //        span[len - 1] &= 127;
        //        w.Advance(len);
        //        return len;
        //    }
        //    return value < 128 ? Fast(writer, (byte)value) : Slow(writer, value);
        //}

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int VarintLength(uint value)
        {
            int len = 1;
            while ((value & ~127U) != 0)
            {
                value >>= 7;
                len++;
            }
            return len;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int VarintLength(ulong value)
        {
            int len = 1;
            while ((value & ~127UL) != 0)
            {
                value >>= 7;
                len++;
            }
            return len;
        }


        public static ulong ReadVarint(ref this BufferReader<ReadOnlyBuffer> reader)
        {
            ulong Fast(ref BufferReader<ReadOnlyBuffer> buffer)
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
            ulong Slow(ref BufferReader<ReadOnlyBuffer> buffer)
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

        internal static void SkipField(ref this BufferReader<ReadOnlyBuffer> reader, WireType wireType)
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
        public static int ReadInt32(ref this BufferReader<ReadOnlyBuffer> reader, WireType wireType)
        {
            switch (wireType)
            {
                case WireType.Varint:
                    return checked((int)(unchecked((long)reader.ReadVarint())));
                case WireType.Fixed32:
                    return (int)ReadRawUInt32(ref reader);
                case WireType.Fixed64:
                    return checked((int)(unchecked((long)ReadRawUInt64(ref reader))));
                default:
                    ThrowNotSupported(wireType);
                    return default;
            }
        }
        private static uint ReadRawUInt32(ref BufferReader<ReadOnlyBuffer> reader)
        {
            uint Fast(ref BufferReader<ReadOnlyBuffer> buffer)
            {
                int index = buffer.Index;
                var span = buffer.Span;
                var val = (uint)(span[index++] | (span[index++] << 8) | (span[index++] << 16) | (span[index] << 24));
                buffer.Skip(4);
                return val;
            }

            uint Slow(ref BufferReader<ReadOnlyBuffer> buffer)
            {
                int a = buffer.Take(), b = buffer.Take(), c = buffer.Take(), d = buffer.Take();
                if ((a | b | c | d) < 0) return ThrowEOF(a < 0 ? 4 : b < 0 ? 3 : c < 0 ? 2 : 1, nameof(ReadRawUInt64));
                return (uint)(a | (b << 8) | (c << 16) | (d << 24));
            }

            return reader.HasSpan(4) ? Fast(ref reader) : Slow(ref reader);
        }
        private static ulong ReadRawUInt64(ref BufferReader<ReadOnlyBuffer> reader)
        {
            ulong Fast(ref BufferReader<ReadOnlyBuffer> buffer)
            {
                int index = buffer.Index;
                var span = buffer.Span;

                var lo = (uint)(span[index++] | (span[index++] << 8) | (span[index++] << 16) | (span[index++] << 24));
                var hi = (uint)(span[index++] | (span[index++] << 8) | (span[index++] << 16) | (span[index] << 24));
                buffer.Skip(8);
                return (ulong)lo | ((ulong)hi) << 32;
            }

            ulong Slow(ref BufferReader<ReadOnlyBuffer> buffer)
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
        public static double ReadDouble(ref this BufferReader<ReadOnlyBuffer> reader, WireType wireType)
        {
            switch (wireType)
            {
                case WireType.Varint:
                    return (long)reader.ReadVarint();
                case WireType.Fixed32:
                    return BitConverter.Int32BitsToSingle((int)ReadRawUInt32(ref reader));
                case WireType.Fixed64:
                    return BitConverter.Int64BitsToDouble((long)ReadRawUInt64(ref reader));
                default:
                    ThrowNotSupported(wireType);
                    return default;
            }
        }

        static readonly Encoding Encoding = Encoding.UTF8;
        [ThreadStatic]
        static Decoder _decoder;
        [ThreadStatic]
        static Encoder _encoder;

        static Decoder GetDecoder()
        {
            var val = _decoder ?? (_decoder = Encoding.GetDecoder());
            val.Reset();
            return val;
        }
        static Encoder GetEncoder()
        {
            var val = _encoder ?? (_encoder = Encoding.GetEncoder());
            val.Reset();
            return val;
        }
        public static string ReadString(ref this BufferReader<ReadOnlyBuffer> reader, WireType wireType)
        {
            unsafe string Fast(ref BufferReader<ReadOnlyBuffer> buffer, int bytes)
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
            unsafe string Slow(ref BufferReader<ReadOnlyBuffer> buffer, int bytes)
                => bytes <= MaxStackAllocSize ? SlowStackAlloc(ref buffer, bytes) : SlowDoubleReadPreallocString(ref buffer, bytes);

            unsafe string SlowStackAlloc(ref BufferReader<ReadOnlyBuffer> buffer, int bytes)
            {
                var decoder = GetDecoder();
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
            unsafe string SlowDoubleReadPreallocString(ref BufferReader<ReadOnlyBuffer> buffer, int bytes)
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

            long bytesNeeded = 0;
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
                        bool madeProgress = !ctx.Execute(ref buffer, out long moreBytesNeeded);
                        Verbose.WriteLine($"failed to make progress; needs {moreBytesNeeded} more bytes");
                        bytesNeeded = buffer.Length + moreBytesNeeded;
                    }
                }
                finally
                {
                    reader.Advance(buffer.Start, buffer.End); // need to ensure Advance is called even upon exception
                    ctx.ClearPosition(); // our Position should now be treated as meaningless
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
    public sealed class AggressiveDeserializer : IResumableDeserializer<Order>, IResumableDeserializer<Customer>,
        IResumableDeserializer<ProtoBuf.CustomerMagicWrapper>
    {
        public static AggressiveDeserializer Instance { get; } = new AggressiveDeserializer();
        private AggressiveDeserializer() { }

        //long IResumableDeserializer<ProtoBuf.Order>.GetLength(in Order value, DeserializationContext ctx)
        //{
        //    Verbose.WriteLine($"sizing Order");

        //    long totalBytes = 0;
        //    totalBytes += ctx.GetLengthInt32(1, WireType.Varint, value.Id, 0);
        //    totalBytes += ctx.GetLengthString(2, WireType.String, value.ProductCode, "");
        //    totalBytes += ctx.GetLengthInt32(3, WireType.Varint, value.Quantity, 0);
        //    totalBytes += ctx.GetLengthDouble(4, WireType.Fixed64, value.UnitPrice, 0.0);
        //    totalBytes += ctx.GetLengthString(5, WireType.String, value.Notes, "");

        //    Verbose.WriteLine($"sized Order: {totalBytes} bytes");
        //    return totalBytes;
        //}

        long IResumableDeserializer<ProtoBuf.Order>.Serialize<TBuffer>(in TBuffer buffer, in ProtoBuf.Order value, DeserializationContext ctx)
        {
            Verbose.WriteLine($"writing Order");

            var writer = OutputWriter.Create(buffer);
            long totalBytes = 0;

            totalBytes += ctx.WriteInt32(ref writer, 1, WireType.Varint, value.Id, 0);
            totalBytes += ctx.WriteString(ref writer, 2, WireType.String, value.ProductCode, "");
            totalBytes += ctx.WriteInt32(ref writer, 3, WireType.Varint, value.Quantity, 0);
            totalBytes += ctx.WriteDouble(ref writer, 4, WireType.Fixed64, value.UnitPrice, 0.0);
            totalBytes += ctx.WriteString(ref writer, 5, WireType.String, value.Notes, "");

            Verbose.WriteLine($"wrote Order: {totalBytes} bytes");
            return totalBytes;
        }
        //long IResumableDeserializer<ProtoBuf.Customer>.GetLength(in Customer value, DeserializationContext ctx)
        //{
        //    Verbose.WriteLine($"sizing Customer");

        //    long totalBytes = 0;

        //    totalBytes += ctx.GetLengthInt32(1, WireType.Varint, value.Id, 0);
        //    totalBytes += ctx.GetLengthString(2, WireType.String, value.Name, "");
        //    totalBytes += ctx.GetLengthString(3, WireType.String, value.Notes, "");
        //    totalBytes += ctx.GetLengthDouble(4, WireType.Fixed64, value.MarketValue, 0.0);

        //    var _5 = value.Orders;
        //    if (_5 != null)
        //    {
        //        foreach (var item in _5)
        //        {
        //            totalBytes += ctx.GetLengthSubItem(Instance, in item, 5, WireType.String);
        //        }
        //    }

        //    Verbose.WriteLine($"sized Customer: {totalBytes} bytes");
        //    return totalBytes;
        //}
        long IResumableDeserializer<ProtoBuf.Customer>.Serialize<TBuffer>(in TBuffer buffer, in ProtoBuf.Customer value, DeserializationContext ctx)
        {
            Verbose.WriteLine($"writing Customer");

            var writer = OutputWriter.Create(buffer);
            long totalBytes = 0;

            totalBytes += ctx.WriteInt32(ref writer, 1, WireType.Varint, value.Id, 0);
            totalBytes += ctx.WriteString(ref writer, 2, WireType.String, value.Name, "");
            totalBytes += ctx.WriteString(ref writer, 3, WireType.String, value.Notes, "");
            totalBytes += ctx.WriteDouble(ref writer, 4, WireType.Fixed64, value.MarketValue, 0.0);

            var _5 = value.Orders;
            if (_5 != null)
            {
                foreach (var item in _5)
                {
                    totalBytes += ctx.SerializeSubItem(buffer, Instance, in item, 5, WireType.String);
                }
            }

            Verbose.WriteLine($"wrote Customer: {totalBytes} bytes");
            return totalBytes;
        }

        //long IResumableDeserializer<ProtoBuf.CustomerMagicWrapper>.GetLength(in ProtoBuf.CustomerMagicWrapper value, DeserializationContext ctx)
        //{
        //    Verbose.WriteLine($"sizing CustomerMagicWrapper");
        
        //    long totalBytes = 0;
        //    var _1 = value.Items;
        //    if (_1 != null)
        //    {
        //        foreach (var item in _1)
        //        {
        //            totalBytes += ctx.GetLengthSubItem(Instance, in item, 1, WireType.String);
        //        }
        //    }

        //    Verbose.WriteLine($"sized CustomerMagicWrapper: {totalBytes} bytes");
        //    return totalBytes;
        //}
        long IResumableDeserializer<ProtoBuf.CustomerMagicWrapper>.Serialize<TBuffer>(in TBuffer buffer, in ProtoBuf.CustomerMagicWrapper value, DeserializationContext ctx)
        {
            Verbose.WriteLine($"writing CustomerMagicWrapper");
            var writer = OutputWriter.Create(buffer);

            long totalBytes = 0;
            var _1 = value.Items;
            if (_1 != null)
            {
                foreach (var item in _1)
                {
                    totalBytes += ctx.SerializeSubItem(buffer, Instance, in item, 1, WireType.String);
                }
            }

            Verbose.WriteLine($"wrote CustomerMagicWrapper: {totalBytes} bytes");
            return totalBytes;
        }
        void IResumableDeserializer<ProtoBuf.CustomerMagicWrapper>.Deserialize(in ReadOnlyBuffer buffer, ref ProtoBuf.CustomerMagicWrapper value, DeserializationContext ctx)
        {

            Verbose.WriteLine($"[{ctx.Position(0)}] reading CustomerMagicWrapper, [{ctx.Position(0)}]-[{ctx.Position(buffer.Length)}]");
            Debug.Assert(ctx.PositionSlow(buffer.Start) == ctx.Position(0));
            Debug.Assert(ctx.PositionSlow(buffer.End) == ctx.Position(buffer.Length));

            // no inheritence or factory - simple init
            if (value == null) value = new ProtoBuf.CustomerMagicWrapper();

            var reader = BufferReader.Create(buffer);
            while (true)
            {
                (var fieldNumber, var wireType) = ctx.ReadNextField(ref reader);
                Verbose.WriteLine($"[{ctx.Position(reader.ConsumedBytes)}] reading CustomerMagicWrapper field {fieldNumber} ({wireType}), {GetLength(reader)} remaining...");

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
                            items.Add(ctx.DeserializeSubItem<Customer>(ref reader, Instance, 1, wireType));

                            (fieldNumber, wireType) = ctx.ReadNextField(ref reader);
                            if (fieldNumber != 1) goto HaveField;
                        }
                    default:
                        reader.SkipField(wireType);
                        break;
                }
            }

        }

        void IResumableDeserializer<Customer>.Deserialize(in ReadOnlyBuffer buffer, ref Customer value, DeserializationContext ctx)
        {
            Verbose.WriteLine($"[{ctx.Position(0)}] reading Customer, [{ctx.Position(0)}]-[{ctx.Position(buffer.Length)}]");
            Debug.Assert(ctx.PositionSlow(buffer.Start) == ctx.Position(0));
            Debug.Assert(ctx.PositionSlow(buffer.End) == ctx.Position(buffer.Length));

            // no inheritance or factory, so can do simple creation
            if (value == null) value = new Customer();

            var reader = BufferReader.Create(buffer);
            while (true)
            {
                (var fieldNumber, var wireType) = ctx.ReadNextField(ref reader);
                Verbose.WriteLine($"[{ctx.Position(reader.ConsumedBytes)}] reading Customer field {fieldNumber} ({wireType}), {GetLength(reader)} remaining...");

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

        static long GetLength(BufferReader<ReadOnlyBuffer> reader) // not in/ref - we're going to corrupt it
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
        void IResumableDeserializer<Order>.Deserialize(in ReadOnlyBuffer buffer, ref Order value, DeserializationContext ctx)
        {
            Verbose.WriteLine($"[{ctx.Position(0)}] reading Order, [{ctx.Position(0)}]-[{ctx.Position(buffer.Length)}]");
            Debug.Assert(ctx.PositionSlow(buffer.Start) == ctx.Position(0));
            Debug.Assert(ctx.PositionSlow(buffer.End) == ctx.Position(buffer.Length));

            // no inheritance or factory, so can do simple creation
            if (value == null) value = new Order();

            var reader = BufferReader.Create(buffer);
            while (true)
            {
                (var fieldNumber, var wireType) = ctx.ReadNextField(ref reader);
                Verbose.WriteLine($"[{ctx.Position(reader.ConsumedBytes)}] reading Order field {fieldNumber} ({wireType}), {GetLength(reader)} remaining...");

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
