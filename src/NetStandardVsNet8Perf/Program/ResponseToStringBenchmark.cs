using BenchmarkDotNet.Attributes;
using NetStandardLib;

namespace NetStandardVsNet8Perf;

[MemoryDiagnoser]
public class ResponseToStringBenchmark
{
    private readonly Response _response = new(DateTime.Now);

    [Benchmark(Baseline = true)]
    public string DefaultToString() => _response.ToString();

    [Benchmark]
    public string ToStringFast() => _response.ToStringFast();
}

public static class ResponseExtensions
{
    public static string ToStringFast(this Response r)
    {
        return $"{nameof(r.When)}: {r.When}, {nameof(r.Id)}: {r.Id}, {nameof(r.X)}: {r.X}, {nameof(r.Y)}: {r.Y}";
    }
}