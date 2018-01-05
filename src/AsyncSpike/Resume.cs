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
    class MoreDataNeededException : Exception
    {
        public long MinBytes { get; }
        public MoreDataNeededException(long minBytes = 2) { minBytes = MinBytes; }
    }
    class DeserializationContext : IDisposable
    {
        void IDisposable.Dispose()
        {
            _previous = null;
            _resume.Clear();
            AwaitCount = 0;
            ContinueFrom = default;
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

        readonly Stack<ResumeFrame> _resume = new Stack<ResumeFrame>();

        internal bool Execute(ref ReadOnlyBuffer buffer)
        {
            while (!buffer.IsEmpty && _resume.TryPeek(out var frame))
            {
                long available = buffer.Length;
                if (available < frame.MinBytes) return false;

                ReadOnlyBuffer slice;
                if (frame.MaxBytes != long.MaxValue && available > frame.MaxBytes)
                {
                    Console.WriteLine($"{frame.MaxBytes} remaining");
                    slice = buffer.Slice(0, frame.MaxBytes);
                }
                else
                {
                    slice = buffer;
                }
                if (frame.Execute(ref slice, this, out var consumedBytes))
                {
                    
                    // complete from length limit (sub-message or outer limit)
                    // or from group markers (sub-message only)
                    if (!ReferenceEquals(_resume.Pop(), frame))
                    {
                        throw new InvalidOperationException("Object was completed, but he wrong frame was popped; oops!");
                    }
                    frame.ChangeState(ResumeFrame.ResumeState.Deserializing, ResumeFrame.ResumeState.AdvertisingFieldHeader);
                    _previous = frame; // so the frame before can extract the values
                }
                Console.WriteLine($"consumed {consumedBytes} bytes");
                if (consumedBytes == 0) return false;
                buffer = buffer.Slice(consumedBytes);
            }
            return true;
        }
        internal ResumeFrame Resume<T>(IResumableDeserializer<T> serializer, T value, long consumedBytes, long maxBytes = long.MaxValue)
        {
            var frame = ResumeFrame.Create<T>(serializer, value, 0, default, 2, maxBytes);
            _resume.Push(frame);
            return frame;
        }

        internal void ResetConsumedBytes(Position start)
        {
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
            }
            catch
            {
                // incomplete object; do what we can
                frame = ResumeFrame.Create<T>(serializer, value, fieldNumber, wireType, 2, len);
                _resume.Push(frame);
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
        public long MinBytes { get; private set; }
        public long MaxBytes { get; private set; }
        public static ResumeFrame Create<T>(IResumableDeserializer<T> serializer, T value, int fieldNumber, WireType wireType, long minBytes, long maxBytes)
        {
            return SimpleCache<TypedResumeFrame<T>>.Get().Init(value, serializer, fieldNumber, wireType, minBytes, maxBytes);
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

        internal abstract bool Execute(ref ReadOnlyBuffer slice, DeserializationContext ctx, out long consumedBytes);

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
            internal override void Recycle() => SimpleCache<TypedResumeFrame<T>>.Recycle(this);

            public T Value => _value;
            private T _value;
            private IResumableDeserializer<T> _serializer;

            internal ResumeFrame Init(T value, IResumableDeserializer<T> serializer, int fieldNumber, WireType wireType, long minBytes, long maxBytes)
            {
                _value = value;
                _serializer = serializer;
                _fieldNumber = fieldNumber;
                _wireType = wireType;
                _state = ResumeState.Deserializing;
                MinBytes = minBytes;
                MaxBytes = maxBytes;
                return this;
            }

            internal override bool Execute(ref ReadOnlyBuffer slice, DeserializationContext ctx, out long consumedBytes)
            {
                Console.WriteLine($"Executing frame for {typeof(T).Name}...");
                ctx.ResetConsumedBytes(slice.Start);
                bool result;
                try
                {
                    _serializer.Deserialize(slice, ref _value, ctx);
                    result = true;
                }
                catch (EndOfStreamException)
                {
                    result = false;
                }
                consumedBytes = new ReadOnlyBuffer(slice.Start, ctx.ContinueFrom).Length;
                MaxBytes -= consumedBytes;
                MinBytes = Math.Min(2, MinBytes - consumedBytes);
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
