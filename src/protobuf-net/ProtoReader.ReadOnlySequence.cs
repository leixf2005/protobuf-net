#if PLAT_SPANS
using ProtoBuf.Meta;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ProtoBuf
{
    public partial class ProtoReader
    {
        /// <summary>
        /// Creates a new reader against a multi-segment buffer
        /// </summary>
        /// <param name="source">The source buffer</param>
        /// <param name="state">Reader state</param>
        /// <param name="model">The model to use for serialization; this can be null, but this will impair the ability to deserialize sub-objects</param>
        /// <param name="context">Additional context about this serialization operation</param>
        public static ProtoReader Create(out State state, ReadOnlySequence<byte> source, TypeModel model, SerializationContext context = null)
        {
            var reader = ReadOnlySequenceProtoReader.GetRecycled()
                ?? new ReadOnlySequenceProtoReader();
            reader.Init(out state, source, model, context);
            return reader;
        }

        /// <summary>
        /// Creates a new reader against a multi-segment buffer
        /// </summary>
        /// <param name="source">The source buffer</param>
        /// <param name="state">Reader state</param>
        /// <param name="model">The model to use for serialization; this can be null, but this will impair the ability to deserialize sub-objects</param>
        /// <param name="context">Additional context about this serialization operation</param>
        public static ProtoReader Create(out State state, ReadOnlyMemory<byte> source, TypeModel model, SerializationContext context = null)
            => Create(out state, new ReadOnlySequence<byte>(source), model, context);

        private sealed class ReadOnlySequenceProtoReader : ProtoReader
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static string ToString(ReadOnlySpan<byte> span, int offset, int bytes)
            {
#if PLAT_SPAN_OVERLOADS
                return UTF8.GetString(span.Slice(offset, bytes));
#else
                unsafe
                {
                    fixed (byte* sPtr = &MemoryMarshal.GetReference(span))
                    {
                        var bPtr = sPtr + offset;
                        int chars = UTF8.GetCharCount(bPtr, bytes);
                        string s = new string('\0', chars);
                        fixed (char* cPtr = s)
                        {
                            UTF8.GetChars(bPtr, bytes, cPtr, chars);
                        }
                        return s;
                    }
                }
#endif
            }

            [ThreadStatic]
            private static ReadOnlySequenceProtoReader s_lastReader;
            private ReadOnlySequence<byte>.Enumerator _source;

            internal static ReadOnlySequenceProtoReader GetRecycled()
            {
                var tmp = s_lastReader;
                s_lastReader = null;
                return tmp;
            }

            internal override void Recycle()
            {
                Dispose();
                s_lastReader = this;
            }

            public override void Dispose()
            {
                base.Dispose();
                _source = default;
            }

            internal void Init(out State state, ReadOnlySequence<byte> source, TypeModel model, SerializationContext context)
            {
                base.Init(model, context);
                _source = source.GetEnumerator();
                state = default;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private int GetSomeData(ref State state, bool throwIfEOF = true)
            {
                var data = state.RemainingInCurrent;
                return data == 0 ? ReadNextBuffer(ref state, throwIfEOF) : data;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private int ReadNextBuffer(ref State state, bool throwIfEOF)
            {
                do
                {
                    if (!_source.MoveNext())
                    {
                        if (throwIfEOF) ThrowEoF(this);
                        return 0;
                    }
                    state.Init(_source.Current);
                } while (state.Span.IsEmpty);
                return state.Span.Length;
            }

            private protected override ulong FallbackReadUInt64Varint(ref State state)
            {
                Span<byte> span = stackalloc byte[10];
                Span<byte> target = span;

                int available = 0;
                if (state.RemainingInCurrent != 0)
                {
                    int take = Math.Min(state.RemainingInCurrent, target.Length);
                    Peek(ref state, take).CopyTo(target);
                    target = target.Slice(available);
                    available += take;
                }

                var iterCopy = _source;
                while (!target.IsEmpty && iterCopy.MoveNext())
                {
                    var nextBuffer = iterCopy.Current.Span;
                    var take = Math.Min(nextBuffer.Length, target.Length);

                    nextBuffer.Slice(0, take).CopyTo(target);
                    target = target.Slice(take);
                    available += take;
                }

                if (available != 10) span = span.Slice(0, available);
                int bytes = State.TryParseUInt64Varint(span, out var val);
                ProtoReader.ImplSkipBytes(this, ref state, bytes);
                return val;
            }

            private protected override uint FallbackReadUInt32Fixed(ref State state)
            {
                Span<byte> span = stackalloc byte[4];
                // manually inline ImplReadBytes because of compiler restriction
                var target = span;
                while (!target.IsEmpty)
                {
                    var take = Math.Min(GetSomeData(ref state), target.Length);
                    Consume(ref state, take).CopyTo(target);
                    target = target.Slice(take);
                }
                return BinaryPrimitives.ReadUInt32LittleEndian(span);
            }

            private protected override ulong FallbackReadUInt64Fixed(ref State state)
            {
                Span<byte> span = stackalloc byte[8];
                // manually inline ImplReadBytes because of compiler restriction
                var target = span;
                while (!target.IsEmpty)
                {
                    var take = Math.Min(GetSomeData(ref state), target.Length);
                    Consume(ref state, take).CopyTo(target);
                    target = target.Slice(take);
                }
                return BinaryPrimitives.ReadUInt64LittleEndian(span);
            }

            private protected override string FallbackReadString(ref State state, int bytes)
            {
                // we should probably do the work with a Decoder,
                // but this works for today
                using (var mem = MemoryPool<byte>.Shared.Rent(bytes))
                {
                    var span = mem.Memory.Span;
                    FallbackReadBytes(ref state, span);
                    return ToString(span, 0, bytes);
                }
            }

            private void FallbackReadBytes(ref State state, Span<byte> target)
            {
                while (!target.IsEmpty)
                {
                    var take = Math.Min(GetSomeData(ref state), target.Length);
                    Consume(ref state, take).CopyTo(target);
                    target = target.Slice(take);
                }
            }

            private protected override void FallbackReadBytes(ref State state, ArraySegment<byte> target)
                => FallbackReadBytes(ref state, target.AsSpan());

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private ReadOnlySpan<byte> Consume(ref State state, int bytes)
            {
                Advance(bytes);
                return state.Consume(bytes);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private ReadOnlySpan<byte> Consume(ref State state, int bytes, out int offset)
            {
                Advance(bytes);
                return state.Consume(bytes, out offset);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private ReadOnlySpan<byte> Peek(ref State state, int bytes)
                => state.Span.Slice(state.OffsetInCurrent, bytes);

            private protected override int FallbackTryReadUInt32VarintWithoutMoving(ref State state, out uint value)
            {
                Span<byte> span = stackalloc byte[5];
                Span<byte> target = span;
                var currentBuffer = Peek(ref state, Math.Min(target.Length, state.RemainingInCurrent));
                currentBuffer.CopyTo(target);
                int available = currentBuffer.Length;
                target = target.Slice(available);

                var iterCopy = _source;
                while (!target.IsEmpty && iterCopy.MoveNext())
                {
                    var nextBuffer = iterCopy.Current.Span;
                    var take = Math.Min(nextBuffer.Length, target.Length);

                    nextBuffer.Slice(0, take).CopyTo(target);
                    target = target.Slice(take);
                    available += take;
                }
                if (available == 0)
                {
                    value = 0;
                    return 0;
                }
                if (available != 5) span = span.Slice(0, available);
                return State.TryParseUInt32Varint(span, out value);
            }

            private protected override void FallbackSkipBytes(ref State state, long count)
            {
                while (count != 0)
                {
                    var take = (int)Math.Min(GetSomeData(ref state), count);
                    state.Skip(take);
                    Advance(take);
                    count -= take;
                }
            }

            private protected override bool IsFullyConsumed(ref State state)
                => GetSomeData(ref state, false) == 0;
        }
    }
}
#endif