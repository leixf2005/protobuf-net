using ProtoBuf;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Sequences;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace AggressiveNamespace
{
    static class SimpleCache<T> where T : class, IDisposable, new()
    {
        static T _instance;
        public static T Get() => Interlocked.Exchange(ref _instance, null) ?? new T();
        public static void Recycle(T obj)
        {
            if(obj != null)
            {
                obj.Dispose();
                Interlocked.Exchange(ref _instance, obj);
            }
        }
    }
    class MoreDataNeededException : EndOfStreamException
    {
        public long MoreBytesNeeded { get; }
        public MoreDataNeededException(long moreBytesNeeded) { MoreBytesNeeded = moreBytesNeeded; }
        public MoreDataNeededException(string message, long moreBytesNeeded) : base(message) { MoreBytesNeeded = moreBytesNeeded; }
    }
    class DeserializationContext : IDisposable
    {
        void IDisposable.Dispose()
        {
            _previous = null;
            _resume.Clear();
            AwaitCount = 0;
            ContinueFrom = BatchStart = default;
            _pushLock = 0;
        }
        ResumeFrame _previous;
        public (int FieldNumber, WireType WireType) ReadNextField(ref BufferReader reader)
        {
            // do we have a value to pop?
            var frame = _previous;
            if (frame != null)
            {
                return frame.FieldHeader();
            }

            ContinueFrom = reader.Cursor; // store our known-safe recovery position
            if (reader.End) return (0, default);
            var fieldHeader = reader.ReadVarint();

            int fieldNumber = checked((int)(fieldHeader >> 3));
            var wireType = (WireType)(int)(fieldHeader & 7);
            if (fieldNumber <= 0) throw new InvalidOperationException($"Invalid field number: {fieldNumber}");
            return (fieldNumber, wireType);
        }
        internal Position ContinueFrom { get; private set; }
        internal Position BatchStart { get; private set; }

        readonly Stack<ResumeFrame> _resume = new Stack<ResumeFrame>();

        internal bool Execute(ref ReadOnlyBuffer buffer, ref long position, out long moreBytesNeeded)
        {
            moreBytesNeeded = 0;
            while (!buffer.IsEmpty && _resume.TryPeek(out var frame))
            {
                long available = buffer.Length;
                
                ReadOnlyBuffer slice;
                bool frameIsComplete = false;
                if (frame.End != long.MaxValue && (position = Position(buffer.Start)) + available >= frame.End)
                {
                    frameIsComplete = true; // we have all the data, so we don't expect it to fail with more-bytes-needed 
                    slice = buffer.Slice(0, frame.End - position);
                }
                else
                {
                    slice = buffer;
                }
                Verbose.WriteLine($"{frame} executing on {slice.Length} bytes from {position} to {(position + slice.Length)}; expected end: {(frame.End == long.MaxValue ? -1 : frame.End)}...");

                ResetStart(position, slice.Start);
                if (frame.TryExecute(ref slice, this, out var consumedBytes, out moreBytesNeeded))
                {
                    Verbose.WriteLine($"Complete frame: {frame}");
                    // complete from length limit (sub-message or outer limit)
                    // or from group markers (sub-message only)
                    if (!ReferenceEquals(_resume.Pop(), frame))
                    {
                        throw new InvalidOperationException("Object was completed, but the wrong frame was popped; oops!");
                    }
                    frame.ChangeState(ResumeFrame.ResumeState.Deserializing, ResumeFrame.ResumeState.AdvertisingFieldHeader);
                    _previous = frame; // so the frame before can extract the values
                }
                ShowFrames("after TryExecute");

                position += consumedBytes;

                if(frameIsComplete && moreBytesNeeded !=0 )
                {
                    throw new InvalidOperationException("We should have had a complete frame, but someone is requesting more data");
                }
                if (consumedBytes == 0) return false;
                Verbose.WriteLine($"consumed {consumedBytes} bytes; position now {position}");
                buffer = buffer.Slice(consumedBytes);
            }
            return true;
        }
        void ShowFrames(string state)
        {
            if (_resume.Count != 0)
            {
                Verbose.WriteLine($"Incomplete frames: {state}");
                foreach (var x in _resume)
                {
                    Verbose.WriteLine($"\t{x}; expected end: {(x.End == long.MaxValue ? -1 : x.End)}...");
                }
            }
        }
        internal ResumeFrame Resume<T>(IResumableDeserializer<T> serializer, T value, long end)
        {
            var frame = ResumeFrame.Create<T>(serializer, value, 0, WireType.String, end);
            _resume.Push(frame);
            ShowFrames("after Resume");
            return frame;
        }
        internal long Position(Position position)
        {
            return BatchStartPosition + new ReadOnlyBuffer(BatchStart, position).Length;
        }
        internal long BatchStartPosition { get; private set; }
        internal void ResetStart(long position, Position start)
        {
            BatchStartPosition = position;
            BatchStart = start;
            ContinueFrom = start;
        }

        internal T DeserializeSubItem<T>(ref BufferReader reader,
            IResumableDeserializer<T> serializer,
            int fieldNumber, WireType wireType, T value = default)
        {
            DeserializeSubItem(ref reader, serializer, fieldNumber, wireType, ref value);
            return value;
        }
        internal void DeserializeSubItem<T>(ref BufferReader reader,
            IResumableDeserializer<T> serializer,
            int fieldNumber, WireType wireType, ref T value)
        {
            var frame = _previous;
            if (frame != null)
            {
                Verbose.WriteLine($"Consuming popped '{typeof(T).Name}'");
                value = frame.Get<T>(fieldNumber, wireType);
                _previous.Recycle();
                _previous = null; // and thus it was consumed!
                return;
            }

            // we're going to assume length-prefixed here
            if (wireType != WireType.String) throw new NotImplementedException();
            int len = checked((int)reader.ReadVarint());

            var from = reader.Cursor;
            bool itFits;
            try
            {   // see if we have it all directly
                reader.Skip(len);
                itFits = true;
            }
            catch(ArgumentOutOfRangeException)
            {
                itFits = false;
            }
            
            if(itFits)
            {
                var slice = new ReadOnlyBuffer(from, reader.Cursor);
                Verbose.WriteLine($"Reading '{typeof(T).Name}' without using the context stack; {len} bytes needed, {slice.Length} available; {Position(from)} to {Position(reader.Cursor)}");

                try
                {
                    _pushLock++;
                    serializer.Deserialize(slice, ref value, this);
                    _pushLock--;
                }
                catch (MoreDataNeededException ex)
                {
                    throw new InvalidOperationException($"{typeof(T).Name} should have been fully contained, but requested {ex.MoreBytesNeeded} more bytes: {ex.Message}");
                }
            }
            else
            {
                // incomplete object; do what we can
                long start = Position(from);

                frame = ResumeFrame.Create<T>(serializer, value, fieldNumber, wireType, start + len);
                var slice = new ReadOnlyBuffer(from, reader.Cursor);

                
                Verbose.WriteLine($"Reading '{typeof(T).Name}' using the context stack (incomplete data); {len} bytes needed, {slice.Length} available; {start} to {frame.End}");

                if (_pushLock != 0)
                {
                    throw new InvalidOperationException($"{typeof(T).Name} should have been fully contained, but didn't seem to fit");
                }
                _resume.Push(frame);
                ShowFrames("in DeserializeSubItem");
                frame.Execute(ref slice, this);

                throw new InvalidOperationException("I unexpectedly succeeded; this is embarrassing");
            }
        }
        int _pushLock;
        public int AwaitCount { get; private set; }
        internal void IncrementAwaitCount() => AwaitCount++;

        
    }
    interface IResumableDeserializer<T>
    {
        void Deserialize(ReadOnlyBuffer buffer, ref T value, DeserializationContext ctx);
    }
    abstract class ResumeFrame
    {
        internal abstract void Recycle();
        public long End { get; private set; }
        public static ResumeFrame Create<T>(IResumableDeserializer<T> serializer, T value, int fieldNumber, WireType wireType, long end)
        {
            return SimpleCache<TypedResumeFrame<T>>.Get().Init(value, serializer, fieldNumber, wireType, end);
        }
        internal T Get<T>(int fieldNumber, WireType wireType)
        {
            ChangeState(ResumeState.ProvidingValue, ResumeState.Complete);
            if (fieldNumber != _fieldNumber || wireType != _wireType)
            {
                throw new InvalidOperationException($"Invalid field/wire-type combination");
            }
            return Get<T>();
        }
        internal T Get<T>() => ((TypedResumeFrame<T>)this).Value;

        internal abstract void Execute(ref ReadOnlyBuffer slice, DeserializationContext ctx);
        internal abstract bool TryExecute(ref ReadOnlyBuffer slice, DeserializationContext ctx, out long consumedBytes, out long moreBytesNeeded);

        private int _fieldNumber;
        private WireType _wireType;
        ResumeState _state;
        internal (int FieldNumber, WireType WireType) FieldHeader()
        {
            ChangeState(ResumeState.AdvertisingFieldHeader, ResumeState.ProvidingValue);
            return (_fieldNumber, _wireType);
        }
        internal void ChangeState(ResumeState from, ResumeState to)
        {
            if (_state == from) _state = to;
            else throw new InvalidOperationException($"Invalid state; expected '{from}', was '{_state}'");
        }

        sealed class TypedResumeFrame<T> : ResumeFrame, IDisposable
        {
            void IDisposable.Dispose()
            {
                _value = default;
                _serializer = default;
            }
            public override string ToString() => $"Frame of '{typeof(T).Name}' (field {_fieldNumber}, {_wireType}): {_value}";
            internal override void Recycle() => SimpleCache<TypedResumeFrame<T>>.Recycle(this);

            public T Value => _value;
            private T _value;
            private IResumableDeserializer<T> _serializer;

            internal ResumeFrame Init(T value, IResumableDeserializer<T> serializer, int fieldNumber, WireType wireType, long end)
            {
                _value = value;
                _serializer = serializer;
                _fieldNumber = fieldNumber;
                _wireType = wireType;
                _state = ResumeState.Deserializing;
                End = end;
                return this;
            }

            internal override void Execute(ref ReadOnlyBuffer slice, DeserializationContext ctx)
            {
                Verbose.WriteLine($"Executing frame for {typeof(T).Name}... [{ctx.Position(slice.Start)}-{ctx.Position(slice.End)}]");
                try
                {
                    _serializer.Deserialize(slice, ref _value, ctx);
                }
                finally
                {
                    Verbose.WriteLine($"{typeof(T).Name} after execute: {_value}");
                }
            }
            internal override bool TryExecute(ref ReadOnlyBuffer slice, DeserializationContext ctx, out long consumedBytes, out long moreBytesNeeded)
            {
                Verbose.WriteLine($"Executing frame for {typeof(T).Name}... [{ctx.Position(slice.Start)}-{ctx.Position(slice.End)}]");
                bool result;
                moreBytesNeeded = 0;
                try
                {
                    _serializer.Deserialize(slice, ref _value, ctx);
                    result = true;
                }
                catch (MoreDataNeededException ex)
                {
                    moreBytesNeeded = ex.MoreBytesNeeded;
                    result = false;
                }
                Verbose.WriteLine($"{typeof(T).Name} after execute: {_value}");
                consumedBytes = new ReadOnlyBuffer(slice.Start, ctx.ContinueFrom).Length;
                return result;
            }
        }

        internal enum ResumeState
        {
            Deserializing,
            AdvertisingFieldHeader,
            ProvidingValue,
            Complete
        }
    }

}
