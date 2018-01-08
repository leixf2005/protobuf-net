using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ProtoBuf
{

    //public interface IAsyncSerializer<T>
    //{
    //    ValueTask<T> DeserializeAsync(AsyncProtoReader reader, T value);
    //}
    //public interface ISyncSerializer<T> : IAsyncSerializer<T>
    //{
    //    T Deserialize(SyncProtoReader reader, T value);
    //}
    //public static class SerializerExtensions
    //{
    //    public static T Deserialize<T>(this ISyncSerializer<T> serializer, Memory<byte> buffer, bool useNewTextEncoder, T value = default(T))
    //    {
    //        using (var reader = AsyncProtoReader.Create(buffer, useNewTextEncoder))
    //        {
    //            return serializer.Deserialize(reader, value);
    //        }
    //    }

    //    public static ValueTask<T> DeserializeAsync<T>(this IAsyncSerializer<T> serializer, Memory<byte> buffer, bool useNewTextEncoder, T value = default(T), bool preferSync = true)
    //    {
    //        async ValueTask<T> AwaitAndDispose(AsyncProtoReader reader, ValueTask<T> task)
    //        {
    //            using (reader) { return await task; }
    //        }
    //        {
    //            SyncProtoReader reader = null;
    //            try
    //            {
    //                reader = AsyncProtoReader.Create(buffer, useNewTextEncoder, preferSync);
    //                if (reader.PreferSync && serializer is ISyncSerializer<T> sync)
    //                {
    //                    return new ValueTask<T>(sync.Deserialize(reader, value));
    //                }
    //                else
    //                {
    //                    var task = serializer.DeserializeAsync(reader, value);
    //                    if (!task.IsCompleted)
    //                    {
    //                        var awaited = AwaitAndDispose(reader, task);
    //                        reader = null; // avoid disposal
    //                    }
    //                    return new ValueTask<T>(task.Result);
    //                }
    //            }
    //            finally
    //            {
    //                if (reader != null) reader.Dispose();
    //            }
    //        }
    //    }

    //    internal static ValueTask<T> DeserializeAsync<T>(IAsyncSerializer<T> serializer, AsyncProtoReader reader, T value = default)
    //    {
    //        return serializer is ISyncSerializer<T> syncSerializer
    //            && reader is SyncProtoReader syncReader
    //            ? new ValueTask<T>(syncSerializer.Deserialize(syncReader, value))
    //            : serializer.DeserializeAsync(reader, value);
    //    }
    //}
    //public sealed class CustomSerializer : ISyncSerializer<Customer>, ISyncSerializer<Order>
    //{
    //    private CustomSerializer() { }
    //    public static CustomSerializer Instance = new CustomSerializer();

    //    ValueTask<Customer> IAsyncSerializer<Customer>.DeserializeAsync(AsyncProtoReader reader, Customer value)
    //    {
    //        return (reader is SyncProtoReader sync && sync.PreferSync)
    //            ? new ValueTask<Customer>(DeserializeCustomerSync(sync, value))
    //            : DeserializeCustomerAsync(reader, value);
    //    }
    //    Customer ISyncSerializer<Customer>.Deserialize(SyncProtoReader reader, Customer value) => DeserializeCustomerSync(reader, value);

    //    static async ValueTask<Customer> DeserializeCustomerAsync(AsyncProtoReader reader, Customer value)
    //    {
    //        var id = value?.Id ?? 0;
    //        var name = value?.Name ?? null;
    //        var notes = value?.Notes ?? null;
    //        var marketValue = value?.MarketValue ?? 0.0;
    //        var orders = value?.Orders ?? null;

    //        while (await reader.ReadNextFieldAsync())
    //        {
    //            switch (reader.FieldNumber)
    //            {
    //                case 1:
    //                    id = await reader.ReadInt32Async();
    //                    break;
    //                case 2:
    //                    name = await reader.ReadStringAsync();
    //                    break;
    //                case 3:
    //                    notes = await reader.ReadStringAsync();
    //                    break;
    //                case 4:
    //                    marketValue = await reader.ReadDoubleAsync();
    //                    break;
    //                case 5:
    //                    if (orders == null)
    //                    {
    //                        if (value == null) value = new Customer();
    //                        orders = value.Orders;
    //                    }
    //                    do
    //                    {
    //                        orders.Add(await reader.ReadSubMessageAsync<Order>(Instance));
    //                    } while (await reader.AssertNextFieldAsync(5));
    //                    break;
    //                default:
    //                    await reader.SkipFieldAsync();
    //                    break;
    //            }
    //        }
    //        if (value == null) value = new Customer();
    //        value.Id = id;
    //        value.Name = name;
    //        value.Notes = notes;
    //        value.MarketValue = marketValue;
    //        // no Orders setter
    //        return value;
    //    }
    //    static Customer DeserializeCustomerSync(SyncProtoReader reader, Customer value)
    //    {
    //        var id = value?.Id ?? 0;
    //        var name = value?.Name ?? null;
    //        var notes = value?.Notes ?? null;
    //        var marketValue = value?.MarketValue ?? 0.0;
    //        var orders = value?.Orders ?? null;

    //        while (reader.ReadNextField())
    //        {
    //            switch (reader.FieldNumber)
    //            {
    //                case 1:
    //                    id = reader.ReadInt32();
    //                    break;
    //                case 2:
    //                    name = reader.ReadString();
    //                    break;
    //                case 3:
    //                    notes = reader.ReadString();
    //                    break;
    //                case 4:
    //                    marketValue = reader.ReadDouble();
    //                    break;
    //                case 5:
    //                    if (orders == null)
    //                    {
    //                        if (value == null) value = new Customer();
    //                        orders = value.Orders;
    //                    }
    //                    do
    //                    {
    //                        orders.Add(reader.ReadSubMessage<Order>(Instance));
    //                    } while (reader.AssertNextField(5));
    //                    break;
    //                default:
    //                    reader.SkipField();
    //                    break;
    //            }
    //        }
    //        if (value == null) value = new Customer();
    //        value.Id = id;
    //        value.Name = name;
    //        value.Notes = notes;
    //        value.MarketValue = marketValue;
    //        // no Orders setter
    //        return value;
    //    }
    //    ValueTask<Order> IAsyncSerializer<Order>.DeserializeAsync(AsyncProtoReader reader, Order value)
    //    {
    //        return (reader is SyncProtoReader sync && sync.PreferSync)
    //            ? new ValueTask<Order>(DeserializeOrderSync(sync, value))
    //            : DeserializeOrderAsync(reader, value);
    //    }

    //    static async ValueTask<Order> DeserializeOrderAsync(AsyncProtoReader reader, Order value)
    //    {
    //        int id = 0;
    //        string productCode = null;
    //        int quantity = 0;
    //        double unitPrice = 0.0;
    //        string notes = null;

    //        while (await reader.ReadNextFieldAsync())
    //        {
    //            switch (reader.FieldNumber)
    //            {
    //                case 1:
    //                    id = await reader.ReadInt32Async();
    //                    break;
    //                case 2:
    //                    productCode = await reader.ReadStringAsync();
    //                    break;
    //                case 3:
    //                    quantity = await reader.ReadInt32Async();
    //                    break;
    //                case 4:
    //                    unitPrice = await reader.ReadDoubleAsync();
    //                    break;
    //                case 5:
    //                    notes = await reader.ReadStringAsync();
    //                    break;
    //                default:
    //                    await reader.SkipFieldAsync();
    //                    break;
    //            }
    //        }
    //        if (value == null) value = new Order();
    //        value.Id = id;
    //        value.ProductCode = productCode;
    //        value.Quantity = quantity;
    //        value.UnitPrice = unitPrice;
    //        value.Notes = notes;
    //        return value;
    //    }

    //    Order ISyncSerializer<Order>.Deserialize(SyncProtoReader reader, Order value) => DeserializeOrderSync(reader, value);

    //    static Order DeserializeOrderSync(SyncProtoReader reader, Order value)
    //    {
    //        int id = 0;
    //        string productCode = null;
    //        int quantity = 0;
    //        double unitPrice = 0.0;
    //        string notes = null;

    //        while (reader.ReadNextField())
    //        {
    //            switch (reader.FieldNumber)
    //            {
    //                case 1:
    //                    id = reader.ReadInt32();
    //                    break;
    //                case 2:
    //                    productCode = reader.ReadString();
    //                    break;
    //                case 3:
    //                    quantity = reader.ReadInt32();
    //                    break;
    //                case 4:
    //                    unitPrice = reader.ReadDouble();
    //                    break;
    //                case 5:
    //                    notes = reader.ReadString();
    //                    break;
    //                default:
    //                    reader.SkipField();
    //                    break;
    //            }
    //        }
    //        if (value == null) value = new Order();
    //        value.Id = id;
    //        value.ProductCode = productCode;
    //        value.Quantity = quantity;
    //        value.UnitPrice = unitPrice;
    //        value.Notes = notes;
    //        return value;
    //    }
    //}

    [ProtoContract]
    public class CustomerMagicWrapper // allows deserialization of multiple customer objects without retaining them
    {
        [ProtoMember(1)]
        public CustomerMagicCollection Items { get; } = new CustomerMagicCollection();
        static IEnumerable<Customer> None => Array.Empty<Customer>();

        public override string ToString() => Items.ToString();

        public class CustomerMagicCollection : IEnumerable<Customer>
        {
            public IEnumerator<Customer> GetEnumerator() => None.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public int CustomerCount { get; private set; }
            public int OrderCount { get; private set; }
            public void Add(Customer customer)
            {
                if (customer != null)
                {
                    CustomerCount++;
                    OrderCount += customer.Orders.Count;
                    if(customer.Orders.Count != 40)
                    {
                        throw new InvalidOperationException($"huh? customer '{customer?.Id}' has {customer?.Orders?.Count ?? -1} orders");
                    }
                }
#if DEBUG
                Checksum = (Checksum * -14315451) + (customer?.GetHashCode() ?? 0);
#endif
            }
            public override string ToString() => $"{CustomerCount} customers, {OrderCount} orders, chk: {Checksum}";
            public int Checksum { get; private set; }
        }
    }

    [ProtoContract]
    public class Customer
    {
        public override string ToString() => Id.ToString();
        public override int GetHashCode()
        {
            int hash = -42;
            hash = (hash * -123124) + Id.GetHashCode();
            hash = (hash * -123124) + (Name?.GetHashCode() ?? 0);
            hash = (hash * -123124) + (Notes?.GetHashCode() ?? 0);
            hash = (hash * -123124) + MarketValue.GetHashCode();
            hash = (hash * -123124) + Orders.Count.GetHashCode();
            foreach (var order in Orders)
            {
                hash = (hash * -123124) + (order?.GetHashCode() ?? 0);
            }
            return hash;
        }

        [ProtoMember(1)]
        public int Id { get; set; }

        [ProtoMember(2)]
        public string Name { get; set; }
        [ProtoMember(3)]
        public string Notes { get; set; }

        [ProtoMember(4)]
        public double MarketValue { get; set; }

        [ProtoMember(5)]
        public List<Order> Orders { get; } = new List<Order>();
    }
    [ProtoContract]
    public class Order
    {
        public override string ToString() => Id.ToString();
        public override int GetHashCode()
        {
            int hash = -42;
            hash = (hash * -123124) + Id.GetHashCode();
            hash = (hash * -123124) + (ProductCode?.GetHashCode() ?? 0);
            hash = (hash * -123124) + Quantity.GetHashCode();
            hash = (hash * -123124) + UnitPrice.GetHashCode();
            hash = (hash * -123124) + (Notes?.GetHashCode() ?? 0);
            return hash;
        }
        [ProtoMember(1)]
        public int Id { get; set; }
        [ProtoMember(2)]
        public string ProductCode { get; set; }
        [ProtoMember(3)]
        public int Quantity { get; set; }
        [ProtoMember(4)]
        public double UnitPrice { get; set; }
        [ProtoMember(5)]
        public string Notes { get; set; }
    }
}
