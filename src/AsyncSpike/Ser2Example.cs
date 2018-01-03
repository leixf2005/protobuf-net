using System.Buffers;

namespace ProtoBuf
{
    class Ser2Example : ISer2<Order>, ISer2<Customer>
    {
        public static Ser2Example Instance { get; } = new Ser2Example();
        private Ser2Example() { }

        v2Result ISer2<Customer>.Deserialize(ref BufferReader reader, ref Customer value) => Deserialize(ref reader, ref value);
        v2Result ISer2<Order>.Deserialize(ref BufferReader reader, ref Order value) => Deserialize(ref reader, ref value);

        private static v2Result Deserialize(ref BufferReader reader, ref Order value)
        {
            while (true)
            {
                // note we effectively snapshot reader; we're going to play with current,
                // and *if* we succeed, we'll then update our snapshot
                var current = reader;
                ulong? fieldHeader = current.TryReadVarint();
                if (fieldHeader == null)
                {
                    return v2Result.Success;
                }
                var wireType = (WireType)(int)(fieldHeader.GetValueOrDefault() & 7L);

                switch (fieldHeader.GetValueOrDefault() >> 3)
                {
                    default:
                        if (!current.TrySkipField(wireType)) return v2Result.Success;
                        break;
                }
                // we successfully read that field, so update the caller
                reader = current;
            }
        }

        private static v2Result Deserialize(ref BufferReader reader, ref Customer value)
        {
            Customer Create(ref Customer obj) => obj ?? (obj = new Customer());

            while (true)
            {
                // note we effectively snapshot reader; we're going to play with current,
                // and *if* we succeed, we'll then update our snapshot
                var current = reader;
                ulong? fieldHeader = current.TryReadVarint();
                if (fieldHeader == null)
                {
                    return v2Result.Success;
                }
                var wireType = (WireType)(int)(fieldHeader.GetValueOrDefault() & 7L);

                switch (fieldHeader.GetValueOrDefault() >> 3)
                {
                    case 1:
                        var _1 = current.TryReadInt32(wireType);
                        if (_1 == null) return v2Result.Success;
                        Create(ref value).Id = _1.GetValueOrDefault();
                        break;
                    case 2:
                        var _2 = current.TryReadString(wireType);
                        if (_2 == null) return v2Result.Success;
                        Create(ref value).Name = _2;
                        break;
                    case 3:
                        var _3 = current.TryReadString(wireType);
                        if (_3 == null) return v2Result.Success;
                        Create(ref value).Notes = _3;
                        break;
                    case 4:
                        var _4 = current.TryReadDouble(wireType);
                        if (_4 == null) return v2Result.Success;
                        Create(ref value).MarketValue = _4.GetValueOrDefault();
                        break;
                    default:
                        if (!current.TrySkipField(wireType)) return v2Result.Success;
                        break;
                }
                // we successfully read that field, so update the caller
                reader = current;
            }

        }
    }
}