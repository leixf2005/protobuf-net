using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Attributes;
using System.Threading.Tasks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using MemoryDiagnoser = BenchmarkDotNet.Diagnosers.MemoryDiagnoser;
using BenchmarkDotNet.Validators;
using BenchmarkDotNet.Columns;
using System.IO;
using System.Buffers;
using AggressiveNamespace;
using System.IO.Pipelines;
using BenchmarkDotNet.Attributes.Columns;

namespace TheAwaitingGame
{
    class Program
    {
        static void Main()
        {
            // tell BenchmarkDotNet not to force GC.Collect after benchmark iteration 
            // (single iteration contains of multiple (usually millions) of invocations)
            // it can influence the allocation-heavy Task<T> benchmarks
            var gcMode = new GcMode { Force = false };

            var customConfig = ManualConfig
                .Create(DefaultConfig.Instance) // copies all exporters, loggers and basic stuff
                .With(JitOptimizationsValidator.FailOnError) // Fail if not release mode
                .With(MemoryDiagnoser.Default) // use memory diagnoser
                .With(StatisticColumn.OperationsPerSecond) // add ops/s
                .With(Job.Default.With(gcMode));

#if NET462
            // enable the Inlining Diagnoser to find out what does not get inlined
            // uncomment it first, it produces a lot of output
            //customConfig = customConfig.With(new BenchmarkDotNet.Diagnostics.Windows.InliningDiagnoser(logFailuresOnly: true, filterByNamespace: true));
#endif
            
            var summary = BenchmarkRunner.Run<Benchmarker>(customConfig);
            Console.WriteLine(summary);
        }
    }
    [GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
    [CategoriesColumn]
    public class Benchmarker
    {
        static ProtoBuf.Customer _customer;
        static byte[] _customerBlob, _magicWrapperBlob;
        public Benchmarker()
        {
            // touch the static field to ensure .cctor has run
            GC.KeepAlive(_customer);
        }
        static Benchmarker()
        {
            var rand = new Random(1234); // seed correlates to asserted number of orders
            _customer = SimpleUsage.InventCustomer(rand);

            var ms = new MemoryStream();
            ProtoBuf.Serializer.Serialize(ms, _customer);
            _customerBlob = ms.ToArray();

            const int CUSTOMER_COUNT = 250;

            ms.SetLength(0);
            ms.Position = 0;
            for(int i = 0; i < CUSTOMER_COUNT; i++)
            {
                ProtoBuf.Serializer.SerializeWithLengthPrefix(ms, _customer, ProtoBuf.PrefixStyle.Base128, 1);
            }
            _magicWrapperBlob = ms.ToArray();


            //var book = new OrderBook();
            //for (int i = 0; i < 50; i++)
            //{
            //    var order = new Order();
            //    int lines = rand.Next(1, 10);
            //    for (int j = 0; j < lines; j++)
            //    {
            //        order.Lines.Add(new OrderLine
            //        {
            //            Quantity = rand.Next(1, 20),
            //            UnitPrice = 0.01M * rand.Next(1, 5000)
            //        });
            //    }
            //    book.Orders.Add(order);
            //}
            //_book = book;

            //int baseline = _book.GetTotalWorthInt32Sync(5);
            //Check(baseline, _book.GetTotalWorthTaskInt32AwaitAsync(5));
            //Check(baseline, _book.GetTotalWorthTaskInt32ManualCompletedAsync(5));
            //Check(baseline, _book.GetTotalWorthTaskInt32ManualCompletedSuccessfullyAsync(5));
            //Check(baseline, _book.GetTotalWorthValueTaskInt32AwaitAsync(5));
            //Check(baseline, _book.GetTotalWorthValueTaskInt32ManualCompletedAsync(5));
            //Check(baseline, _book.GetTotalWorthValueTaskInt32ManualCompletedSuccessfullyAsync(5));
        }

        //private static void Check(int baseline, ValueTask<int> task)
        //{
        //    if (task.IsCompleted && task.Result != baseline)
        //        throw new InvalidOperationException("Baseline check failed");
        //}

        //private static void Check(int baseline, Task<int> task)
        //{
        //    if (task.IsCompleted && task.Result != baseline)
        //        throw new InvalidOperationException("Baseline check failed");
        //}

        const int REPEATS_PER_CUSTOMER = 250;

        [Benchmark(OperationsPerInvoke = REPEATS_PER_CUSTOMER, Description = "Stream", Baseline = true)]
        [BenchmarkCategory("single read")]
        public void DeserializeSingleWithStream()
        {
            var stream = new MemoryStream(_customerBlob);
            for(int i  = 0; i < REPEATS_PER_CUSTOMER; i++)
            {
                stream.Position = 0;
                GC.KeepAlive(ProtoBuf.Serializer.Deserialize<ProtoBuf.Customer>(stream));
            }
        }

        [Benchmark(OperationsPerInvoke = REPEATS_PER_CUSTOMER, Description = "ReadOnlyBuffer")]
        [BenchmarkCategory("single read")]
        public void DeserializeSingleWithBuffer()
        {
            var buffer = new ReadOnlyBuffer(_customerBlob);
            for (int i = 0; i < REPEATS_PER_CUSTOMER; i++)
            {
                GC.KeepAlive(AggressiveDeserializer.Instance.Deserialize<ProtoBuf.Customer>(buffer));
            }
        }


        const int REPEATS_PER_MAGIC_WRAPPER = 5;

        [Benchmark(OperationsPerInvoke = REPEATS_PER_MAGIC_WRAPPER, Description = "Stream", Baseline = true)]
        [BenchmarkCategory("multi read")]
        public void DeserializeMultiWithStream()
        {
            var stream = new MemoryStream(_magicWrapperBlob);
            for (int i = 0; i < REPEATS_PER_MAGIC_WRAPPER; i++)
            {
                stream.Position = 0;
                GC.KeepAlive(ProtoBuf.Serializer.Deserialize<ProtoBuf.CustomerMagicWrapper>(stream));
            }
        }

        [Benchmark(OperationsPerInvoke = REPEATS_PER_MAGIC_WRAPPER, Description = "ReadOnlyBuffer")]
        [BenchmarkCategory("multi read")]
        public void DeserializeMultiWithBuffer()
        {
            var buffer = new ReadOnlyBuffer(_magicWrapperBlob);
            for (int i = 0; i < REPEATS_PER_MAGIC_WRAPPER; i++)
            {
                GC.KeepAlive(AggressiveDeserializer.Instance.Deserialize<ProtoBuf.CustomerMagicWrapper>(buffer));
            }
        }

        PipeOptions _options = new PipeOptions(new MemoryPool());
        [Benchmark(Description = "Pipe, single alloc", OperationsPerInvoke = 50)]
        [BenchmarkCategory("multi write")]
        public long WriteWithPipeSingleAlloc()
        {
            var pipe = new Pipe(_options);
            var writer = pipe.Writer;
            var buffer = writer.Alloc();
            long totalBytes = 0;
            for (int i = 0; i < 50; i++)
            {
                totalBytes += AggressiveDeserializer.Instance.SerializeWithLengthPrefix<ProtoBuf.Customer>(buffer, _customer, 1);
            }
            buffer.Commit();
            writer.Complete();
            pipe.Reader.Complete();
            pipe.Reset();
            return totalBytes;
        }

        [Benchmark(Description = "Pipe, alloc per item", OperationsPerInvoke = 50)]
        [BenchmarkCategory("read/write")]
        public async ValueTask<long> ReadWriteWithPipeMultiAlloc()
        {
            var pipe = new Pipe(_options);
            var writer = pipe.Writer;
            long totalBytes = 0;
            for (int i = 0; i < 50; i++)
            {
                var buffer = writer.Alloc();
                totalBytes += AggressiveDeserializer.Instance.SerializeWithLengthPrefix<ProtoBuf.Customer>(buffer, _customer, 1);
                buffer.Commit();
            }
            writer.Complete();

            await AggressiveDeserializer.Instance.DeserializeAsync<ProtoBuf.CustomerMagicWrapper>(pipe.Reader);
            pipe.Reader.Complete();
            pipe.Reset();
            return totalBytes;
        }

        [Benchmark(Description = "Pipe, alloc per item", OperationsPerInvoke = 50)]
        [BenchmarkCategory("multi write")]
        public long WriteWithPipeMultiAlloc()
        {
            var pipe = new Pipe(_options);
            var writer = pipe.Writer;            
            long totalBytes = 0;
            for (int i = 0; i < 50; i++)
            {
                var buffer = writer.Alloc();
                totalBytes += AggressiveDeserializer.Instance.SerializeWithLengthPrefix<ProtoBuf.Customer>(buffer, _customer, 1);
                buffer.Commit();
            }            
            writer.Complete();
            pipe.Reader.Complete();
            pipe.Reset();
            return totalBytes;
        }



        [Benchmark(Description = "Stream", OperationsPerInvoke = 50, Baseline = true)]
        [BenchmarkCategory("multi write")]
        public long WriteWithStream()
        {
            var ms = new MemoryStream();
            for (int i = 0; i < 50; i++)
            {
                ProtoBuf.Serializer.SerializeWithLengthPrefix<ProtoBuf.Customer>(ms, _customer, ProtoBuf.PrefixStyle.Base128, 1);
            }
            return ms.Length;
        }

        [Benchmark(Description = "Stream", OperationsPerInvoke = 50, Baseline = true)]
        [BenchmarkCategory("read/write")]
        public long ReadWriteWithStream()
        {
            var ms = new MemoryStream();
            for (int i = 0; i < 50; i++)
            {
                ProtoBuf.Serializer.SerializeWithLengthPrefix<ProtoBuf.Customer>(ms, _customer, ProtoBuf.PrefixStyle.Base128, 1);
            }
            ms.Position = 0;
            ProtoBuf.Serializer.Deserialize<ProtoBuf.CustomerMagicWrapper>(ms);
            return ms.Length;
        }

#if OLDER_BENCHMARKS
        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM)]
        public decimal Sync() => _book.GetTotalWorth(REPEATS_PER_ITEM);

        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM)]
        public Task<decimal> TaskAsync() => _book.GetTotalWorthTaskAsync(REPEATS_PER_ITEM);

        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM)]
        public Task<decimal> TaskCheckedAsync() => _book.GetTotalWorthTaskCheckedAsync(REPEATS_PER_ITEM);

        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM)]
        public ValueTask<decimal> ValueTaskAsync() => _book.GetTotalWorthValueTaskAsync(REPEATS_PER_ITEM);

        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM)]
        public ValueTask<decimal> ValueTaskWrappedAsync() => _book.GetTotalWorthValueTaskCheckedWrappedAsync(REPEATS_PER_ITEM);

        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM)]
        public ValueTask<decimal> ValueTaskDecimalReferenceAsync() => _book.GetTotalWorthValueTaskCheckedDecimalReferenceAsync(REPEATS_PER_ITEM);

        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM)]
        public ValueTask<decimal> ValueTaskCheckedAsync() => _book.GetTotalWorthValueTaskCheckedAsync(REPEATS_PER_ITEM);

        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM)]
        public ValueTask<decimal> HandCrankedAsync() => _book.GetTotalWorthHandCrankedAsync(REPEATS_PER_ITEM);

        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM)]
        public ValueTask<decimal> AssertCompletedAsync() => _book.GetTotalWorthAssertCompletedAsync(REPEATS_PER_ITEM);

        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM)]
        public Task<double> TaskDoubleAsync() => _book.GetTotalWorthTaskDoubleAsync(REPEATS_PER_ITEM);

        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM)]
        public ValueTask<double> ValueTaskDoubleAsync() => _book.GetTotalWorthValueTaskDoubleAsync(REPEATS_PER_ITEM);


        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM, Description = "int/await/task")]
        public Task<int> TaskInt32AwaitAsync() => _book.GetTotalWorthTaskInt32AwaitAsync(REPEATS_PER_ITEM);

        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM, Description = "int/manual/task/iscompleted")]
        public Task<int> TaskInt32ManualCAsync() => _book.GetTotalWorthTaskInt32ManualCompletedAsync(REPEATS_PER_ITEM);
        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM, Description = "int/manual/task/iscompletedsuccessfully")]
        public Task<int> TaskInt32ManualCSAsync() => _book.GetTotalWorthTaskInt32ManualCompletedSuccessfullyAsync(REPEATS_PER_ITEM);


        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM, Description = "int/await/valuetask")]
        public ValueTask<int> ValueTaskInt32AwaitAsync() => _book.GetTotalWorthValueTaskInt32AwaitAsync(REPEATS_PER_ITEM);

        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM, Description = "int/manual/valuetask/iscompleted")]
        public ValueTask<int> ValueTaskInt32ManualCAsync() => _book.GetTotalWorthValueTaskInt32ManualCompletedAsync(REPEATS_PER_ITEM);
        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM, Description = "int/manual/valuetask/iscompletedsuccessfully")]
        public ValueTask<int> ValueTaskInt32ManualCSAsync() => _book.GetTotalWorthValueTaskInt32ManualCompletedSuccessfullyAsync(REPEATS_PER_ITEM);

        [Benchmark(OperationsPerInvoke = REPEATS_PER_ITEM, Description = "int/sync", Baseline = true)]
        public int TaskInt32Sync() => _book.GetTotalWorthInt32Sync(REPEATS_PER_ITEM);

