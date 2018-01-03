#define RUNACC

#if DEBUG
#define VERBOSE
#endif

using ProtoBuf;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Xunit;

public class SimpleUsage : IDisposable
{
    private PipeOptions _options = new PipeOptions(new MemoryPool());
    void IDisposable.Dispose() => _options?.Pool?.Dispose();

    static void SlicePerf(int LOOP, TextWriter log)
    {
        int Acc(ReadOnlySpan<byte> a) => a[0] ^ a[1]; // just something arbitrary

        byte[] data = new byte[64 * 1024];
        new Random(123).NextBytes(data);

        log?.WriteLine($"Comparing slice performance slicing {data.Length} bytes into {data.Length / 2} slices, {LOOP} times");

#if !RUNACC
        log?.WriteLine("The individual variant results only make sense if RUNACC is defined, which: is isn't -");
        log?.WriteLine("the interior code *didn't run at all*, so: just look at the overall timings in the categories");
#endif




        ReadOnlyMemory<byte> memory;
        ReadOnlySpan<byte> span;
        ReadOnlyBuffer rob;
        Stopwatch watch;
        int acc = 0;

        log?.WriteLine("");
        log?.WriteLine("Span<byte>");
        watch = Stopwatch.StartNew();
        for (int i = 0; i < LOOP; i++)
        {
            acc = 0;
            span = data;
            while (!span.IsEmpty)
            {
#if RUNACC
                acc ^= Acc(span.Slice(0, 2));
#endif
                span = span.Slice(2);
            }
        }
        watch.Stop();
        log?.WriteLine($"\t=>Slice: acc={acc}, {watch.ElapsedMilliseconds}ms");

        log?.WriteLine("");
        log?.WriteLine("Memory<byte>");
        watch = Stopwatch.StartNew();
        for (int i = 0; i < LOOP; i++)
        {
            acc = 0;
            memory = data;
            while (!memory.IsEmpty)
            {
#if RUNACC
                acc ^= Acc(memory.Span.Slice(0, 2));
#endif
                memory = memory.Slice(2);
            }
        }
        watch.Stop();
        log?.WriteLine($"\t=>Span=>Slice: acc={acc}, {watch.ElapsedMilliseconds}ms");

        watch = Stopwatch.StartNew();
        for (int i = 0; i < LOOP; i++)
        {
            acc = 0;
            memory = data;
            while (!memory.IsEmpty)
            {
#if RUNACC
                acc ^= Acc(memory.Slice(0, 2).Span);
#endif
                memory = memory.Slice(2);
            }
        }
        watch.Stop();
        log?.WriteLine($"\t=>Slice=>Span: acc={acc}, {watch.ElapsedMilliseconds}ms");

        rob = new ReadOnlyBuffer(data);
        log?.WriteLine("");        
        log?.WriteLine($"ReadOnlyBuffer, {nameof(rob.IsSingleSpan)}={rob.IsSingleSpan}");

        watch = Stopwatch.StartNew();
        for (int i = 0; i < LOOP; i++)
        {
            acc = 0;
            rob = new ReadOnlyBuffer(data);
            while (!rob.IsEmpty)
            {
#if RUNACC
                acc ^= Acc(rob.Slice(0, 2).First.Span);
#endif
                rob = rob.Slice(2);
            }
        }
        watch.Stop();
        log?.WriteLine($"\t=>Slice=>First=>Span: acc={acc}, {watch.ElapsedMilliseconds}ms");

        watch = Stopwatch.StartNew();
        for (int i = 0; i < LOOP; i++)
        {
            acc = 0;
            rob = new ReadOnlyBuffer(data);
            while (!rob.IsEmpty)
            {
#if RUNACC
                acc ^= Acc(rob.First.Slice(0, 2).Span);
#endif
                rob = rob.Slice(2);
            }
        }
        watch.Stop();
        log?.WriteLine($"\t=>First=>Slice=>Span: acc={acc}, {watch.ElapsedMilliseconds}ms");

        watch = Stopwatch.StartNew();
        for (int i = 0; i < LOOP; i++)
        {
            acc = 0;
            rob = new ReadOnlyBuffer(data);
            while (!rob.IsEmpty)
            {
#if RUNACC
                acc ^= Acc(rob.First.Span.Slice(0, 2));
#endif
                rob = rob.Slice(2);
            }
        }
        watch.Stop();
        log?.WriteLine($"\t=>First=>Span=>Slice: acc={acc}, {watch.ElapsedMilliseconds}ms");


        rob = new ReadOnlyBuffer(data);
        var drob = new DoubleBufferedReadOnlyBuffer(rob);
        log?.WriteLine("");
        log?.WriteLine($"DoubleBufferedReadOnlyBuffer, {nameof(drob.IsSingleSpan)}={drob.IsSingleSpan}");

        watch = Stopwatch.StartNew();
        for (int i = 0; i < LOOP; i++)
        {
            acc = 0;
            drob = new DoubleBufferedReadOnlyBuffer(rob);
            while (!drob.IsEmpty)
            {
#if RUNACC
                acc ^= Acc(drob.First.Slice(0, 2).Span);
#endif
                drob = drob.Slice(2);
            }
        }
        watch.Stop();
        log?.WriteLine($"\t=>First=>Slice=>Span: acc={acc}, {watch.ElapsedMilliseconds}ms");

        watch = Stopwatch.StartNew();
        for (int i = 0; i < LOOP; i++)
        {
            acc = 0;
            drob = new DoubleBufferedReadOnlyBuffer(rob);
            while (!drob.IsEmpty)
            {
#if RUNACC
                acc ^= Acc(drob.First.Span.Slice(0, 2));
#endif
                drob = drob.Slice(2);
            }
        }
        watch.Stop();
        log?.WriteLine($"\t=>First=>Span=>Slice: acc={acc}, {watch.ElapsedMilliseconds}ms");


    }
    static async Task<int> Main()
    {
        try
        {
            // SlicePerf(10, null);
            // SlicePerf(10000, Console.Out);
            // await Execute();
            await ExecuteV2();
            return 0;
        }
        catch (Exception ex)
        {
            var orig = ex;
            await Console.Error.WriteLineAsync();
            while (ex != null)
            {
                await Console.Error.WriteLineAsync($"{ex.GetType().Name}: {ex.Message}");
                ex = ex.InnerException;
            }
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync(orig.StackTrace);
            return 1;
        }
        finally { }
    }
    static async Task ExecuteV2()
    {
        Console.WriteLine($"Preparing data, warming up (JIT), and validating output (see [checksum])");
        var rand = new Random(1234);
        var customer = InventCustomer(rand);
        await DescribeAsync(new ValueTask<Customer>(customer), "original");

        ArraySegment<byte> range;
        using (var ms = new MemoryStream())
        {
            Serializer.Serialize(ms, customer);
            if (!ms.TryGetBuffer(out range)) range = ms.ToArray();

            ms.Position = 0;

            await DescribeAsync(new ValueTask<Customer>(Serializer.Deserialize<Customer>(ms)), "protobuf-net");

            var options = new PipeOptions(new MemoryPool());

            Console.WriteLine("v2 deserialize...");

            var reader = await CreateIPipeReader(range);
            await DescribeAsync(Ser2Example.Instance.DeserializeAsync<Customer>(reader), "v2 async");

            var buffer = new ReadOnlyBuffer(range.Array, range.Offset, range.Count);
            await DescribeAsync(new ValueTask<Customer>(Ser2Example.Instance.Deserialize<Customer>(ref buffer)), "v2 sync");
            await DescribeAsync(new ValueTask<Customer>(Ser2Example.Instance.Deserialize<Customer>(ref buffer)), "v2 sync again");

            const int LOOP = 5000;
            Console.WriteLine($"Deserializing {ms.Length} bytes, {LOOP} times");
            var watch = Stopwatch.StartNew();
            for(int i = 0; i< LOOP; i++)
            {
                ms.Position = 0;
                GC.KeepAlive(Serializer.Deserialize<Customer>(ms));
            }
            watch.Stop();
            
            Console.WriteLine($"protobuf-net current: {watch.ElapsedMilliseconds}ms");
            
            watch = Stopwatch.StartNew();
            for (int i = 0; i < LOOP; i++)
            {
                GC.KeepAlive(Ser2Example.Instance.Deserialize<Customer>(ref buffer));
            }
            watch.Stop();

            Console.WriteLine($"via BufferReader API: {watch.ElapsedMilliseconds}ms");
        }
    }
    static async Task Execute()
    {
        var rand = new Random(1234);
        var customer = InventCustomer(rand);
        using (var ms = new MemoryStream())
        {

            Console.WriteLine("(the number [in square brackets] will change between runs)");
            await DescribeAsync(new ValueTask<Customer>(customer), "original");

            Serializer.Serialize(ms, customer);
            Console.WriteLine($"serialized: {ms.Length} bytes");

            ms.Position = 0;
            var clone = Serializer.Deserialize<Customer>(ms);
            await DescribeAsync(new ValueTask<Customer>(clone), "old code, old encoder");

            if (!ms.TryGetBuffer(out var range))
            {
                range = new ArraySegment<byte>(ms.ToArray());
            }
            Memory<byte> buffer = range;

            var task = SerializerExtensions.DeserializeAsync<Customer>(CustomSerializer.Instance, buffer, false, preferSync: false);
            await DescribeAsync(task, "new code, old encoder (prefer async)");

            task = SerializerExtensions.DeserializeAsync<Customer>(CustomSerializer.Instance, buffer, false, preferSync: true);
            await DescribeAsync(task, "new code, old encoder (prefer sync)");

            using (var reader = await CreatePipeReader(range))
            {
                PipeReader.ResetPeekCounts();
                task = SerializerExtensions.DeserializeAsync<Customer>(CustomSerializer.Instance, reader);
                var typed = reader as PipeReader;
                await DescribeAsync(task, $"new code, old encoder (pipe); {typed?.ReadCount} reads, {typed?.PeekCount} peeks, {PipeReader.SingleSpanPeek} single, {PipeReader.MultiSpanPeek} multi");
            }

            //task = SerializerExtensions.DeserializeAsync<Customer>(CustomSerializer.Instance, buffer, true);
            //if (task.IsCompleted)
            //{
            //    Console.WriteLine("completed!");
            //    Describe(task.Result, "new code, new encoder");
            //}
            //else
            //{
            //    Console.WriteLine("incomplete");
            //}

            const int LOOP = 50000;
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < LOOP; i++)
            {
                ms.Position = 0;
                GC.KeepAlive(Serializer.Deserialize<Customer>(ms));
            }
            watch.Stop();
            Console.WriteLine($"old sync code, old encoder: {watch.ElapsedMilliseconds}ms");

            watch = Stopwatch.StartNew();
            for (int i = 0; i < LOOP; i++)
            {
                GC.KeepAlive(await SerializerExtensions.DeserializeAsync<Customer>(CustomSerializer.Instance, buffer, false, preferSync: false));
            }
            watch.Stop();
            Console.WriteLine($"new async code, old encoder (prefer async): {watch.ElapsedMilliseconds}ms");

            watch = Stopwatch.StartNew();
            for (int i = 0; i < LOOP; i++)
            {
                GC.KeepAlive(await SerializerExtensions.DeserializeAsync<Customer>(CustomSerializer.Instance, buffer, false, preferSync: true));
            }
            watch.Stop();
            Console.WriteLine($"new async code, old encoder (prefer sync): {watch.ElapsedMilliseconds}ms");


            watch = Stopwatch.StartNew();
            var pipes = new PipeReader[LOOP];
            for(int i = 0; i < pipes.Length; i++)
            {
                pipes[i] = await CreatePipeReader(range);
            }
            watch.Stop();
            Console.WriteLine($"preparing {LOOP} pipes: {watch.ElapsedMilliseconds}ms");

            watch = Stopwatch.StartNew();
            for (int i = 0; i < LOOP; i++)
            {
                using (var reader = pipes[i])
                {
                    GC.KeepAlive(await SerializerExtensions.DeserializeAsync<Customer>(CustomSerializer.Instance, reader));
                }
            }
            watch.Stop();
            Console.WriteLine($"new async code, old encoder (pipe): {watch.ElapsedMilliseconds}ms");

            pipes = null;

            //watch = Stopwatch.StartNew();
            //for (int i = 0; i < LOOP; i++)
            //{
            //    GC.KeepAlive(SerializerExtensions.DeserializeAsync<Customer>(CustomSerializer.Instance, buffer, true).Result);
            //}
            //watch.Stop();
            //Console.WriteLine($"new async code, new encoder: {watch.ElapsedMilliseconds}ms");
        }
    }

    private static async ValueTask<IPipeReader> CreateIPipeReader(ArraySegment<byte> range)
    {
        var pipe = await CreatePipe(range);
        return pipe.Reader;
    }
    private static async ValueTask<PipeReader> CreatePipeReader(ArraySegment<byte> range)
    {
        var pipe = await CreatePipe(range);
        return new PipeReader(pipe.Reader, true);
    }

    static readonly PipeOptions _pipeConfig = new PipeOptions(new MemoryPool());
    private static async ValueTask<Pipe> CreatePipe(ArraySegment<byte> range)
    {
        var ms = new MemoryStream(range.Array, range.Offset, range.Count);
        var pipe = new Pipe(_pipeConfig);
        await pipe.WriteAsync(range);
        pipe.Writer.Complete();
        return pipe;
    }

    private static async Task DescribeAsync(ValueTask<Customer> task, string label)
    {
        var suffix = task.IsCompleted ? "completed" : "awaited";
        var customer = task.IsCompleted ? task.Result : await task;
        if (customer == null)
        {
            await Console.Out.WriteLineAsync($"{label}\t(null) {suffix}");
        }
        else
        {
            await Console.Out.WriteLineAsync($"{label}\t{customer.Id}: {customer.Orders?.Count ?? -1} [{customer.GetHashCode()}] - {suffix}; {customer.Name}");
        }
    }

    private static Customer InventCustomer(Random rand)
    {
        var c = new Customer
        {
            Id = rand.Next(100000),
            MarketValue = rand.NextDouble() * 80000,
            Name = CreateString(rand, 5, 20),
            Notes = CreateString(rand, 0, 250),
        };
        int orders = rand.Next(50);
        for (int i = 0; i < orders; i++)
            c.Orders.Add(InventOrder(rand));
        return c;
    }
    private static Order InventOrder(Random rand)
    {
        var o = new Order
        {
            Id = rand.Next(100000),
            Notes = CreateString(rand, 10, 200),
            ProductCode = CreateString(rand, 6, 6),
            Quantity = rand.Next(50),
            UnitPrice = rand.NextDouble() * 100
        };
        return o;
    }

    static readonly string Alphabet = new string(Enumerable.Range(32, 223).Select(i => (char)i).ToArray());
    private static unsafe string CreateString(Random rand, int minLen, int maxLen)
    {
        int len = rand.Next(minLen, maxLen + 1);
        if (len == 0) return "";
        char* c = stackalloc char[len];
        for (int i = 0; i < len; i++)
            c[i] = Alphabet[rand.Next(0, Alphabet.Length)];
        return new string(c, 0, len);
    }

    static void Main2()
    {
        try
        {
            Console.WriteLine("Running...");
            using (var rig = new SimpleUsage())
            {
                rig.RunGoogleTests().Wait();
            }
        }
        catch (Exception ex)
        {
            while (ex != null)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                Console.Error.WriteLine();
                ex = ex.InnerException;
            }

        }
    }
    // see example in: https://developers.google.com/protocol-buffers/docs/encoding
    public async Task RunGoogleTests()
    {
        await Console.Out.WriteLineAsync(nameof(ReadTest1));
        await ReadTest1();

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync(nameof(ReadTest2));
        await ReadTest2();

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync(nameof(ReadTest3));
        await ReadTest3();

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync(nameof(WriteTest1));
        await WriteTest1();

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync(nameof(WriteTest2));
        await WriteTest2();

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync(nameof(WriteTest3));
        await WriteTest3();

    }

    [Xunit.Fact]
    public Task ReadTest1() => ReadTest<Test1>("08 96 01", DeserializeTest1Async, "A: 150");

    [Xunit.Fact]
    public Task WriteTest1() => WriteTest(new Test1 { A = 150 }, "08 96 01", SerializeTest1Async);

    [Xunit.Fact]
    public Task ReadTest2() => ReadTest<Test2>("12 07 74 65 73 74 69 6e 67", DeserializeTest2Async, "B: testing");

    [Xunit.Fact]
    public Task WriteTest2() => WriteTest(new Test2 { B = "testing" }, "12 07 74 65 73 74 69 6e 67", SerializeTest2Async);


    // note I've suffixed with another dummy "1" field to test the end sub-object code
    [Xunit.Fact]
    public Task ReadTest3() => ReadTest<Test3>("1a 03 08 96 01 08 96 01", DeserializeTest3Async, "C: [A: 150]");

    [Xunit.Fact]
    public Task WriteTest3() => WriteTest(new Test3 { C = new Test1 { A = 150 } }, "1a 03 08 96 01", SerializeTest3Async);

    class Test1
    {
        public int A;
        public override string ToString() => $"A: {A}";
    }

    class Test2
    {
        public string B { get; set; }
        public override string ToString() => $"B: {B}";
    }

    class Test3
    {
        public Test1 C { get; set; }
        public override string ToString() => $"C: [{C}]";
    }


    [Conditional("VERBOSE")]
    public static void Trace(string message)
    {
#if VERBOSE
        Console.WriteLine(message);
#endif
    }

    // note: this code would be spat out my the roslyn generator API
    async ValueTask<Test1> DeserializeTest1Async(
        AsyncProtoReader reader, Test1 value = default(Test1))
    {
        Trace($"Reading {nameof(Test1)} fields...");
        while (await reader.ReadNextFieldAsync())
        {
            Trace($"Reading {nameof(Test1)} field {reader.FieldNumber}...");
            switch (reader.FieldNumber)
            {
                case 1:
                    (value ?? Create(ref value)).A = await reader.ReadInt32Async();
                    break;
                default:
                    await reader.SkipFieldAsync();
                    break;
            }
            Trace($"Reading next {nameof(Test1)} field...");
        }
        Trace($"Reading {nameof(Test1)} fields complete");
        return value ?? Create(ref value);
    }

    async ValueTask<long> SerializeTest1Async(AsyncProtoWriter writer, Test1 value)
    {
        long bytes = 0;
        if (value != null)
        {
            Trace($"Writing {nameof(Test1)} fields...");
            bytes += await writer.WriteVarintInt32Async(1, value.A);
            Trace($"Writing {nameof(Test1)} field complete");
        }
        return bytes;
    }
    async ValueTask<long> SerializeTest2Async(AsyncProtoWriter writer, Test2 value)
    {
        long bytes = 0;
        if (value != null)
        {
            Trace($"Writing {nameof(Test2)} fields...");
            bytes += await writer.WriteStringAsync(2, value.B);
            Trace($"Writing {nameof(Test2)} field complete");
        }
        return bytes;
    }

    async ValueTask<long> SerializeTest3Async(AsyncProtoWriter writer, Test3 value)
    {
        long bytes = 0;
        if (value != null)
        {
            Trace($"Writing {nameof(Test3)} fields...");
            bytes += await writer.WriteSubObject(3, value.C, (α, β) => SerializeTest1Async(α, β));
            Trace($"Writing {nameof(Test3)} field complete");
        }
        return bytes;
    }

    private async Task ReadTest<T>(string hex, Func<AsyncProtoReader, T, ValueTask<T>> deserializer, string expected)
    {
        await ReadTestPipe<T>(hex, deserializer, expected);
        await ReadTestBuffer<T>(hex, deserializer, expected);
    }
    private async Task ReadTestPipe<T>(string hex, Func<AsyncProtoReader, T, ValueTask<T>> deserializer, string expected)
    {
        var pipe = new Pipe(_options);
        await AppendPayloadAsync(pipe, hex);
        pipe.Writer.Complete(); // simulate EOF

        Trace($"deserializing via {nameof(PipeReader)}...");
        using (var reader = AsyncProtoReader.Create(pipe.Reader))
        {
            var obj = await deserializer(reader, default(T));
            string actual = obj?.ToString();
            await Console.Out.WriteLineAsync(actual);
            Assert.Equal(expected, actual);
        }
    }
    private async Task ReadTestBuffer<T>(string hex, Func<AsyncProtoReader, T, ValueTask<T>> deserializer, string expected)
    {
        var blob = ParseBlob(hex);
        Trace($"deserializing via {nameof(MemoryReader)}...");
        using (var reader = AsyncProtoReader.Create(blob, true))
        {
            var obj = await deserializer(reader, default(T));
            string actual = obj?.ToString();
            await Console.Out.WriteLineAsync(actual);
            Assert.Equal(expected, actual);
        }
    }

    private async Task WriteTest<T>(T value, string expected, Func<AsyncProtoWriter, T, ValueTask<long>> serializer)
    {
        long len = await WriteTestPipe(value, expected, serializer);
        await WriteTestSpan(len, value, expected, serializer);
    }
    private async ValueTask<long> WriteTestPipe<T>(T value, string expected, Func<AsyncProtoWriter, T, ValueTask<long>> serializer)
    {
        long bytes;
        string actual;
        if (value == null)
        {
            bytes = 0;
            actual = "";
        }
        else
        {
            var pipe = new Pipe(_options);
            using (var writer = AsyncProtoWriter.Create(pipe.Writer))
            {
                bytes = await serializer(writer, value);
                await Console.Out.WriteLineAsync($"Serialized to pipe in {bytes} bytes");
                await writer.FlushAsync(true);
            }
            var blob = await ReadToEndBlobAsync(pipe.Reader);
            actual = NormalizeHex(BitConverter.ToString(blob));
        }
        expected = NormalizeHex(expected);
        Assert.Equal(expected, actual);
        return bytes;
    }

    public static async Task<byte[]> ReadToEndBlobAsync(IPipeReader input)
    {
        while (true)
        {
            // Wait for more data
            var result = await input.ReadAsync();

            if (result.IsCompleted)
            {
                // Read all the data, return it
                var tmp = result.Buffer.ToArray();
                // release from the source
                input.Advance(result.Buffer.End, result.Buffer.End);
            }
            else
            {
                // Don't advance the buffer so remains in buffer
                input.Advance(result.Buffer.Start, result.Buffer.End);
            }
        }
    }
    private async Task WriteTestSpan<T>(long bytes, T value, string expected, Func<AsyncProtoWriter, T, ValueTask<long>> serializer)
    {
        long nullBytes = await serializer(AsyncProtoWriter.Null, value);
        Trace($"Serialized to nil-writer in {nullBytes} bytes");
        Assert.Equal(bytes, nullBytes);

        string actual;
        if (value == null)
        {
            actual = "";
        }
        else
        {
            var blob = new byte[bytes];
            Trace($"Allocated span of length {blob.Length}");
            using (var writer = AsyncProtoWriter.Create(blob))
            {
                long newBytes = await serializer(writer, value);
                await Console.Out.WriteLineAsync($"Serialized to span in {newBytes} bytes");
                Assert.Equal(bytes, newBytes);
                await writer.FlushAsync(true);
            }
            actual = NormalizeHex(BitConverter.ToString(blob));
        }
        expected = NormalizeHex(expected);
        Assert.Equal(expected, actual);
    }

    // note: this code would be spat out my the roslyn generator API
    async ValueTask<Test2> DeserializeTest2Async(
        AsyncProtoReader reader, Test2 value = default(Test2))
    {
        Trace($"Reading {nameof(Test2)} fields...");
        while (await reader.ReadNextFieldAsync())
        {
            Trace($"Reading {nameof(Test2)} field {reader.FieldNumber}...");
            switch (reader.FieldNumber)
            {
                case 2:
                    (value ?? Create(ref value)).B = await reader.ReadStringAsync();
                    break;
                default:
                    await reader.SkipFieldAsync();
                    break;
            }
            Trace($"Reading next {nameof(Test2)} field...");
        }
        Trace($"Reading {nameof(Test2)} fields complete");
        return value ?? Create(ref value);
    }

    async ValueTask<Test3> DeserializeTest3Async(
        AsyncProtoReader reader, Test3 value = default(Test3))
    {
        Trace($"Reading {nameof(Test3)} fields...");
        while (await reader.ReadNextFieldAsync())
        {
            Trace($"Reading {nameof(Test3)} field {reader.FieldNumber}...");
            switch (reader.FieldNumber)
            {
                case 3:
                    var token = (await reader.BeginSubObjectAsync()).Token;
                    (value ?? Create(ref value)).C = await DeserializeTest1Async(reader, value?.C);
                    reader.EndSubObject(ref token);
                    break;
                default:
                    await reader.SkipFieldAsync();
                    break;
            }
            Trace($"Reading next {nameof(Test3)} field...");
        }
        Trace($"Reading {nameof(Test3)} fields complete");
        return value ?? Create(ref value);
    }

    static T Create<T>(ref T obj) where T : class, new() => obj ?? (obj = new T());





    public abstract class AsyncProtoWriter : IDisposable
    {
        public virtual Task FlushAsync(bool final) => Task.CompletedTask;
        public virtual void Dispose() { }
        public async ValueTask<int> WriteVarintInt32Async(int fieldNumber, int value) =>
            await WriteFieldHeader(fieldNumber, WireType.Varint) + await WriteVarintUInt64Async((ulong)(long)value);


        public virtual ValueTask<int> WriteBoolean(int fieldNumber, bool value)
            => WriteVarintInt32Async(fieldNumber, value ? 1 : 0);

        private ValueTask<int> WriteFieldHeader(int fieldNumber, WireType wireType) => WriteVarintUInt32Async((uint)((fieldNumber << 3) | (int)wireType));

        //public async ValueTask<int> WriteStringAsync(int fieldNumber, Utf8String value)
        //{
        //    return await WriteFieldHeader(fieldNumber, WireType.String)
        //        + await WriteVarintUInt32Async((uint)value.Length)
        //        + await WriteBytes(value.Bytes);
        //}
        public async ValueTask<int> WriteStringAsync(int fieldNumber, string value)
        {
            if (value == null) return 0;
            return await WriteFieldHeader(fieldNumber, WireType.String)
                + (value.Length == 0 ? await WriteVarintUInt32Async(0) : await WriteStringWithLengthPrefix(value));
        }
        protected static readonly Encoding Encoding = Encoding.UTF8;
        // protected static TextEncoder Encoder = TextEncoder.Utf8;

        //public async ValueTask<int> WriteBytesAsync(int fieldNumber, ReadOnlySpan<byte> value)
        //{
        //    int bytes = await WriteFieldHeader(fieldNumber, WireType.String) + await WriteVarintUInt32Async((uint)value.Length);
        //    if (value.Length != 0) bytes += await WriteBytes(value);
        //    return bytes;
        //}

        protected async virtual ValueTask<int> WriteStringWithLengthPrefix(string value)
        {
            byte[] bytes = Encoding.GetBytes(value); // cheap and nasty, but it works
            return await WriteVarintUInt32Async((uint)bytes.Length) + await WriteBytes(bytes);
        }
        protected abstract ValueTask<int> WriteBytes(ReadOnlySpan<byte> bytes);

        protected virtual ValueTask<int> WriteVarintUInt32Async(uint value) => WriteVarintUInt64Async(value);
        protected abstract ValueTask<int> WriteVarintUInt64Async(ulong value);

        internal virtual async ValueTask<long> WriteSubObject<T>(int fieldNumber, T value, Func<AsyncProtoWriter, T, ValueTask<long>> serializer)
        {
            if (value == null) return 0;
            long payloadLength = await serializer(Null, value);
            long prefixLength = await WriteFieldHeader(fieldNumber, WireType.String)
                + await WriteVarintUInt64Async((ulong)payloadLength);
            var bytesWritten = await serializer(this, value);
            Debug.Assert(bytesWritten == payloadLength, "Payload length mismatch in WriteSubObject");

            return prefixLength + payloadLength;
        }

        protected static int WriteVarintUInt32(Span<byte> span, uint value)
        {
            const uint SEVENBITS = 0x7F, CONTINUE = 0x80;

            // least significant group first
            int offset = 0;
            while ((value & ~SEVENBITS) != 0)
            {
                span[offset++] = (byte)((value & SEVENBITS) | CONTINUE);
                value >>= 7;
            }
            span[offset++] = (byte)value;
            return offset;
        }
        protected static int WriteVarintUInt64(Span<byte> span, ulong value)
        {
            const ulong SEVENBITS = 0x7F, CONTINUE = 0x80;

            // least significant group first
            int offset = 0;
            while ((value & ~SEVENBITS) != 0)
            {
                span[offset++] = (byte)((value & SEVENBITS) | CONTINUE);
                value >>= 7;
            }
            span[offset++] = (byte)value;
            return offset;
        }
        public static AsyncProtoWriter Create(IPipeWriter writer, bool closePipe = true) => new PipeWriter(writer, closePipe);

        public static AsyncProtoWriter Create(Memory<byte> span) => new BufferWriter(span);

        /// <summary>
        /// Provides an AsyncProtoWriter that computes lengths without requiring backing storage
        /// </summary>
        public static readonly AsyncProtoWriter Null = new NullWriter();

        protected static int GetVarintLength(uint value)
        {
            int count = 0;
            do
            {
                count++;
                value >>= 7;
            }
            while (value != 0);
            return count;
        }
        protected static int GetVarintLength(ulong value)
        {
            int count = 0;
            do
            {
                count++;
                value >>= 7;
            }
            while (value != 0);
            return count;
        }
        sealed class NullWriter : AsyncProtoWriter
        {
            protected override ValueTask<int> WriteBytes(ReadOnlySpan<byte> bytes) => new ValueTask<int>(bytes.Length);

            protected override ValueTask<int> WriteStringWithLengthPrefix(string value)
            {
                int bytes = Encoding.GetByteCount(value);
                return new ValueTask<int>(GetVarintLength((uint)bytes) + bytes);
            }

            public override ValueTask<int> WriteBoolean(int fieldNumber, bool value)
                => new ValueTask<int>(GetVarintLength((uint)(fieldNumber << 3)) + 1);

            protected override ValueTask<int> WriteVarintUInt32Async(uint value)
                => new ValueTask<int>(GetVarintLength(value));

            protected override ValueTask<int> WriteVarintUInt64Async(ulong value)
                => new ValueTask<int>(GetVarintLength(value));

            internal async override ValueTask<long> WriteSubObject<T>(int fieldNumber, T value, Func<AsyncProtoWriter, T, ValueTask<long>> serializer)
            {
                if (value == null) return 0;
                long len = await serializer(this, value);
                return GetVarintLength((uint)(fieldNumber << 3)) + GetVarintLength((ulong)len) + len;
            }
        }
    }

    internal sealed class PipeWriter : AsyncProtoWriter
    {
        private IPipeWriter _writer;
        private WritableBuffer _output;
        private readonly bool _closePipe;
        private volatile bool _isFlushing;
        internal PipeWriter(IPipeWriter writer, bool closePipe = true)
        {
            _writer = writer;
            _closePipe = closePipe;
            _output = writer.Alloc();
        }

        public override async Task FlushAsync(bool final)
        {
            if (_isFlushing) throw new InvalidOperationException("already flushing");
            _isFlushing = true;
            Trace("Flushing...");
            var tmp = _output;
            _output = default(WritableBuffer);
            await tmp.FlushAsync();
            Trace("Flushed");
            _isFlushing = false;

            if (!final)
            {
                _output = _writer.Alloc();
            }
        }

        protected override ValueTask<int> WriteStringWithLengthPrefix(string value)
        {
            // can write up to 127 characters (if ASCII) in a single-byte prefix - and conveniently
            // 127 is handy for binary testing
            int bytes = ((value.Length & ~127) == 0) ? TryWriteShortStringWithLengthPrefix(value) : 0;
            if (bytes == 0)
            {
                bytes = WriteLongStringWithLengthPrefix(value);
            }
            return new ValueTask<int>(bytes);
        }
        int TryWriteShortStringWithLengthPrefix(string value)
        {
            // to encode without checking bytes, need 4 times length, plus 1 - the sneaky way
            _output.Ensure(Math.Min(128, value.Length << 2) | 1);
            var span = _output.Buffer.Span;
            int bytesWritten;
            if (TryEncode(value, span.Slice(1, Math.Max(127, span.Length - 1)), out bytesWritten)
                && (bytesWritten & ~127) == 0) // <= 127
            {
                Debug.Assert(bytesWritten <= 127, "Too many bytes written in TryWriteShortStringWithLengthPrefix");
                span[0] = (byte)bytesWritten++; // note the post-increment here to account for the prefix byte
                _output.Advance(bytesWritten);
                Trace($"Wrote '{value}' in {bytesWritten} bytes (including length prefix) without checking length first");
                return bytesWritten;
            }
            return 0; // failure (for example: we had a 90 character string, but it turned out to have non-trivial
            // unicode contents which meant that it took more than 127 bytes)
        }


        int WriteLongStringWithLengthPrefix(string value)
        {
            _output.Ensure(5);
            int maxPayloadBytes = value.Length << 2, maxHeaderBytes = GetVarintLength((uint)maxPayloadBytes), payloadBytes, headerBytes;
            if (_output.Buffer.Length <= (maxPayloadBytes + maxHeaderBytes) && maxHeaderBytes == GetVarintLength((uint)value.Length))
            {
                // that's handy - it all fits in the buffer, and the prefix-size is the same regardless of best/worst case
                var span = _output.Buffer.Span;
                bool success = TryEncode(value, span.Slice(maxHeaderBytes), out payloadBytes);
                Debug.Assert(success, "TryEncode failed in WriteLongStringWithLengthPrefix");
                Debug.Assert(payloadBytes <= maxPayloadBytes, "Payload exceeded expected size");

                headerBytes = WriteVarintUInt32(span, (uint)payloadBytes);
                Debug.Assert(headerBytes == maxHeaderBytes, "Header bytes was wrong size");

                _output.Advance(headerBytes + payloadBytes);
            }
            else
            {
                payloadBytes = Encoding.GetByteCount(value);
                headerBytes = WriteVarintUInt32(_output.Buffer.Span, (uint)payloadBytes);
                _output.Advance(headerBytes);
                int bytesWritten;
                if (payloadBytes <= _output.Buffer.Length)
                {
                    // already enough space in the output buffer - just write it
                    bool success = TryEncode(value, _output.Buffer.Span, out bytesWritten);
                    Debug.Assert(success, "TryEncode failed in WriteLongStringWithLengthPrefix");
                    Trace($"Wrote '{value}' in {bytesWritten} bytes into available buffer space");
                }
                else
                {
                    bytesWritten = WriteLongString(value, ref _output);
                }
                Debug.Assert(bytesWritten == payloadBytes, "Payload length mismatch in WriteLongStringWithLengthPrefix");
                _output.Advance(payloadBytes);
            }
            return headerBytes + payloadBytes;
        }
        static unsafe int WriteLongString(string value, ref WritableBuffer output)
        {
            fixed (char* c = value)
            {
                var utf16 = new ReadOnlySpan<char>(c, value.Length);
                int totalBytesWritten = 0;
                do
                {
                    output.Ensure(Math.Min(utf16.Length << 2, 128)); // ask for a humble amount, but prepare to be amazed

                    int bytesWritten, charsConsumed;
                    // note: not expecting success here (except for the last one)
                    TryEncode(utf16, output.Buffer.Span, out charsConsumed, out bytesWritten);

                    utf16 = utf16.Slice(charsConsumed);
                    output.Advance(bytesWritten);

                    Trace($"Wrote {charsConsumed} chars of long string in {bytesWritten} bytes");
                    totalBytesWritten += bytesWritten;
                } while (utf16.Length != 0);
                return totalBytesWritten;
            }
        }

        // I need some text APIs back!
        //internal static unsafe bool TryEncode(string utf16, Span<byte> span, out int bytesWritten)
        //    => TryEncode(utf16.AsReadOnlySpan(), span, out _, out bytesWritten);

        internal static unsafe bool TryEncode(ReadOnlySpan<char> utf16, Span<byte> span, out int charsConsumed, out int bytesWritten)
        {
            fixed (char* chars = &MemoryMarshal.GetReference(utf16))
            fixed (byte* bytes = &MemoryMarshal.GetReference(span))
            {
                bytesWritten = Encoding.GetByteCount(chars, utf16.Length);
                if (bytesWritten > span.Length)
                {
                    charsConsumed = 0;
                    bytesWritten = 0;
                    return false;
                }
                bytesWritten = Encoding.GetBytes(chars, utf16.Length, bytes, span.Length);
                charsConsumed = utf16.Length;
            }
            return true;
        }


        internal static unsafe bool TryEncode(string value, Span<byte> span, out int bytesWritten)
        {
            bytesWritten = Encoding.GetByteCount(value);
            if (bytesWritten > span.Length)
            {
                bytesWritten = 0;
                return false;
            }
            fixed (char* chars = value)
            fixed (byte* bytes = &MemoryMarshal.GetReference(span))
            {
                Encoding.GetBytes(chars, value.Length, bytes, span.Length);
            }
            return true;
        }

        protected override ValueTask<int> WriteBytes(ReadOnlySpan<byte> bytes)
        {
            _output.Write(bytes);
            return new ValueTask<int>(bytes.Length);
        }

        protected override ValueTask<int> WriteVarintUInt32Async(uint value)
        {
            _output.Ensure(5);
            int len = WriteVarintUInt32(_output.Buffer.Span, value);
            _output.Advance(len);
            Trace($"Wrote {value} in {len} bytes");
            return new ValueTask<int>(len);
        }
        protected override ValueTask<int> WriteVarintUInt64Async(ulong value)
        {
            _output.Ensure(10);
            int len = WriteVarintUInt64(_output.Buffer.Span, value);
            _output.Advance(len);
            Trace($"Wrote {value} in {len} bytes");
            return new ValueTask<int>(len);
        }

        public override void Dispose()
        {
            var writer = _writer;
            var output = _output;
            _writer = null;
            _output = default(WritableBuffer);
            if (writer != null)
            {
                if (_isFlushing)
                {
                    writer.CancelPendingFlush();
                }
                try { output.Commit(); } catch { /* swallow */ }
                if (_closePipe)
                {
                    writer.Complete();
                }
            }
        }
    }
    internal sealed class BufferWriter : AsyncProtoWriter
    {
        private Memory<byte> _buffer;
        internal BufferWriter(Memory<byte> span)
        {
            _buffer = span;
        }

        protected override ValueTask<int> WriteBytes(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_buffer.Span);
            Trace($"Wrote {bytes.Length} raw bytes ({_buffer.Length - bytes.Length} remain)");
            _buffer = _buffer.Slice(bytes.Length);
            return new ValueTask<int>(bytes.Length);
        }

        protected override ValueTask<int> WriteVarintUInt64Async(ulong value)
        {
            int bytes = PipeWriter.WriteVarintUInt64(_buffer.Span, value);
            Trace($"Wrote {value} as varint in {bytes} bytes ({_buffer.Length - bytes} remain)");
            _buffer = _buffer.Slice(bytes);
            return new ValueTask<int>(bytes);
        }
        protected override ValueTask<int> WriteVarintUInt32Async(uint value)
        {
            int bytes = WriteVarintUInt32(_buffer.Span, value);
            Trace($"Wrote {value} as varint in {bytes} bytes ({_buffer.Length - bytes} remain)");
            _buffer = _buffer.Slice(bytes);
            return new ValueTask<int>(bytes);
        }

        protected override ValueTask<int> WriteStringWithLengthPrefix(string value)
        {
            // can write up to 127 characters (if ASCII) in a single-byte prefix - and conveniently
            // 127 is handy for binary testing
            int bytes = ((value.Length & ~127) == 0) ? TryWriteShortStringWithLengthPrefix(value) : 0;
            if (bytes == 0)
            {
                bytes = WriteLongStringWithLengthPrefix(value);
            }
            return new ValueTask<int>(bytes);
        }
        int TryWriteShortStringWithLengthPrefix(string value)
        {
            // to encode without checking bytes, need 4 times length, plus 1 - the sneaky way
            int bytesWritten;
            if (PipeWriter.TryEncode(value, _buffer.Slice(1, Math.Min(127, _buffer.Length - 1)).Span, out bytesWritten))
            {
                Debug.Assert(bytesWritten <= 127, "Too many bytes written in TryWriteShortStringWithLengthPrefix");
                _buffer.Span[0] = (byte)bytesWritten++; // note the post-increment here to account for the prefix byte
                Trace($"Wrote '{value}' in {bytesWritten} bytes (including length prefix) without checking length first ({_buffer.Length - bytesWritten} remain)");
                _buffer = _buffer.Slice(bytesWritten);
                return bytesWritten;
            }
            return 0; // failure
        }
        int WriteLongStringWithLengthPrefix(string value)
        {
            int maxPayloadBytes = value.Length << 2, maxHeaderBytes = GetVarintLength((uint)maxPayloadBytes), payloadBytes, headerBytes;
            if (_buffer.Length <= (maxPayloadBytes + maxHeaderBytes) && maxHeaderBytes == GetVarintLength((uint)value.Length))
            {
                // that's handy - it all fits in the buffer, and the prefix-size is the same regardless of best/worst case
                var span = _buffer.Span;
                bool success = PipeWriter.TryEncode(value, span.Slice(maxHeaderBytes), out payloadBytes);
                Debug.Assert(success, "TryEncode failed in WriteLongStringWithLengthPrefix");
                Debug.Assert(payloadBytes <= maxPayloadBytes, "Payload exceeded expected size");

                headerBytes = WriteVarintUInt32(span, (uint)payloadBytes);
                Debug.Assert(headerBytes == maxHeaderBytes, "Header bytes was wrong size");
                Trace($"Wrote '{value}' payload in {headerBytes}+{payloadBytes} bytes into available buffer space ({_buffer.Length - (headerBytes + payloadBytes)} remain)");
                _buffer = _buffer.Slice(headerBytes + payloadBytes);
            }
            else
            {
                int bytesWritten;
                payloadBytes = Encoding.GetByteCount(value);
                headerBytes = WriteVarintUInt32(_buffer.Span, (uint)payloadBytes);
                Trace($"Wrote '{value}' header in {headerBytes} bytes into available buffer space ({_buffer.Length - headerBytes} remain)");
                _buffer = _buffer.Slice(headerBytes);

                // we should already have enough space in the output buffer - just write it
                bool success = PipeWriter.TryEncode(value, _buffer.Span, out bytesWritten);
                if (!success) throw new InvalidOperationException("Span range would be exceeded");
                Trace($"Wrote '{value}' payload in {payloadBytes} bytes into available buffer space ({_buffer.Length - payloadBytes} remain)");
                Debug.Assert(bytesWritten == payloadBytes, "Payload length mismatch in WriteLongStringWithLengthPrefix");

                _buffer = _buffer.Slice(payloadBytes);

            }
            return headerBytes + payloadBytes;
        }
    }


    static string NormalizeHex(string hex) => hex.Replace('-', ' ').Replace(" ", "").Trim().ToUpperInvariant();

    private static byte[] ParseBlob(string hex)
    {
        hex = NormalizeHex(hex);
        var len = hex.Length / 2;
        byte[] blob = new byte[len];
        for (int i = 0; i < blob.Length; i++)
        {
            blob[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
        }
        return blob;
    }
    private static Task AppendPayloadAsync(IPipe pipe, string hex)
    {
        var blob = ParseBlob(hex);
        return pipe.Writer.WriteAsync(blob);
    }
}