//using System;
//using System.Buffers;
//using System.Threading.Tasks;

//namespace ProtoBuf
//{
//    class Ser2Example : ISer2<Order>, ISer2<Customer>
//    {
//        public static Ser2Example Instance { get; } = new Ser2Example();
//        private Ser2Example() { }

//        v2Result ISer2<Customer>.Deserialize(ref  BufferReader<ReadOnlyBuffer> reader, ref Customer value) => Deserialize(ref reader, ref value);
//        v2Result ISer2<Order>.Deserialize(ref  BufferReader<ReadOnlyBuffer> reader, ref Order value) => Deserialize(ref reader, ref value);
//        ValueTask<Order> ISer2<Order>.DeserializeAsync(System.IO.Pipelines.IPipeReader reader, Order value)
//            => throw new NotSupportedException();

//        ValueTask<Customer> ISer2<Customer>.DeserializeAsync(System.IO.Pipelines.IPipeReader reader, Customer value)
//        {
//            throw new NotImplementedException();
//        }



//        private static v2Result Deserialize(ref  BufferReader<ReadOnlyBuffer> reader, ref Order value)
//        {
//            Order Create(ref Order obj) => obj ?? (obj = new Order());

//            while (true)
//            {
//                // note we effectively snapshot reader; we're going to play with current,
//                // and *if* we succeed, we'll then update our snapshot
//                var current = reader;
//                (var fieldNumber, var wireType) = current.ReadNextField();
//                if (fieldNumber <= 0)
//                {
//                    Create(ref value);
//                    return v2Result.Success;
//                }

//                switch (fieldNumber)
//                {

//                    case 1:
//                        var _1 = current.TryReadInt32(wireType);
//                        if (_1 == null) return v2Result.Success;
//                        Create(ref value).Id = _1.GetValueOrDefault();
//                        break;
//                    case 2:
//                        var _2 = current.TryReadString(wireType);
//                        if (_2 == null) return v2Result.Success;
//                        Create(ref value).ProductCode = _2;
//                        break;
//                    case 3:
//                        var _3 = current.TryReadInt32(wireType);
//                        if (_3 == null) return v2Result.Success;
//                        Create(ref value).Quantity = _3.GetValueOrDefault();
//                        break;
//                    case 4:
//                        var _4 = current.TryReadDouble(wireType);
//                        if (_4 == null) return v2Result.Success;
//                        Create(ref value).UnitPrice = _4.GetValueOrDefault();
//                        break;
//                    case 5:
//                        var _5 = current.TryReadString(wireType);
//                        if (_5 == null) return v2Result.Success;
//                        Create(ref value).Notes = _5;
//                        break;
//                    default:
//                        if (!current.TrySkipField(wireType)) return wireType.DefaultResult();
//                        break;
//                }
//                // we successfully read that field, so update the caller
//                reader = current;
//            }
//        }

//        private static v2Result Deserialize(ref  BufferReader<ReadOnlyBuffer> reader, ref Customer value)
//        {
//            Customer Create(ref Customer obj) => obj ?? (obj = new Customer());

//            while (true)
//            {
//                // note we effectively snapshot reader; we're going to play with current,
//                // and *if* we succeed, we'll then update our snapshot
//                var current = reader;
//                (var fieldNumber, var wireType)= current.ReadNextField();
//                if(fieldNumber <= 0)
//                {
//                    Create(ref value);
//                    return v2Result.Success;
//                }
                
//                switch (fieldNumber)
//                {
//                    case 1:
//                        var _1 = current.TryReadInt32(wireType);
//                        if (_1 == null) return v2Result.Success;
//                        Create(ref value).Id = _1.GetValueOrDefault();
//                        break;
//                    case 2:
//                        var _2 = current.TryReadString(wireType);
//                        if (_2 == null) return v2Result.Success;
//                        Create(ref value).Name = _2;
//                        break;
//                    case 3:
//                        var _3 = current.TryReadString(wireType);
//                        if (_3 == null) return v2Result.Success;
//                        Create(ref value).Notes = _3;
//                        break;
//                    case 4:
//                        var _4 = current.TryReadDouble(wireType);
//                        if (_4 == null) return v2Result.Success;
//                        Create(ref value).MarketValue = _4.GetValueOrDefault();
//                        break;
//                    case 5:
//                        var _5 = Create(ref value).Orders;
//                        do
//                        {
//                            var x = current.HasEntireSubObject(wireType);
//                            if (!x.Result) return v2Result.SwitchToAsync;
//                            Order newObj = null;
//                            // create a reader over just that inner slice
//                            var innerReader = current.Slice(0, x.Bytes);
//                            var status = Deserialize(ref innerReader, ref newObj);
//                            if (status != v2Result.Success) return status;

//                            if (!innerReader.End) throw new Exception($"sub-object not fully consumed; {(x.Bytes-innerReader.ConsumedBytes)} remain");

//                            reader = current; // update after every successful sub-item
//                            _5.Add(newObj);

//                            // see if we have another of the same field
//                            (fieldNumber, wireType) = current.ReadNextField();
//                        } while (fieldNumber == 5); // note that we discard "current" after this in the case of failure, so no harm
//                        break;
//                    default:
//                        if (!current.TrySkipField(wireType)) return wireType.DefaultResult();
//                        break;
//                }
//                // we successfully read that field, so update the caller
//                reader = current;
//            }
//        }
//    }
//}