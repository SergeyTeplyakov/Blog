//using BenchmarkDotNet.Attributes;
//using BenchmarkDotNet.Jobs;

using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Roslyn.Utilities;

namespace StringInterning;

[MemoryDiagnoser]
//[SimpleJob(RuntimeMoniker.Net472)]
//[SimpleJob(RuntimeMoniker.Net80)]
//[SimpleJob(RuntimeMoniker.NativeAot80)]
public class StringInterningBenchmarks
{
    
    private readonly int _dop;

    public StringInterningBenchmarks(int count, int? dop = null)
    {
        //_count = count;
        _dop = dop ?? Environment.ProcessorCount;
        _list = Enumerable.Range(1, count).Select(i => i.ToString()).ToList();
    }

    public StringInterningBenchmarks()
        : this(10_000, Environment.ProcessorCount)
    {
    }

    private List<string> _list;

    [Params(10_000, 100_000, 1_000_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _list = Enumerable.Range(1, Count).Select(n => n.ToString()).ToList();
    }

    [Benchmark]
    public void String_Intern()
    {
        _list.AsParallel().ForAll(static s => string.Intern(s));
    }

    [Benchmark]
    public void StringCache_Intern()
    {
        _list.AsParallel().ForAll(static s => StringCache.Intern(s));
    }

    public void Validate_String_Intern()
    {
        var sw = Stopwatch.StartNew();
        int interned = 0;
        foreach (var i in _list)
        {
            if (i == string.IsInterned(i))
            {
                interned++;
            }
        }

        Console.WriteLine($"SI.Count: {_list.Count}. Interned: {interned} in {sw.Elapsed}");
    }

    private static void ThrowArgumentException(string i)
    {
        throw new ArgumentException($"The string {i} is not interned!");
    }

    // [Benchmark]
    public void CustomString_Intern()
    {
        _list.AsParallel().WithDegreeOfParallelism(_dop).ForAll(static s => CustomStringIntern.Intern(s));
    }

    public void Validate_CustomString_Intern()
    {
        var sw = Stopwatch.StartNew();
        int interned = 0;
        foreach (var i in _list)
        {
            if (i == CustomStringIntern.IsInterned(i))
            {
                interned++;
            }
        }

        Console.WriteLine($"CSI.Count: {_list.Count}. Interned: {interned} in {sw.Elapsed}");
    }

    

    public void Validate_ConcurrentDictionary_Intern()
    {
        var sw = Stopwatch.StartNew();
        int interned = 0;
        foreach (var i in _list)
        {
            if (i == StringCache.IsInterned(i))
            {
                interned++;
            }
        }

        Console.WriteLine($"CD.Count: {_list.Count}. Interned: {interned} in {sw.Elapsed}");
    }

    // [Benchmark]
    //public void String_Table()
    //{
    //    _list.AsParallel().WithDegreeOfParallelism(_dop).ForAll(static s => StringTable.Add(s));
    //}

    //public void Validate_StringTable_Intern()
    //{
    //    var sw = Stopwatch.StartNew();
    //    int interned = 0;
    //    foreach (var i in _list)
    //    {
    //        if (i == StringTable.IsInterned(i))
    //        {
    //            interned++;
    //        }
    //    }

    //    Console.WriteLine($"ST.Count: {_list.Count}. Interned: {interned} in {sw.Elapsed}");
    //}
}