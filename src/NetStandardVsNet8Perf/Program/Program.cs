using BenchmarkDotNet.Running;
using NetStandardLib;

namespace NetStandardVsNet8Perf;

class Program
{
    static void Main(string[] args)
    {
        BenchmarkRunner.Run<ResponseToStringBenchmark>();
    }
}