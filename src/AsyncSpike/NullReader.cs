using System.Threading.Tasks;

namespace ProtoBuf
{
    internal sealed class NullReader : SyncProtoReader
    {

        protected override int? TryReadVarintInt32(bool consume) => null;

        protected override string ReadString(int bytes) => ThrowEOF<string>();

        protected override byte[] ReadBytes(int bytes) => ThrowEOF<byte[]>();
        protected override uint ReadFixedUInt32() => ThrowEOF<uint>();
        protected override ulong ReadFixedUInt64() => ThrowEOF<ulong>();
        protected override void ApplyDataConstraint() { }
        protected override void RemoveDataConstraint() { }
        protected override void SkipBytes(int bytes) => ThrowEOF();
    }
}
