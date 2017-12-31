using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace ProtoBuf
{
    public abstract class SyncProtoReader : AsyncProtoReader
    {
        protected SyncProtoReader(long length = long.MaxValue) : base(length) { }
        public virtual bool PreferSync => true;

        protected override ValueTask<uint> ReadFixedUInt32Async() => AsTask(ReadFixedUInt32());
        protected override ValueTask<ulong> ReadFixedUInt64Async() => AsTask(ReadFixedUInt64());
        protected override Task SkipBytesAsync(int bytes) { SkipBytes(bytes); return Task.CompletedTask; }
        protected override ValueTask<byte[]> ReadBytesAsync(int bytes) => AsTask(ReadBytes(bytes));
        protected override ValueTask<string> ReadStringAsync(int bytes) => AsTask(ReadString(bytes));
        protected override ValueTask<int?> TryReadVarintInt32Async(bool consume = true) => AsTask(TryReadVarintInt32(consume));

        public override Task<bool> AssertNextFieldAsync(int fieldNumber) => AsTask(AssertNextField(fieldNumber));
        public virtual bool AssertNextField(int fieldNumber)
        {
            var field = TryReadVarintInt32(false);
            return field >> 3 == fieldNumber && ReadNextField();
        }

        public override Task<bool> ReadNextFieldAsync() => AsTask(ReadNextField());
        public bool ReadNextField()
        {
            var next = TryReadVarintInt32();
            SetFieldHeader(next.GetValueOrDefault());
            return next.HasValue;
        }


        public override Task<bool> ReadBooleanAsync() => AsTask(ReadBoolean());
        public bool ReadBoolean()
        {
            var val = ReadInt32();
            return val != 0;
        }

        public override ValueTask<int> ReadInt32Async() => AsTask(ReadInt32());
        public int ReadInt32()
        {
            switch (WireType)
            {
                case WireType.Varint:
                    return ValueOrEOF(TryReadVarintInt32());
                case WireType.Fixed32:
                    return checked((int)ReadFixedUInt32());
                case WireType.Fixed64:
                    return checked((int)ReadFixedUInt64());
                default:
                    throw new InvalidOperationException();
            }
        }

        public override ValueTask<byte[]> ReadBytesAsync() => AsTask(ReadBytes());
        public byte[] ReadBytes()
        {
            if (WireType != WireType.String) throw new InvalidOperationException();

            var len = ValueOrEOF(TryReadVarintInt32());
            Trace($"BLOB length: {len}");
            return len == 0 ? EmptyBytes : ReadBytes(len);
        }

        public override ValueTask<string> ReadStringAsync() => AsTask(ReadString());
        public string ReadString()
        {
            if (WireType != WireType.String) throw new InvalidOperationException();

            var len = ValueOrEOF(TryReadVarintInt32());
            Trace($"String length: {len}");
            return len == 0 ? "" : ReadString(len);
        }

        public override Task SkipFieldAsync() { SkipField(); return Task.CompletedTask; }

        public void SkipField()
        {
            switch (WireType)
            {
                case WireType.Varint:
                    ValueOrEOF(TryReadVarintInt32());
                    break;
                case WireType.Fixed32:
                    SkipBytes(4);
                    break;
                case WireType.Fixed64:
                    SkipBytes(8);
                    break;
                case WireType.String:
                    SkipBytes(ValueOrEOF(TryReadVarintInt32()));
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        public override ValueTask<double> ReadDoubleAsync() => AsTask(ReadDouble());
        public double ReadDouble()
        {
            switch (WireType)
            {
                case WireType.Fixed32:
                    return ToSingle(ReadFixedUInt32());
                case WireType.Fixed64:
                    return ToDouble(ReadFixedUInt64());
                default:
                    throw new InvalidOperationException();
            }
        }
        public override ValueTask<float> ReadSingleAsync() => AsTask(ReadSingle());
        public float ReadSingle()
        {
            switch (WireType)
            {
                case WireType.Fixed32:
                    return ToSingle(ReadFixedUInt32());
                case WireType.Fixed64:
                    return (float)ToDouble(ReadFixedUInt64());
                default:
                    throw new InvalidOperationException();
            }
        }
        public override ValueTask<SubObjectToken> BeginSubObjectAsync() => AsTask(BeginSubObject());
        public SubObjectToken BeginSubObject()
        {
            switch (WireType)
            {
                case WireType.String:
                    int len = ValueOrEOF(TryReadVarintInt32());
                    return IssueSubObjectToken(len);
                default:
                    throw new InvalidOperationException();
            }
        }

        protected abstract uint ReadFixedUInt32();
        protected abstract ulong ReadFixedUInt64();
        protected abstract void SkipBytes(int bytes);
        protected abstract byte[] ReadBytes(int bytes);
        protected abstract string ReadString(int bytes);
        protected abstract int? TryReadVarintInt32(bool consume = true);
    }
}
