//using System;
//using System.Buffers;
//using System.IO;
//using System.IO.Pipelines;
//using System.Runtime.InteropServices;
//using System.Text;
//using System.Threading.Tasks;

//namespace ProtoBuf
//{
//    enum v2Result
//    {
//        Success,
//        SwitchToAsync // for an open-ended sub-object
//    }
//    interface ISer2<T>
//    {
//        v2Result Deserialize(ref ReadableBufferReader reader, ref T value);
//        ValueTask<T> DeserializeAsync(IPipeReader reader, T value);
//    }

//    static class Ser2Extensions
//    {
//        static uint ThrowOverflow() => throw new OverflowException();
//        static bool ThrowNotSupported(WireType wireType) => throw new NotSupportedException(wireType.ToString());

//        internal static v2Result DefaultResult(this WireType wireType)
//        {
//            switch(wireType)
//            {
//                case WireType.Varint:
//                case WireType.Fixed32:
//                case WireType.Fixed64:
//                    return v2Result.Success;
//                case WireType.StartGroup:
//                case WireType.String:
//                    return v2Result.SwitchToAsync;
//                default:
//                    ThrowNotSupported(wireType);
//                    return default;
//            }
//        }
//        public static bool TrySkip(ref this ReadableBufferReader reader, int length)
//        {
//            try
//            {
//                reader.Skip(length);
//                return true;
//            }
//            catch (ArgumentOutOfRangeException)
//            {
//                return false;
//            }
//        }
//        public static bool TrySkipField(ref this ReadableBufferReader reader, WireType wireType)
//        {
//            switch (wireType)
//            {
//                case WireType.Varint:
//                    return reader.TryReadVarint().HasValue;
//                case WireType.Fixed64:
//                    return reader.TrySkip(8);
//                case WireType.String:
//                    var len = reader.TryReadVarint();
//                    return len != null && reader.TrySkip(checked((int)len.GetValueOrDefault()));
//                case WireType.Fixed32:
//                    return reader.TrySkip(4);
//                default:
//                    return ThrowNotSupported(wireType);
//            }
//        }
//        private static uint? TryReadRawUInt32(ref this ReadableBufferReader reader)
//        {
//            uint Fast(ref ReadableBufferReader buffer)
//            {
//                int index = buffer.Index;
//                var span = buffer.Span;
//                var val = (uint)(span[index++] | (span[index++] << 8) | (span[index++] << 16) | (span[index] << 24));
//                buffer.Skip(4);
//                return val;
//            }

//            uint? Slow(ref ReadableBufferReader buffer)
//            {
//                int a = buffer.Take(), b = buffer.Take(), c = buffer.Take(), d = buffer.Take();
//                if (a < 0 || b < 0 || c < 0 || d < 0) return null;
//                return (uint)(a | (b << 8) | (c << 16) | (d << 24));
//            }

//            return reader.HasSpan(4) ? Fast(ref reader) : Slow(ref reader);
//        }

//        private static bool HasSpan(ref this ReadableBufferReader reader, int bytes)
//           => reader.Span.Length >= reader.Index + bytes;

//        private static ulong? TryReadRawUInt64(ref this ReadableBufferReader reader)
//        {
//            ulong Fast(ref ReadableBufferReader buffer)
//            {
//                int index = buffer.Index;
//                var span = buffer.Span;
//                var lo = (uint)(span[index++] | (span[index++] << 8) | (span[index++] << 16) | (span[index++] << 24));
//                var hi = (uint)(span[index++] | (span[index++] << 8) | (span[index++] << 16) | (span[index] << 24));
//                buffer.Skip(8);
//                return (ulong)lo | ((ulong)hi) << 32;
//            }

//            ulong? Slow(ref ReadableBufferReader buffer)
//            {
//                int a = buffer.Take(), b = buffer.Take(), c = buffer.Take(), d = buffer.Take(),
//                    e = buffer.Take(), f = buffer.Take(), g = buffer.Take(), h = buffer.Take();
//                if (a < 0 || b < 0 || c < 0 || d < 0
//                    || e < 0 || f < 0 || g < 0 || h < 0) return null;
//                var lo = (uint)(a | (b << 8) | (c << 16) | (d << 24));
//                var hi = (uint)(e | (f << 8) | (g << 16) | (h << 24));
//                return (ulong)lo | ((ulong)hi) << 32;
//            }

//            return reader.HasSpan(8) ? Fast(ref reader) : Slow(ref reader);
//        }
//        private static int? CheckedInt32(this ulong? value)
//        {
//            if (value == null) return null;
//            long l = (long)value.GetValueOrDefault();
//            return checked((int)l);
//        }

//        public static int? TryReadInt32(ref this ReadableBufferReader reader, WireType wireType)
//        {
//            switch (wireType)
//            {
//                case WireType.Varint:
//                    return reader.TryReadVarint().CheckedInt32();
//                case WireType.Fixed32:
//                    return (int?)reader.TryReadRawUInt32();
//                case WireType.Fixed64:
//                    return reader.TryReadRawUInt64().CheckedInt32();
//                default:
//                    ThrowNotSupported(wireType);
//                    return default;
//            }
//        }


