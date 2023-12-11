//using BenchmarkDotNet.Running;

using System.Diagnostics;
using System.Net;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace StringInterning
{
    internal class Program
    {
        static void Check()
        {
            SortedList<int, int> nodeList = new SortedList<int, int>()
            {
                [1] = 1,
                [2] = 2,
                [3] = 3,
            };

            var nodesToRemove = new HashSet<int>() {1, 2};

            var nodes = nodeList.Where(n => nodesToRemove.Contains(n.Key));

            foreach (var nodeKvp in nodes)
            {
                nodeList.Remove(nodeKvp.Key);
            }

        }
        static void Compare(string name1, Action<StringInterningBenchmarks> action1, string name2,
            Action<StringInterningBenchmarks> action2)
        {
            var bm0 = new StringInterningBenchmarks(10);
            action1(bm0);
            action2(bm0);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var bm = new StringInterningBenchmarks(5_000_000, dop: Environment.ProcessorCount);
            Console.WriteLine("Running...");

            var sw = Stopwatch.StartNew();

            action1(bm);
            action1(bm);
            //action1(bm);
            //action1(bm);
            //action1(bm);

            Console.WriteLine($"{name1} is done in {sw.Elapsed}");
            GC.Collect();
            Thread.Sleep(2000);
            sw = Stopwatch.StartNew();

            action2(bm);
            action2(bm);
            //action2(bm);
            //action2(bm);
            //action2(bm);
            Console.WriteLine($"{name2} is done in {sw.Elapsed}");

            sw = Stopwatch.StartNew();
            //bm.Validate_StringTable_Intern();
            bm.Validate_String_Intern();
            bm.Validate_CustomString_Intern();
            bm.Validate_ConcurrentDictionary_Intern();
            Console.WriteLine($"Validated that all the strings are interned in {sw.Elapsed}!");
        }

        static async Task Stress()
        {
            var cts = new CancellationTokenSource();
            long count = 0;
            cts.CancelAfter(TimeSpan.FromMinutes(150));
            Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var old = Interlocked.Read(ref count);
                    
                    await Task.Delay(1000, cts.Token);
                    var @new = Interlocked.Read(ref count);
                    Console.WriteLine($"Performed {(@new - old)} runs. {@new} overall...");
                }
            });

            while (!cts.IsCancellationRequested)
            {
                await LookupStress.Stress(cts.Token);
                Interlocked.Increment(ref count);
            }
        }

        static void Main(string[] args)
        {
            //LookupStress.Stress(CancellationToken.None).GetAwaiter().GetResult();
            //Stress().GetAwaiter().GetResult();
            //BenchmarkRunner.Run<LookupBenchmark>();
            // Check();
            //var token = new CancellationTokenSource(delay: TimeSpan.FromSeconds(2));
            //LookupStress.Stress(token.Token).GetAwaiter().GetResult();
            //Console.WriteLine("Done!");
            //var config = DefaultConfig.Instance.With(Job.Default.With(NativeAotRuntime.Net70));
            //BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
            //    .Run(args, config);
            //BenchmarkRunner.Run<StringInterningBenchmarks>();

            var bm = new StringInterningBenchmarks() { Count = 10 };
            bm.Setup();
            bm.String_Intern();
            bm.StringCache_Intern();

            bm.Count = 10_000_000;
            bm.Setup();
            GC.Collect();
            // to make it easier to see the sections in profiling session
            Thread.Sleep(2_000);

            var sw = Stopwatch.StartNew();
            // The first call will populate the cache
            // and the second one will mostly read from the cache.
            for (int i = 0; i < 10; i++)
                bm.StringCache_Intern();

            Console.WriteLine($"Custom string interning is done in {sw.Elapsed}");

            GC.Collect();
            // to make it easier to see the sections in profiling session
            Thread.Sleep(2_000);
            sw.Restart();

            for (int i = 0; i < 10; i++)
                bm.String_Intern();

            Console.WriteLine($"String interning is done in {sw.Elapsed}");



            //Compare(
            //    "String.Intern", bm => bm.String_Intern(),
            //    "CSI", bm => bm.ConcurrentDictionary_Based_StringCache());

            //Compare(
            //    "CD", bm => bm.String_Intern(),
            //    "CSI", bm => bm.String_Intern());
        }
    }
}