#endif
    }

    public static class ValueTaskExtensions
    {
        public static T AssertCompleted<T>(this ValueTask<T> task)
        {
            if (!task.IsCompleted)
            {
                throw new InvalidOperationException();
            }
            return task.Result;
        }
    }
    class OrderBook
    {
        public List<Order> Orders { get; } = new List<Order>();

        public decimal GetTotalWorth(int repeats)
        {
            decimal total = 0;
            while (repeats-- > 0)
            {
                foreach (var order in Orders) total += order.GetOrderWorth();
            }
            return total;
        }
        public async Task<decimal> GetTotalWorthTaskAsync(int repeats)
        {
            decimal total = 0;
            while (repeats-- > 0)
            {
                foreach (var order in Orders) total += await order.GetOrderWorthTaskAsync();
            }
            return total;
        }
        public async Task<decimal> GetTotalWorthTaskCheckedAsync(int repeats)
        {
            decimal total = 0;
            while (repeats-- > 0)
            {
                foreach (var order in Orders)
                {
                    var task = order.GetOrderWorthTaskCheckedAsync();
                    total += (task.IsCompleted) ? task.Result : await task;
                }
            }
            return total;
        }
        public async ValueTask<decimal> GetTotalWorthValueTaskAsync(int repeats)
        {
            decimal total = 0;
            while (repeats-- > 0)
            {
                foreach (var order in Orders) total += await order.GetOrderWorthValueTaskAsync();
            }
            return total;
        }
        public async ValueTask<decimal> GetTotalWorthValueTaskCheckedAsync(int repeats)
        {
            decimal total = 0;
            while (repeats-- > 0)
            {
                foreach (var order in Orders)
                {
                    var task = order.GetOrderWorthValueTaskCheckedAsync();
                    total += (task.IsCompleted) ? task.Result : await task;
                }
            }
            return total;
        }
        public async ValueTask<decimal> GetTotalWorthValueTaskCheckedWrappedAsync(int repeats)
        {
            decimal total = 0;
            while (repeats-- > 0)
            {
                foreach (var order in Orders)
                {
                    var task = order.GetOrderWorthValueTaskCheckedAsync();
                    total += await task.AsTask();
                }
            }
            return total;
        }
        public async ValueTask<decimal> GetTotalWorthValueTaskCheckedDecimalReferenceAsync(int repeats)
        {
            decimal total = 0;
            while (repeats-- > 0)
            {
                foreach (var order in Orders)
                {
                    var task = order.GetOrderWorthAssertCompletedDecimalReferenceAsync();
                    total += (task.IsCompleted) ? task.Result.Value : (await task).Value;
                }
            }
            return total;
        }
        public ValueTask<decimal> GetTotalWorthAssertCompletedAsync(int repeats)
        {
            decimal total = 0;
            while (repeats-- > 0)
            {
                foreach (var order in Orders) total += order.GetOrderWorthAssertCompletedAsync().AssertCompleted();
            }
            return new ValueTask<decimal>(total);
        }
        public ValueTask<decimal> GetTotalWorthHandCrankedAsync(int repeats)
        {
            decimal total = 0;

            var orders = Orders;
            var count = orders.Count;
            var task = default(ValueTask<decimal>);

            while (repeats-- > 0)
            {
                var i = 0;
                for (; i < count; i++)
                {
                    task = orders[i].GetOrderWorthHandCrankedAsync();
                    if (!task.IsCompleted) break;
                    total += task.Result;
                }

                if (i < count)
                {
                    return ContinueAsync(total, task, repeats, i);
                }
            }
            return new ValueTask<decimal>(total);
        }
        private async ValueTask<decimal> ContinueAsync(decimal total, ValueTask<decimal> task, int repeats, int i)
        {
            total += await task;

            var orders = Orders;
            while (repeats-- > 0)
            {
                var count = orders.Count;
                i = 0;
                for (; i < count; i++)
                {
                    task = orders[i].GetOrderWorthHandCrankedAsync();
                    total += (task.IsCompleted) ? task.Result : await task;
                }
            }

            return total;
        }
        public async Task<double> GetTotalWorthTaskDoubleAsync(int repeats)
        {
            double total = 0;
            while (repeats-- > 0)
            {
                foreach (var order in Orders) total += await order.GetOrderWorthTaskDoubleAsync();
            }
            return total;
        }
        public async ValueTask<double> GetTotalWorthValueTaskDoubleAsync(int repeats)
        {
            double total = 0;
            while (repeats-- > 0)
            {
                foreach (var order in Orders) total += await order.GetOrderWorthValueTaskDoubleAsync();
            }
            return total;
        }

        internal int GetTotalWorthInt32Sync(int repeats)
        {
            int total = 0;
            while (repeats-- > 0)
            {
                for (int i = 0; i < Orders.Count; i++)
                {
                    total += Orders[i].GetOrderWorthInt32Sync();
                }
            }
            return total;
        }
        internal async Task<int> GetTotalWorthTaskInt32AwaitAsync(int repeats)
        {
            int total = 0;
            while (repeats-- > 0)
            {
                for (int i = 0; i < Orders.Count; i++)
                {
                    total += await Orders[i].GetOrderWorthTaskInt32AwaitAsync();
                }
            }
            return total;
        }
        internal async ValueTask<int> GetTotalWorthValueTaskInt32AwaitAsync(int repeats)
        {
            int total = 0;
            while (repeats-- > 0)
            {
                for (int i = 0; i < Orders.Count; i++)
                {
                    total += await Orders[i].GetOrderWorthValueTaskInt32AwaitAsync();
                }
            }
            return total;
        }
        internal Task<int> GetTotalWorthTaskInt32ManualCompletedAsync(int repeats)
        {
            async Task<int> Awaited(Task<int> task, int total, int i, int r)
            {
                total += await task;
                while (++i < Orders.Count) // finish the inner loop
                {
                    total += await Orders[i].GetOrderWorthTaskInt32ManualCompletedAsync();
                }
                while (r-- > 0) // finish the outer loop
                {
                    for (i = 0; i < Orders.Count; i++)
                    {
                        total += await Orders[i].GetOrderWorthTaskInt32ManualCompletedAsync();
                    }
                }
                return total;
            }
            {
                int total = 0;
                while (repeats-- > 0)
                {
                    for (int i = 0; i < Orders.Count; i++)
                    {
                        var task = Orders[i].GetOrderWorthTaskInt32ManualCompletedAsync();
                        if (!task.IsCompleted) return Awaited(task, total, i, repeats);
                        total += task.Result;
                    }
                }
                return Task.FromResult(total);
            }
        }
        internal Task<int> GetTotalWorthTaskInt32ManualCompletedSuccessfullyAsync(int repeats)
        {
            async Task<int> Awaited(Task<int> task, int total, int i, int r)
            {
                total += await task;
                while (++i < Orders.Count) // finish the inner loop
                {
                    total += await Orders[i].GetOrderWorthTaskInt32ManualCompletedSuccessfullyAsync();
                }
                while (r-- > 0) // finish the outer loop
                {
                    for (i = 0; i < Orders.Count; i++)
                    {
                        total += await Orders[i].GetOrderWorthTaskInt32ManualCompletedSuccessfullyAsync();
                    }
                }
                return total;
            }
            {
                int total = 0;
                while (repeats-- > 0)
                {
                    for (int i = 0; i < Orders.Count; i++)
                    {
                        var task = Orders[i].GetOrderWorthTaskInt32ManualCompletedSuccessfullyAsync();
                        if (task.Status != TaskStatus.RanToCompletion) return Awaited(task, total, i, repeats);
                        total += task.Result;
                    }
                }
                return Task.FromResult(total);
            }
        }
        internal ValueTask<int> GetTotalWorthValueTaskInt32ManualCompletedAsync(int repeats)
        {
            async ValueTask<int> Awaited(ValueTask<int> task, int total, int i, int r)
            {
                total += await task;
                while (++i < Orders.Count) // finish the inner loop
                {
                    total += await Orders[i].GetOrderWorthValueTaskInt32ManualCompletedAsync();
                }
                while (r-- > 0) // finish the outer loop
                {
                    for (i = 0; i < Orders.Count; i++)
                    {
                        total += await Orders[i].GetOrderWorthValueTaskInt32ManualCompletedAsync();
                    }
                }
                return total;
            }
            {
                int total = 0;
                while (repeats-- > 0)
                {
                    for (int i = 0; i < Orders.Count; i++)
                    {
                        var task = Orders[i].GetOrderWorthValueTaskInt32ManualCompletedAsync();
                        if (!task.IsCompleted) return Awaited(task, total, i, repeats);
                        total += task.Result;
                    }
                }
                return new ValueTask<int>(total);
            }
        }
        internal ValueTask<int> GetTotalWorthValueTaskInt32ManualCompletedSuccessfullyAsync(int repeats)
        {
            async ValueTask<int> Awaited(ValueTask<int> task, int total, int i, int r)
            {
                total += await task;
                while (++i < Orders.Count) // finish the inner loop
                {
                    total += await Orders[i].GetOrderWorthValueTaskInt32ManualCompletedSuccessfullyAsync();
                }
                while (r-- > 0) // finish the outer loop
                {
                    for (i = 0; i < Orders.Count; i++)
                    {
                        total += await Orders[i].GetOrderWorthValueTaskInt32ManualCompletedSuccessfullyAsync();
                    }
                }
                return total;
            }
            {
                int total = 0;
                while (repeats-- > 0)
                {
                    for (int i = 0; i < Orders.Count; i++)
                    {
                        var task = Orders[i].GetOrderWorthValueTaskInt32ManualCompletedSuccessfullyAsync();
                        if (!task.IsCompletedSuccessfully) return Awaited(task, total, i, repeats);
                        total += task.Result;
                    }
                }
                return new ValueTask<int>(total);
            }
        }
    }
    class Order
    {
        public List<OrderLine> Lines { get; } = new List<OrderLine>();

        public decimal GetOrderWorth()
        {
            decimal total = 0;
            foreach (var line in Lines) total += line.GetLineWorth();
            return total;
        }
        public async Task<decimal> GetOrderWorthTaskAsync()
        {
            decimal total = 0;
            foreach (var line in Lines) total += await line.GetLineWorthTaskAsync();
            return total;
        }
        public async Task<decimal> GetOrderWorthTaskCheckedAsync()
        {
            decimal total = 0;
            foreach (var line in Lines)
            {
                var task = line.GetLineWorthTaskAsync();
                total += (task.IsCompleted) ? task.Result : await task;
            }
            return total;
        }
        public async ValueTask<decimal> GetOrderWorthValueTaskAsync()
        {
            decimal total = 0;
            foreach (var line in Lines) total += await line.GetLineWorthValueTaskAsync();
            return total;
        }
        public async ValueTask<decimal> GetOrderWorthValueTaskCheckedAsync()
        {
            decimal total = 0;
            foreach (var line in Lines)
            {
                var task = line.GetLineWorthValueTaskAsync();
                total += (task.IsCompleted) ? task.Result : await task;
            }
            return total;
        }

        public async ValueTask<DecimalReference> GetOrderWorthAssertCompletedDecimalReferenceAsync()
        {
            decimal total = 0;
            foreach (var line in Lines)
            {
                var task = line.GetLineWorthValueTaskDecimalReferenceAsync();
                total += (task.IsCompleted) ? task.Result.Value : (await task).Value;
            }
            return new DecimalReference(total);
        }

        public async Task<double> GetOrderWorthTaskDoubleAsync()
        {
            double total = 0;
            foreach (var line in Lines)
            {
                total += await line.GetLineWorthTaskDoubleAsync();
            }
            return total;
        }

        public async ValueTask<double> GetOrderWorthValueTaskDoubleAsync()
        {
            double total = 0;
            foreach (var line in Lines)
            {
                total += await line.GetLineWorthValueTaskDoubleAsync();
            }
            return total;
        }


        public ValueTask<decimal> GetOrderWorthAssertCompletedAsync()
        {
            decimal total = 0;
            foreach (var line in Lines) total += line.GetLineWorthValueTaskAsync().AssertCompleted();
            return new ValueTask<decimal>(total);
        }

        public ValueTask<decimal> GetOrderWorthHandCrankedAsync()
        {
            decimal total = 0;

            var currentTask = default(ValueTask<decimal>);
            var lines = Lines;
            var count = lines.Count;

            var i = 0;
            for (; i < count; i++)
            {
                currentTask = lines[i].GetLineWorthValueTaskAsync();
                if (!currentTask.IsCompleted) break;
                total += currentTask.Result;
            }

            if (i < count) return ContinueAsync(total, currentTask, i);
            return new ValueTask<decimal>(total);
        }

        async ValueTask<decimal> ContinueAsync(decimal total, ValueTask<decimal> currentTask, int i)
        {
            total += await currentTask;

            var count = Lines.Count;

            for (; i < count; i++)
            {
                currentTask = Lines[i].GetLineWorthValueTaskAsync();
                if (currentTask.IsCompleted)
                    total += currentTask.Result;
                else
                    total += await currentTask;
            }

            return total;
        }

        internal int GetOrderWorthInt32Sync()
        {
            int total = 0;
            for (int i = 0; i < Lines.Count; i++)
            {
                total += Lines[i].GetLineWorthInt32Sync();
            }
            return total;
        }
        internal async Task<int> GetOrderWorthTaskInt32AwaitAsync()
        {
            int total = 0;
            for (int i = 0; i < Lines.Count; i++)
            {
                total += await Lines[i].GetLineWorthInt32TaskAsync();
            }
            return total;
        }
        internal async ValueTask<int> GetOrderWorthValueTaskInt32AwaitAsync()
        {
            int total = 0;
            for (int i = 0; i < Lines.Count; i++)
            {
                total += await Lines[i].GetLineWorthInt32ValueTaskAsync();
            }
            return total;
        }

        internal Task<int> GetOrderWorthTaskInt32ManualCompletedAsync()
        {
            async Task<int> Awaited(Task<int> task, int total, int i)
            {
                total += await task;
                while (++i < Lines.Count)
                {
                    total += await Lines[i].GetLineWorthInt32TaskAsync();
                }
                return total;
            }
            {
                int total = 0;
                for (int i = 0; i < Lines.Count; i++)
                {
                    var task = Lines[i].GetLineWorthInt32TaskAsync();
                    if (!task.IsCompleted) return Awaited(task, total, i);
                    total += task.Result;
                }
                return Task.FromResult(total);
            }
        }
        internal Task<int> GetOrderWorthTaskInt32ManualCompletedSuccessfullyAsync()
        {
            async Task<int> Awaited(Task<int> task, int total, int i)
            {
                total += await task;
                while (++i < Lines.Count)
                {
                    total += await Lines[i].GetLineWorthInt32TaskAsync();
                }
                return total;
            }
            {
                int total = 0;
                for (int i = 0; i < Lines.Count; i++)
                {
                    var task = Lines[i].GetLineWorthInt32TaskAsync();
                    if (task.Status != TaskStatus.RanToCompletion) return Awaited(task, total, i);
                    total += task.Result;
                }
                return Task.FromResult(total);
            }
        }
        internal ValueTask<int> GetOrderWorthValueTaskInt32ManualCompletedAsync()
        {
            async ValueTask<int> Awaited(ValueTask<int> task, int total, int i)
            {
                total += await task;
                while (++i < Lines.Count)
                {
                    total += await Lines[i].GetLineWorthInt32ValueTaskAsync();
                }
                return total;
            }
            {
                int total = 0;
                for (int i = 0; i < Lines.Count; i++)
                {
                    var task = Lines[i].GetLineWorthInt32ValueTaskAsync();
                    if (!task.IsCompleted) return Awaited(task, total, i);
                    total += task.Result;
                }
                return new ValueTask<int>(total);
            }
        }
        internal ValueTask<int> GetOrderWorthValueTaskInt32ManualCompletedSuccessfullyAsync()
        {
            async ValueTask<int> Awaited(ValueTask<int> task, int total, int i)
            {
                total += await task;
                while (++i < Lines.Count)
                {
                    total += await Lines[i].GetLineWorthInt32ValueTaskAsync();
                }
                return total;
            }
            {
                int total = 0;
                for (int i = 0; i < Lines.Count; i++)
                {
                    var task = Lines[i].GetLineWorthInt32ValueTaskAsync();
                    if (!task.IsCompletedSuccessfully) return Awaited(task, total, i);
                    total += task.Result;
                }
                return new ValueTask<int>(total);
            }
        }
    }
    public class OrderLine
    {
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal GetLineWorth() => Quantity * UnitPrice;
        public Task<decimal> GetLineWorthTaskAsync() => Task.FromResult(Quantity * UnitPrice);

        [MethodImpl(MethodImplOptions.AggressiveInlining)] // it fails to inline by default due to "Native estimate for function size exceeds threshold."
        public ValueTask<decimal> GetLineWorthValueTaskAsync() => new ValueTask<decimal>(Quantity * UnitPrice);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<DecimalReference> GetLineWorthValueTaskDecimalReferenceAsync() => new ValueTask<DecimalReference>(new DecimalReference(Quantity * UnitPrice));

        public ValueTask<double> GetLineWorthValueTaskDoubleAsync() => new ValueTask<double>((double)(Quantity * UnitPrice));

        public Task<double> GetLineWorthTaskDoubleAsync() => Task.FromResult<double>((double)(Quantity * UnitPrice));

#if CONSTANT_RESULTS
        const int FixedResultValue = 42;
        static readonly Task<int> FixedResultTask = Task.FromResult(FixedResultValue);

        internal int GetLineWorthInt32Sync() => FixedResultValue;
        internal Task<int> GetLineWorthInt32TaskAsync() => FixedResultTask;

        internal ValueTask<int> GetLineWorthInt32ValueTaskAsync() => new ValueTask<int>(FixedResultValue);
#else

        internal int GetLineWorthInt32Sync() => Quantity;
        internal Task<int> GetLineWorthInt32TaskAsync() => Task.FromResult(Quantity);

        internal ValueTask<int> GetLineWorthInt32ValueTaskAsync() => new ValueTask<int>(Quantity);

#endif
    }
    public class DecimalReference
    {
        public decimal Value;
        public DecimalReference(decimal value)
        {
            Value = value;
        }
    }
}