//        private static unsafe float? ToSingle(this uint? value)
//        {
//            if (value == null) return null;
//            var x = value.GetValueOrDefault();
//            return *(float*)(&x);
//        }

//        private static unsafe double? ToDouble(this ulong? value)
//        {
//            if (value == null) return null;
//            var x = value.GetValueOrDefault();
//            return *(double*)(&x);
//        }

//        //// it is *not* a mistake that this isn't "ref this"; this is intended as a peek - side-effects are local
//        //public static bool IsField(this ReadableBufferReader reader, int fieldNumber)
//        //    => checked((int)(reader.TryReadVarint().GetValueOrDefault() >> 3)) == fieldNumber;



//        // slices a portion of a reader out to a separate reader; note that from the caller's
//        // perspective the reader will have progressed
//        public static ReadableBufferReader Slice(ref this ReadableBufferReader reader, int start, int length)
//        {
//            if(start != 0) reader.Skip(start);

//            var startPos = reader.Cursor;
//            var hex = reader.GetHex(length);

//            // var before = reader.GetHex(length);
//            reader.Skip(length);
//            var endPos = reader.Cursor;

//            var rob = new ReadableBuffer(startPos, endPos);
//            var result = new ReadableBufferReader(rob);
            
//            // var after = result.GetHex(length);

//            return result;
//        }

//        private static unsafe string GetHex(this ReadableBufferReader reader, int len)
//        {
//            const string HEX = "0123456789ABCDEF";
//            var c = stackalloc char[len * 2];
//            var ptr = c;
//            int i;
//            for (i = 0; i < len; i++)
//            {
//                int val = reader.Take();
//                if (val < 0) break;

//                *ptr++ = HEX[val >> 4];
//                *ptr++ = HEX[val & 15];
//            }
//            return new string(c, 0, 2 * i);
//        }
//        public static (bool Result, int Bytes) HasEntireSubObject(ref this ReadableBufferReader reader, WireType wireType)
//        {
//            switch(wireType)
//            {
//                case WireType.String:
//                    var mutable = reader;
//                    var nlen = mutable.TryReadVarint().CheckedInt32();
//                    if (nlen == null) break;
//                    var bytes = nlen.GetValueOrDefault();
//                    if (mutable.HasSpan(bytes))
//                    {
//                        reader = mutable; // update to position
//                        return (true, bytes);
//                    }
//                    try {
//                        var clone = mutable;
//                        clone.Skip(bytes); // to check
//                        reader = mutable;
//                        return (true, bytes);
//                    }
//                    catch (ArgumentOutOfRangeException) { break; }
//            }
//            return (false, 0);
//        }
//        public static double? TryReadDouble(ref this ReadableBufferReader reader, WireType wireType)
//        {
//            switch (wireType)
//            {
//                case WireType.Varint:
//                    return (long?)reader.TryReadVarint();
//                case WireType.Fixed32:
//                    return reader.TryReadRawUInt32().ToSingle();
//                case WireType.Fixed64:
//                    return reader.TryReadRawUInt64().ToDouble();
//                default:
//                    ThrowNotSupported(wireType);
//                    return default;
//            }
//        }

//        static readonly Encoding Encoding = Encoding.UTF8;
//        public static string TryReadString(ref this ReadableBufferReader reader, WireType wireType)
//        {
//            unsafe string Fast(ref ReadableBufferReader buffer, int bytes)
//            {
//                string s;
//                fixed (byte* ptr = &MemoryMarshal.GetReference(buffer.Span))
//                {
//                    s = Encoding.GetString(ptr + buffer.Index, bytes);
//                }
//                buffer.Skip(bytes);
//                return s;
//            }
//            unsafe string Slow(ref ReadableBufferReader buffer, int bytes)
//            {
//                var decoder = Encoding.GetDecoder();
//                int bytesLeft = bytes;

//                var snapshot = buffer;
//                int charCount = 0;
//                while (bytesLeft > 0 && !buffer.End)
//                {
//                    var span = buffer.Span;
//                    int bytesThisSpan = Math.Min(bytesLeft, span.Length - buffer.Index);

//                    fixed (byte* ptr = &MemoryMarshal.GetReference(buffer.Span))
//                    {
//                        charCount += decoder.GetCharCount(ptr + buffer.Index, bytesThisSpan, false);
//                    }
//                    buffer.Skip(bytesThisSpan); // move to the next span, if one
//                    bytesLeft -= bytesThisSpan;
//                }
//                if (bytesLeft != 0) return null;

//                decoder.Reset();
//                buffer = snapshot;

//                string s = new string((char)0, charCount);
//                bytesLeft = bytes;
//                fixed (char* c = s)
//                {
//                    var cPtr = c;
//                    while (bytesLeft > 0 && !buffer.End)
//                    {
//                        var span = buffer.Span;
//                        int bytesThisSpan = Math.Min(bytesLeft, span.Length - buffer.Index);

