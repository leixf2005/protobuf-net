using System;
#if PLAT_SPANS
using System.Buffers.Binary;
#endif
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ProtoBuf
{
    public partial class ProtoReader
    {
        /// <summary>
        /// Holds state used by the deserializer
        /// </summary>
        public ref struct State
        {
            internal static readonly Type ByRefType = typeof(State).MakeByRefType();
            internal static readonly Type[] ByRefTypeArray = new[] { ByRefType };
#if PLAT_SPANS
            internal SolidState Solidify() => new SolidState(
                _memory.Slice(OffsetInCurrent, RemainingInCurrent));

            internal State(ReadOnlyMemory<byte> memory) : this() => Init(memory);
            internal void Init(ReadOnlyMemory<byte> memory)
            {
                _memory = memory;
                Span = memory.Span;
                OffsetInCurrent = 0;
                RemainingInCurrent = Span.Length;
            }
            internal ReadOnlySpan<byte> Span { get; private set; }
            private ReadOnlyMemory<byte> _memory;
            internal int OffsetInCurrent { get; private set; }
            internal int RemainingInCurrent { get; private set; }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Skip(int bytes)
            {
                OffsetInCurrent += bytes;
                RemainingInCurrent -= bytes;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal ReadOnlySpan<byte> Consume(int bytes)
            {
                var s = Span.Slice(OffsetInCurrent, bytes);
                OffsetInCurrent += bytes;
                RemainingInCurrent -= bytes;
                return s;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal ReadOnlySpan<byte> Consume(int bytes, out int offset)
            {
                offset = OffsetInCurrent;
                OffsetInCurrent += bytes;
                RemainingInCurrent -= bytes;
                return Span;
            }

            internal int ReadVarintUInt64(out ulong value)
            {
                Debug.Assert(RemainingInCurrent >= 10);
                var span = Span.Slice(OffsetInCurrent);
                int len = TryParseUInt64Varint(span, out value);
                OffsetInCurrent += len;
                RemainingInCurrent -= len;
                return len;
            }
            internal static int TryParseUInt32Varint(ReadOnlySpan<byte> span, out uint value)
            {
                return span.Length >= 5
                    ? TryParseUInt32VarintFast(span, out value)
                    : TryParseUInt32VarintSlow(span, out value);
            }
            private static int TryParseUInt32VarintFast(ReadOnlySpan<byte> span, out uint value)
            {
                uint u32 = BinaryPrimitives.ReadUInt32LittleEndian(span);
                // we could have flags at 32,24,16,8
                // mask to just those (0x80808080)
                // now >> by 31, 23, 15, 7 and mask accordingly

                uint msbs = (u32 & 0x80808080);
                // keep in mind that we're working **little** endian; first byte is in 0xFF
                // and the fourth byte is in 0xFF000000
                switch (((msbs >> 28) | (msbs >> 21) | (msbs >> 14) | (msbs >> 7)) & 0x0F)
                {
                    default:
                        value = 0;
                        return 0;
                    case 0:
                    case 2:
                    case 4:
                    case 6:
                    case 8:
                    case 10:
                    case 12:
                    case 14:
                        // ***0
                        value = u32 & 0x7F;
                        return 1;
                    case 1:
                    case 5:
                    case 9:
                    case 13:
                        // **01
                        value = (u32 & 0x7F) | ((u32 & 0x7F00) >> 1);
                        return 2;
                    case 3:
                    case 11:
                        // *011
                        value = (u32 & 0x7F) | ((u32 & 0x7F00) >> 1) | ((u32 & 0x7F0000) >> 2);
                        return 3;
                    case 7:
                        // 0111
                        value = (u32 & 0x7F) | ((u32 & 0x7F00) >> 1) | ((u32 & 0x7F0000) >> 2) | ((u32 & 0x7F000000) >> 3);
                        return 4;
                    case 15:
                        // 1111
                        var final = span[4];
                        if ((final & 0xF0) != 0) ThrowOverflow(null);
                        value = (u32 & 0x7F) | ((u32 & 0x7F00) >> 1) | ((u32 & 0x7F0000) >> 2) | ((u32 & 0x7F000000) >> 3) | (uint)(final << 28);
                        return 5;
                }
            }
            private static int TryParseUInt32VarintSlow(ReadOnlySpan<byte> span, out uint value)
            {
                value = span[0];
                if ((value & 0x80) == 0) return 1;
                value &= 0x7F;

                uint chunk = span[1];
                value |= (chunk & 0x7F) << 7;
                if ((chunk & 0x80) == 0) return 2;

                chunk = span[2];
                value |= (chunk & 0x7F) << 14;
                if ((chunk & 0x80) == 0) return 3;

                chunk = span[3];
                value |= (chunk & 0x7F) << 21;
                if ((chunk & 0x80) == 0) return 4;

                chunk = span[4];
                value |= chunk << 28; // can only use 4 bits from this chunk
                if ((chunk & 0xF0) == 0) return 5;

                ThrowOverflow(null);
                return 0;
            }
            internal static int TryParseUInt64Varint(ReadOnlySpan<byte> span, out ulong value)
            {
                if(span.Length >= 4)
                {
                    uint u32 = BinaryPrimitives.ReadUInt32LittleEndian(span);
                    if ((u32 & 0x80808080) != 0x80808080)
                    {
                        // isn't 4 MSBs in a row, so we can use our 32-bit approach
                        int len = TryParseUInt32VarintFast(span, out u32);
                        value = u32;
                        return len;
                    }
                }
                return TryParseUInt64VarintSlow(span, out value);
            }
            private static int TryParseUInt64VarintSlow(ReadOnlySpan<byte> span, out ulong value)
            {
                value = span[0];
                if ((value & 0x80) == 0) return 1;
                value &= 0x7F;

                ulong chunk = span[1];
                value |= (chunk & 0x7F) << 7;
                if ((chunk & 0x80) == 0) return 2;

                chunk = span[2];
                value |= (chunk & 0x7F) << 14;
                if ((chunk & 0x80) == 0) return 3;

                chunk = span[3];
                value |= (chunk & 0x7F) << 21;
                if ((chunk & 0x80) == 0) return 4;

                chunk = span[4];
                value |= (chunk & 0x7F) << 28;
                if ((chunk & 0x80) == 0) return 5;

                chunk = span[5];
                value |= (chunk & 0x7F) << 35;
                if ((chunk & 0x80) == 0) return 6;

                chunk = span[6];
                value |= (chunk & 0x7F) << 42;
                if ((chunk & 0x80) == 0) return 7;

                chunk = span[7];
                value |= (chunk & 0x7F) << 49;
                if ((chunk & 0x80) == 0) return 8;

                chunk = span[8];
                value |= (chunk & 0x7F) << 56;
                if ((chunk & 0x80) == 0) return 9;

                chunk = span[9];
                value |= chunk << 63; // can only use 1 bit from this chunk

                if ((chunk & ~(ulong)0x01) != 0) ThrowOverflow(null);
                return 10;
            }
            internal int ReadVarintUInt32(out uint value)
            {
                Debug.Assert(RemainingInCurrent >= 5);
                int bytes = TryParseUInt32VarintFast(Span.Slice(OffsetInCurrent), out value);
                OffsetInCurrent += bytes;
                RemainingInCurrent -= bytes;
                return bytes;
            }

            internal void ReadBytes(ArraySegment<byte> target)
            {
                Debug.Assert(RemainingInCurrent >= target.Count);
                Span.Slice(OffsetInCurrent, target.Count).CopyTo(target.AsSpan());
                OffsetInCurrent += target.Count;
                RemainingInCurrent -= target.Count;
            }

            internal string ReadString(int bytes)
            {
                Debug.Assert(RemainingInCurrent >= bytes);
                string s = ReadOnlySequenceProtoReader.ToString(Span, OffsetInCurrent, bytes);
                OffsetInCurrent += bytes;
                RemainingInCurrent -= bytes;
                return s;
            }

            internal ulong ReadFixedUInt64()
            {
                var val = BinaryPrimitives.ReadUInt64LittleEndian(Span.Slice(OffsetInCurrent));
                OffsetInCurrent += 8;
                RemainingInCurrent -= 8;
                return val;
            }

            internal uint ReadFixedUInt32()
            {
                var val = BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice(OffsetInCurrent));
                OffsetInCurrent += 4;
                RemainingInCurrent -= 4;
                return val;
            }
#else
            internal SolidState Solidify() => default;
            internal int RemainingInCurrent => 0;
            internal int ReadVarintUInt32(out uint value) => throw new NotSupportedException();
            internal int ReadVarintUInt64(out ulong value) => throw new NotSupportedException();
            internal void Skip(int count) => throw new NotSupportedException();
            internal void ReadBytes(ArraySegment<byte> target) => throw new NotSupportedException();
            internal string ReadString(int bytes) => throw new NotSupportedException();
            internal ulong ReadFixedUInt64() => throw new NotSupportedException();
            internal uint ReadFixedUInt32() => throw new NotSupportedException();
#endif
        }

        internal readonly struct SolidState
        {
#if PLAT_SPANS
            private readonly ReadOnlyMemory<byte> _memory;
            internal SolidState(ReadOnlyMemory<byte> memory) => _memory = memory;
            internal State Liquify() => new State(_memory);
#else
            internal State Liquify() => default;
#endif
        }
    }
}
