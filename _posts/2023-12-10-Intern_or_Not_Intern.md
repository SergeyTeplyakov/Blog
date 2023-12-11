---
layout: post
title: String Interning - To Use or Not to Use? A Performance Question
categories: benchmarking
---

I recently join a new team and one of the projects was having a high memory footprint issues. There are a few mitigations put in place and one of them was to de-duplicate strings by using string interning.

When the application creates tens of millions of strings with a high repetition rate such optimization is quite helpful and in this case it was reducing the memory footprint by about 10-15%. But when I looked into the profiling data I've noticed that the string interning was a huge bottle neck and the application was spending about 96% of the execution time in spin locks inside the string table.

This presented an interesting challenge: while string de-duplication helped with memory usage, it also significantly hurt startup performance, as most calls to `string.Intern` were made during app initialization. Removing string interning indeed helped performance quite a lot, but I was cusious if another string de-duplication approaches might be better. So I've tried a naive one based on `ConcurrentDictionary<string, string>`.

```csharp
public static class StringCache
{
    private static ConcurrentDictionary<string, string> cache = new(StringComparer.Ordinal);

    public static string Intern(string str) => cache.GetOrAdd(str, str);

    public static void Clear() => cache.Clear();
}
```

The cache currently uses a static `ConcurrentDictionary<string, string`, but it can easily be made non-static and passed around as needed. Additionally, if we know that string de-duplication is only needed during application initialization, we can clear the cache once initialization is complete to avoid keeping transient strings that are not part of the final object graph. Having the ability to clear the cache solves one of the issues that a global string interning cache has.

However, performance of this naive implementation is a concern. To test performance, we need to be careful when benchmarking a global state like the string interning cache, since the benchmark is executed multiple times within the same process, which can skew the data. One solution is to clean a custom table on each iteration, but cleaning the string table cache requires running each iteration in a separate process.

But we need to start somewhere. So lets try this benchmark first:

```csharp
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
    _list.AsParallel().ForAll(static s => StringCache.TryIntern(s));
}
```

In this case we're measuring the read performance, which still might be a useful thing to check. Here are the results for .NET 8 (but they're pretty much the same for .NET Framework as well):

```
| Method             | Count   | Mean         | StdDev       | Allocated |
|------------------- |-------- |-------------:|-------------:|----------:|
| String_Intern      | 10000   |   3,463.7 us |     47.04 us |   4.04 KB |
| StringCache_Intern | 10000   |     114.5 us |      3.61 us |   4.01 KB |
| String_Intern      | 100000  |  39,546.8 us |  1,653.10 us |    4.1 KB |
| StringCache_Intern | 100000  |   1,371.8 us |    129.97 us |   4.03 KB |
| String_Intern      | 1000000 | 823,046.8 us | 16,736.25 us |   5.05 KB |
| StringCache_Intern | 1000000 |  32,094.0 us |  3,291.34 us |   4.07 KB |
```

Ignore the allocations since they're caused by PLINQ. The time looks bad! Why the built-in version is so slow?

To double check the runtime behavior (and to look the code under the profiler) I've decided to write a "simple" console app that calls de-duplication logic on 10M different strings multiple times. This is not the exact scenario our service has but it might be closer than the benchmark. 

```csharp
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
```

The results:
```
Custom string interning is done in 00:00:03.9975182
String interning is done in 00:01:13.9881888
```

The difference is still huge (like 15-x). And by playing with the number of iterations, I got different ratios between the string interning and custom cache. It seems that the string interning is drastically slower (like 20-30x) in terms of reads, but "just" 2-3x slower in terms of writes.

And most importantly the string interning performance issue is not theoretical. After switching from the string interning to the custom `StringCache` the startup time for our service dropped 2-x! With just a simple change! Plus we got an ability to clean-up the cache to get rid of the cached strings that are not part of the final state.

But before closing this topic, lets run the same custom benchmark with Native AOT:

```
Custom string interning is done in 00:00:03.3062479
String interning is done in 00:00:05.6756519
```

Why? The thing is that the string interning logic for both Full Framework and .NET Core is implemented in native code at [`StringLiteralMap::GetInternedString`](https://github.com/dotnet/runtime/blob/1aae18a21ebb5f08a2a734cfe31ba4bd00b2ad7b/src/coreclr/vm/stringliteralmap.cpp#L220). String interning for native AOT has a different implementation and is [written in C#](https://github.com/dotnet/runtime/blob/1aae18a21ebb5f08a2a734cfe31ba4bd00b2ad7b/src/coreclr/nativeaot/System.Private.CoreLib/src/System/String.Intern.cs#L12)! The new implementation uses [`LockFreReaderHashtable<TKey,TValue>`](https://github.com/dotnet/runtime/blob/425cfb9f9f4b8b8235772106f31eb2342238f6eb/src/coreclr/tools/Common/TypeSystem/Common/Utilities/LockFreeReaderHashtable.cs#L23) which is used by the runtime in many other places. And that implementation is **WAY MORE** efficient than the native string interning implementation. It is somewhat comparable with `ConcurrentDictionary` in terms of perf, but requires less memory for keeping all the records.

And running the same benchmark with Native AOT gives drastically different results as well:

```
| Method             | Count   | Mean        | Error       | StdDev      | Allocated |
|------------------- |-------- |------------:|------------:|------------:|----------:|
| String_Intern      | 10000   |    196.8 us |     3.82 us |     3.92 us |   4.11 KB |
| StringCache_Intern | 10000   |    211.9 us |     4.15 us |     5.67 us |   4.11 KB |
| String_Intern      | 100000  |  1,680.1 us |    47.58 us |   140.28 us |   4.14 KB |
| StringCache_Intern | 100000  |  2,102.1 us |    86.83 us |   250.53 us |   4.13 KB |
| String_Intern      | 1000000 | 31,059.8 us | 1,349.33 us | 3,827.82 us |   4.16 KB |
| StringCache_Intern | 1000000 | 40,368.6 us | 1,279.83 us | 3,713.02 us |   4.15 KB |
```

We can't see the difference in memory consumption, since these benchmarks are essentially the stable state benchmarks, when all the records are already added to the string caches.

# Conclusion
* String interning in non-native AOT is very slow and can drastically affect your application performance.
* If you call `string.Intern` in your code you probably should think if you really should.
* A very naive custom string cache based on `ConcurrentDictionary<string, string>` is drastically faster then the string interning cache and gives you an opportunity to clean-up the cache.
* If your app runs as a Native AOT app, then the performance is good, and the only drawback of the bulit-in string interning is an inability to clean it.