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
    }
    class DeserializationContext : IDisposable
    {
        void IDisposable.Dispose()
        {
            _previous = null;
            _resume.Clear();
            AwaitCount = 0;
            ContinueFrom = BatchStart = default;
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
                Console.WriteLine($"{frame} executing on {slice.Length} bytes...");

                ResetStart(position, slice.Start);
                if (frame.Execute(ref slice, this, out var consumedBytes, out moreBytesNeeded))
                {
                    
                    // complete from length limit (sub-message or outer limit)
                    // or from group markers (sub-message only)
                    if (!ReferenceEquals(_resume.Pop(), frame))
                    {
                        throw new InvalidOperationException("Object was completed, but the wrong frame was popped; oops!");
                    }
                    frame.ChangeState(ResumeFrame.ResumeState.Deserializing, ResumeFrame.ResumeState.AdvertisingFieldHeader);
                    _previous = frame; // so the frame before can extract the values
                }
                position += consumedBytes;

                if(frameIsComplete && moreBytesNeeded !=0 )
                {
                    throw new InvalidOperationException("We should have had a complete frame, but someone is requesting more data");
                }
                if (consumedBytes == 0) return false;
                Console.WriteLine($"consumed {consumedBytes} bytes; position now {position}");
                buffer = buffer.Slice(consumedBytes);
            }
            return true;
        }
        internal ResumeFrame Resume<T>(IResumableDeserializer<T> serializer, T value, long end)
        {
            var frame = ResumeFrame.Create<T>(serializer, value, 0, WireType.String, end);
            _resume.Push(frame);
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
        private long lastPos = 0;
        internal void DeserializeSubItem<T>(ref BufferReader reader,
            IResumableDeserializer<T> serializer,
            int fieldNumber, WireType wireType, ref T value)
        {
            var frame = _previous;
            if (frame != null)
            {
                Console.WriteLine($"Consuming popped '{typeof(T).Name}'");
                value = frame.Get<T>(fieldNumber, wireType);
                _previous.Recycle();
                _previous = null; // and thus it was consumed!
                return;
            }


            // we're going to assume length-prefixed here
            if (wireType != WireType.String) throw new NotImplementedException();
            int len = checked((int)reader.ReadVarint());

            var from = reader.Cursor;
            bool expectToFail;
            try
            {   // see if we have it all directly
                reader.Skip(len);
                expectToFail = false;

                var debugPos = Position(from);
                if (debugPos == 528186 || debugPos < lastPos) Debugger.Break();
                lastPos = debugPos;
                Console.WriteLine($"Reading '{typeof(T).Name}' without using the context stack; {len} bytes needed; {debugPos} to {Position(reader.Cursor)}");
            }
            catch
            {
                // incomplete object; do what we can
                long start = Position(from);
                if (start < lastPos) Debugger.Break();
                lastPos = start;
                frame = ResumeFrame.Create<T>(serializer, value, fieldNumber, wireType, start + len);
                _resume.Push(frame);
                Console.WriteLine($"Reading '{typeof(T).Name}' using the context stack (incomplete data); {len} bytes needed; {start} to {frame.End}");
                expectToFail = true;
            }
            var slice = new ReadOnlyBuffer(from, reader.Cursor);
            serializer.Deserialize(slice, ref value, this);
            if (expectToFail) throw new InvalidOperationException("I unexpectedly succeeded; this is problematic");
        }

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

        internal abstract bool Execute(ref ReadOnlyBuffer slice, DeserializationContext ctx, out long consumedBytes, out long moreBytesNeeded);

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
            public override string ToString() => $"Frame of '{typeof(T).Name}' (field {_fieldNumber}, {_wireType})";
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

            internal override bool Execute(ref ReadOnlyBuffer slice, DeserializationContext ctx, out long consumedBytes, out long moreBytesNeeded)
            {
                Console.WriteLine($"Executing frame for {typeof(T).Name}...");
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
