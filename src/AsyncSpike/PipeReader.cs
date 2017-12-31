using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ProtoBuf
{
    internal sealed class PipeReader : AsyncProtoReader
    {
        private IPipeReader _reader;
        private readonly bool _closePipe;
        private volatile bool _isReading;
        ReadOnlyBuffer _available, _originalAsReceived;
        internal PipeReader(IPipeReader reader, bool closePipe, long bytes = long.MaxValue) : base(bytes)
        {
            _reader = reader;
            _closePipe = closePipe;
        }
        protected override Task SkipBytesAsync(int pBytes)
        {
            async Task ImplAsync(int bytes)
            {
                while (bytes > 0)
                {
                    if (!await RequestMoreDataAsync().ConfigureAwait(false)) ThrowEOF<int>();

                    var remove = Math.Min(bytes, checked((int)_available.Length));
                    _available = _available.Slice(remove);
                    bytes -= remove;
                    Advance(remove);
                }
            }
            if (pBytes >= checked((int)_available.Length))
            {
                _available = _available.Slice(pBytes);
                Advance(pBytes);
                return Task.CompletedTask;
            }

            pBytes -= (int)_available.Length;
            _available = _available.Slice(pBytes);
            return ImplAsync(pBytes);
        }
        private Task EnsureBufferedAsync(int bytes)
        {
            async Task Awaited(Task<bool> task)
            {
                if (!await task.ConfigureAwait(false)) ThrowEOF<int>();
                while (_available.Length < bytes)
                {
                    if (!await RequestMoreDataAsync().ConfigureAwait(false)) ThrowEOF<int>();
                }
            }
            while (_available.Length < bytes)
            {
                var task = RequestMoreDataAsync();
                if (!task.IsCompleted) return Awaited(task);
                if (!task.Result) return ThrowEOF<Task>();
            }
            return Task.CompletedTask;
        }

        private unsafe T ReadLittleEndian<T>() where T : struct
        {
            T val;
            if (_available.First.Length >= Unsafe.SizeOf<T>())
            {
                val = _available.First.Span.NonPortableCast<byte, T>()[0];
            }
            else
            {
                byte* raw = stackalloc byte[Unsafe.SizeOf<T>()];
                _available.Slice(0, Unsafe.SizeOf<T>())
                    .CopyTo(new Span<byte>(raw, Unsafe.SizeOf<T>()));
                val = Unsafe.Read<T>(raw);
            }
            _available = _available.Slice(Unsafe.SizeOf<T>());
            Advance(Unsafe.SizeOf<T>());
            return val;
        }

        protected override ValueTask<uint> ReadFixedUInt32Async()
        {
            async ValueTask<uint> Awaited(Task task)
            {
                await task.ConfigureAwait(false);
                return ReadLittleEndian<uint>();
            }
            var t = EnsureBufferedAsync(4);
            if (!t.IsCompleted) return Awaited(t);

            t.Wait(); // check for exception
            return AsTask(ReadLittleEndian<uint>());
        }
        protected override ValueTask<ulong> ReadFixedUInt64Async()
        {
            async ValueTask<ulong> Awaited(Task task)
            {
                await task.ConfigureAwait(false);
                return ReadLittleEndian<ulong>();
            }

            var t = EnsureBufferedAsync(8);
            if (!t.IsCompleted) return Awaited(t);

            t.Wait(); // check for exception
            return AsTask(ReadLittleEndian<ulong>());
        }
        protected override ValueTask<byte[]> ReadBytesAsync(int bytes)
        {
            async ValueTask<byte[]> Awaited(Task task, int len)
            {
                await task.ConfigureAwait(false);
                return Process(len);
            }
            byte[] Process(int len)
            {
                var arr = _available.Slice(0, len).ToArray();
                _available = _available.Slice(len);
                Advance(len);
                return arr;
            }
            var t = EnsureBufferedAsync(bytes);
            if (!t.IsCompleted) return Awaited(t, bytes);

            t.Wait(); // check for exception
            return AsTask(Process(bytes));
        }
        protected override ValueTask<string> ReadStringAsync(int bytes)
        {
            async ValueTask<string> Awaited(Task task, int len)
            {
                await task.ConfigureAwait(false);
                return Process(len);
            }
            unsafe string Process(int len)
            {
                string s;
                var first = _available.First;
                if (first.Length >= len)
                {
                    s = MemoryReader.GetUtf8String(first, len);
                }
                else if (_available.IsSingleSpan)
                {
                    throw new EndOfStreamException();
                }
                else
                {
                    var decoder = Encoding.GetDecoder();
                    int bytesLeft = len;
                    var iter = _available.GetEnumerator();
                    int charCount = 0;
                    while (bytesLeft > 0 && iter.MoveNext())
                    {
                        var buffer = iter.Current;
                        int bytesThisBuffer = Math.Min(bytesLeft, buffer.Length);
                        fixed (byte* ptr = &MemoryMarshal.GetReference(buffer.Span))
                        {
                            charCount += decoder.GetCharCount(ptr, bytesThisBuffer, false);
                        }
                        bytesLeft -= bytesThisBuffer;
                    }
                    if (bytesLeft != 0) throw new EndOfStreamException();
                    decoder.Reset();

                    s = new string((char)0, charCount);
                    iter = _available.GetEnumerator();
                    bytesLeft = len;
                    fixed (char* c = s)
                    {
                        var cPtr = c;
                        while (bytesLeft > 0 && iter.MoveNext())
                        {
                            var buffer = iter.Current;
                            int bytesThisBuffer = Math.Min(bytesLeft, buffer.Length);
                            fixed (byte* ptr = &MemoryMarshal.GetReference(buffer.Span))
                            {
                                int charsWritten = decoder.GetChars(ptr, bytesThisBuffer, cPtr, charCount, false);
                                cPtr += charsWritten;
                                charCount -= charsWritten;
                            }
                            bytesLeft -= bytesThisBuffer;
                        }
                        if (charCount != 0 || bytesLeft != 0) throw new EndOfStreamException();
                    }
                }
                Trace($"Read string: {s}");

                _available = _available.Slice(len);
                Advance(len);
                return s;
            }

            var t = EnsureBufferedAsync(bytes);
            if (!t.IsCompleted) return Awaited(t, bytes);

            t.Wait(); // check for exception
            return AsTask(Process(bytes));
        }
        private static (int value, int consumed) TryPeekVarintInt32(ref ReadOnlyBuffer buffer)
        {
            Trace($"Parsing varint from {buffer.Length} bytes...");
            return (buffer.IsSingleSpan || buffer.First.Length >= MaxBytesForVarint)
                ? TryPeekVarintSingleSpan(buffer.First.Span)
                : TryPeekVarintMultiSpan(ref buffer);
        }
        internal static unsafe (int value, int consumed) TryPeekVarintSingleSpan(ReadOnlySpan<byte> span)
        {
            int len = span.Length;
            if (len == 0) return (0, 0);
            // thought: optimize the "I have tons of data" case? (remove the length checks)
            fixed (byte* spanPtr = &MemoryMarshal.GetReference(span))
            {
                var ptr = spanPtr;

                // least significant group first
                int val = *ptr & 127;
                if ((*ptr & 128) == 0)
                {
                    Trace($"Parsed {val} from 1 byte");
                    return (val, 1);
                }
                if (len == 1) return (0, 0);

                val |= (*++ptr & 127) << 7;
                if ((*ptr & 128) == 0)
                {
                    Trace($"Parsed {val} from 2 bytes");
                    return (val, 2);
                }
                if (len == 2) return (0, 0);

                val |= (*++ptr & 127) << 14;
                if ((*ptr & 128) == 0)
                {
                    Trace($"Parsed {val} from 3 bytes");
                    return (val, 3);
                }
                if (len == 3) return (0, 0);

                val |= (*++ptr & 127) << 21;
                if ((*ptr & 128) == 0)
                {
                    Trace($"Parsed {val} from 4 bytes");
                    return (val, 4);
                }
                if (len == 4) return (0, 0);

                val |= (*++ptr & 127) << 28;
                if ((*ptr & 128) == 0)
                {
                    Trace($"Parsed {val} from 5 bytes");
                    return (val, 5);
                }
                if (len == 5) return (0, 0);

                // TODO switch to long and check up to 10 bytes (for -1)
                throw new NotImplementedException("need moar pointer math");
            }
        }
        private static unsafe (int value, int consumed) TryPeekVarintMultiSpan(ref ReadOnlyBuffer buffer)
        {
            int value = 0;
            int consumed = 0, shift = 0;
            foreach (var segment in buffer)
            {
                var span = segment.Span;
                if (span.Length != 0)
                {
                    fixed (byte* ptr = &MemoryMarshal.GetReference(span))
                    {
                        byte* head = ptr;
                        while (consumed++ < MaxBytesForVarint)
                        {
                            int val = *head++;
                            value |= (val & 127) << shift;
                            shift += 7;
                            if ((val & 128) == 0)
                            {
                                Trace($"Parsed {value} from {consumed} bytes (multiple spans)");
                                return (value, consumed);
                            }
                        }
                    }
                }
            }
            return (0, 0);
        }

        const int MaxBytesForVarint = 10;


        private Task<bool> RequestMoreDataAsync()
        {
            // ask the underlying pipe for more data
            ValueAwaiter<ReadResult> BeginReadAsync()
            {
                _reader.Advance(_available.Start, _available.End);
                _isReading = true;
                _available = default(ReadOnlyBuffer);
                return _reader.ReadAsync();
            }
            // accept data from the pipe, and see whether we should ask again
            bool EndReadCheckAskAgain(ReadResult read, long oldLen)
            {
                _originalAsReceived = _available = read.Buffer;
                _isReading = false;

                if (read.IsCancelled)
                {
                    throw new ObjectDisposedException(GetType().Name);
                }
                return read.Buffer.Length <= oldLen && !read.IsCompleted;
            }
            // convert from a synchronous request to an async continuation
            async Task<bool> Awaited(ValueAwaiter<ReadResult> t, long oldLen)
            {
                ReadResult read = await t; // note: not a Task/ValueTask<T> - ConfigureAwait does not apply
                while (EndReadCheckAskAgain(read, oldLen))
                {
                    t = BeginReadAsync();
                    read = t.IsCompleted ? t.GetResult() : await t;
                }
                return PostProcess(oldLen);
            }
            // finalize state and see how well we did
            bool PostProcess(long oldLen)
            {
                if (End != long.MaxValue)
                {
                    ApplyDataConstraint();
                }
                return _available.Length > oldLen; // did we make progress?
            }

            {
                if (Position >= End)
                {
                    Trace("Refusing more data to sub-object");
                    return False; // nope!
                }

                var oldLen = _available.Length;
                ReadResult read;
                do
                {
                    var t = BeginReadAsync();
                    if (!t.IsCompleted) return Awaited(t, oldLen);
                    read = t.GetResult();
                }
                while (EndReadCheckAskAgain(read, oldLen));

                return PostProcess(oldLen) ? True : False;
            }
        }
        protected override void RemoveDataConstraint()
        {
            if (_available.End != _originalAsReceived.End)
            {
                var wasForConsoleMessage = _available.Length;
                // change back to the original right hand boundary
                _available = _originalAsReceived.Slice(_available.Start);
                Trace($"Data constraint removed; {_available.Length} bytes available (was {wasForConsoleMessage})");
            }
        }
        protected override void ApplyDataConstraint()
        {
            if (End != long.MaxValue && checked(Position + _available.Length) > End)
            {
                int allow = checked((int)(End - Position));
                var wasForConsoleMessage = _available.Length;
                _available = _available.Slice(0, allow);
                Trace($"Data constraint imposed; {_available.Length} bytes available (was {wasForConsoleMessage})");
            }
        }
        protected override ValueTask<int?> TryReadVarintInt32Async(bool consume)
        {
            async ValueTask<int?> Awaited(Task<bool> task, bool consumeData)
            {
                while (await task.ConfigureAwait(false))
                {
                    var read = TryPeekVarintInt32(ref _available);
                    if (read.consumed != 0)
                    {
                        if (consumeData)
                        {
                            Advance(read.consumed);
                            _available = _available.Slice(read.consumed);
                        }
                        return read.value;
                    }

                    task = RequestMoreDataAsync();
                }
                if (_available.Length == 0) return null;
                return ThrowEOF<int?>();
            }

            Task<bool> more;
            do
            {
                var read = TryPeekVarintInt32(ref _available);
                if (read.consumed != 0)
                {
                    if (consume)
                    {
                        Advance(read.consumed);
                        _available = _available.Slice(read.consumed);
                    }
                    return AsTask<int?>(read.value);
                }

                more = RequestMoreDataAsync();
                if (!more.IsCompleted) return Awaited(more, consume);
            }
            while (more.Result);

            if (_available.Length == 0) return AsTask<int?>(null);
            return ThrowEOF<ValueTask<int?>>();
        }

        public override void Dispose()
        {
            var reader = _reader;
            var available = _available;
            _reader = null;
            _available = default(ReadOnlyBuffer);
            if (reader != null)
            {
                if (_isReading)
                {
                    reader.CancelPendingRead();
                }
                else
                {
                    reader.Advance(available.Start);
                }

                if (_closePipe)
                {
                    reader.Complete();
                }
            }
        }
    }
}