//                        fixed (byte* ptr = &MemoryMarshal.GetReference(buffer.Span))
//                        {
//                            int charsWritten = decoder.GetChars(ptr + buffer.Index, bytesThisSpan, cPtr, charCount, false);
//                            cPtr += charsWritten;
//                            charCount -= charsWritten;
//                        }
//                        buffer.Skip(bytesThisSpan); // move to the next span, if one
//                        bytesLeft -= bytesThisSpan;
//                    }
//                    if (charCount != 0 || bytesLeft != 0) throw new InvalidOperationException("Something went wrong decoding a string!");
//                }
//                return s;
//            }

//            if (wireType != WireType.String) ThrowNotSupported(wireType);

//            var nlen = reader.TryReadVarint().CheckedInt32();
//            if (nlen == null) return null;
//            int len = nlen.GetValueOrDefault();
//            if (len == 0) return "";
//            return reader.HasSpan(len) ? Fast(ref reader, len) : Slow(ref reader, len);
//        }

//        public static (int FieldNumber, WireType WireType) ReadNextField(ref this ReadableBufferReader reader)
//        {
//            var next = reader.TryReadVarint();
//            if (next == null) return default;

//            ulong x = next.GetValueOrDefault();
//            return (checked((int)(x >> 3)), (WireType)(int)(x & 7));
//        }
//        public static ulong? TryReadVarint(ref this ReadableBufferReader reader)
//        {
//            ulong Fast(ref ReadableBufferReader buffer)
//            {
//                var span = buffer.Span;
//                ulong val = 0;
//                int shift = 0, x;
//                int index = buffer.Index;
//                for (int i = 0; i < 9; i++)
//                {
//                    x = span[index++];
//                    val |= ((ulong)(x & 127)) << shift;
//                    if ((x & 128) == 0)
//                    {
//                        buffer.Skip(i + 1);
//                        return val;
//                    }
//                    shift += 7;
//                }
//                x = span[index];
//                buffer.Skip(10);
//                switch (x)
//                {
//                    case 0:
//                        return val;
//                    case 1:
//                        return val | (1UL << 63);
//                }
//                return ThrowOverflow();
//            }
//            ulong? Slow(ref ReadableBufferReader buffer)
//            {
//                var x = buffer.Take();
//                if (x < 0) return null;

//                ulong val = (ulong)x;
//                if ((val & 128) == 0) return val;

//                int shift = 7;
//                for (int i = 0; i < 8; i++)
//                {
//                    x = buffer.Take();
//                    if (x < 0) return null;
//                    val |= ((ulong)(x & 127)) << shift;
//                    if ((x & 128) == 0)
//                    {
//                        return val;
//                    }
//                    shift += 7;
//                }
//                x = buffer.Take();
//                switch (buffer.Take())
//                {
//                    case 0:
//                        return val;
//                    case 1:
//                        return val | (1UL << 63);
//                }
//                if (x < 0) return null;
//                return ThrowOverflow();
//            }
//            return reader.HasSpan(10) ? Fast(ref reader) : Slow(ref reader);
//        }
//        private static (v2Result Status, long ConsumedBytes) DeserializeSync<T>(ISer2<T> serializer, ref ReadableBuffer buffer, ref T value)
//        {
//            var br = new ReadableBufferReader(buffer);
//            var status = serializer.Deserialize(ref br, ref value);
//            buffer = buffer.Slice(br.Cursor);
//            return (status, br.ConsumedBytes);

//        }
//        public static T Deserialize<T>(this ISer2<T> serializer, ref ReadableBuffer buffer, T value = default)
//        {
//            var reader = new ReadableBufferReader(buffer);
//            var status = serializer.Deserialize(ref reader, ref value);
//            if (status == v2Result.Success && reader.End) return value;

//            throw new EndOfStreamException();
//        }

//        public static async ValueTask<T> DeserializeAsync<T>(this ISer2<T> serializer, IPipeReader reader, T value = default, long maxBytes = long.MaxValue)
//        {
//            while (maxBytes != 0)
//            {
//                var result = await reader.ReadAsync();

//                var buffer = result.Buffer;
//                if (buffer.IsEmpty && result.IsCompleted)
//                    break;

//                if (buffer.Length > maxBytes)
//                {
//                    buffer = buffer.Slice(0, maxBytes);
//                }
//                var tuple = DeserializeSync<T>(serializer, ref buffer, ref value);
//                maxBytes -= tuple.ConsumedBytes;

//                // if we need to switch to async, then we know nothing about what we've examined - so advertise Start;
//                // otherwise, we've examined to the end

//                if (tuple.Status == v2Result.SwitchToAsync)
//                {
//                    // don't claim anything about what has/hasn't been examined - we're just
//                    // changing our approach
//                    reader.Advance(buffer.Start, buffer.Start);

//                    throw new NotImplementedException();
//                }
//                else
//                {
//                    // we've looked at the buffer and decided that we're a little stuck
//                    reader.Advance(buffer.Start, buffer.End);
//                }
//            }
//            return value;
//        }
//    }
//}
