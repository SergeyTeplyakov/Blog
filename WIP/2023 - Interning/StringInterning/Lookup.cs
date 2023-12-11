using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using BenchmarkDotNet.Attributes;

namespace StringInterning;

[SimpleJob(iterationCount: 1_000)]
public class LookupBenchmark
{
    private List<string> Keys;
    private List<string> Values;
    private static Random random = new Random(42);

    [Params(10_000, 100_000)]
    public int KeyCount { get; set; }

    [Params(10, 100)]
    public int ValueCount { get; set; }

    private Lookup<string, string> OldLookup;
    private Lookup<string, string> NewLookup;

    [GlobalSetup]
    public void Setup()
    {
        Keys = Enumerable.Range(1, KeyCount).Select(n => n.ToString()).ToList();
        Values = Enumerable.Range(1, ValueCount).Select(n => n.ToString()).ToList();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        OldLookup = new Lookup<string, string>(StringComparer.OrdinalIgnoreCase, StringComparer.OrdinalIgnoreCase);
        NewLookup = new Lookup<string, string>(StringComparer.OrdinalIgnoreCase, StringComparer.OrdinalIgnoreCase, useSharedLock: false);
    }

    [Benchmark]
    public void Add_SharedLock()
    {
        AddEverything(OldLookup);
    }

    [Benchmark]
    public void Add_DifferentLock()
    {
        AddEverything(NewLookup);
    }

    private void AddEverything(Lookup<string, string> lookup)
    {
        for (int i = 0; i < 10 * Keys.Count; i++)
        {
            var keyIdx = random.Next(Keys.Count);
            var valueIdx = random.Next(Values.Count);
            lookup.AddValue(Keys[keyIdx], Values[valueIdx]);
        }
    }
}
public static class LookupStress
{
    public static async Task Stress(CancellationToken token)
    {
        var lookup = new Lookup<string, string>(StringComparer.Ordinal, StringComparer.Ordinal);
        int count = 10_000;
        List<string> keys = Enumerable.Range(1, count).Select(n => n.ToString()).ToList();
        var rnd = new Random();
        int writersCount = 8;
        var values = Enumerable.Range(1, writersCount).Select(n => n.ToString()).ToList();

        //var reader = Enumerable.Range(1, readersCount).Select(_ => Task.Factory.StartNew(() =>
        //{
        //    while (!token.IsCancellationRequested)
        //    {
        //        var idx = rnd.Next(keys.Count);
        //        lookup.Contains(keys[idx]);
        //    }
        //}, TaskCreationOptions.LongRunning));


        var writers = Enumerable.Range(1, writersCount).Select(writer =>
        {
            return Task.Factory.StartNew(() =>
            {
                for (int i = 0; i < count; i++)
                {
                    var key = keys[i];
                    lookup.AddValue(key, writer.ToString());
                }
            }, TaskCreationOptions.LongRunning);
        }).ToList();

        //var remover = Task.Factory.StartNew(() =>
        //{
        //    while (!token.IsCancellationRequested)
        //    {
        //        var idx = rnd.Next(keys.Count);
        //        lookup.RemoveKey(keys[idx]);
        //    }
        //});

        //writers.AddRange(reader);
        //writers.Add(remover);
        await Task.WhenAll(writers);
        long totalCount = 0;
        foreach(var key in keys)
        {
            totalCount += lookup[key].Count;
        }

        if (totalCount != count * writersCount)
        {
            Console.WriteLine($"Keys.Count: {keys.Count}, Total: {totalCount}");
            Debugger.Launch();
        }
    }
}

/// <summary>
/// Helper class to create a lookup for parallel operation.
/// </summary>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <typeparam name="TValue">The type of the value.</typeparam>
internal class Lookup<TKey, TValue> : ILookup<TKey, TValue>
    where TKey : class
{
    ///// <summary>
    ///// The write lock to make sure there is only one thread is writing.
    ///// </summary>
    private readonly object writeLock;

    //private readonly ConcurrentDictionary<TKey, Grouping> content;

    /// <summary>
    /// The comparer of the values.
    /// </summary>
    private readonly IEqualityComparer<TValue> comparer;

    private readonly bool _useSharedLock;

    /// <summary>
    /// Initializes a new instance of the <see cref="Lookup{KeyT, ValueT}"/> class.
    /// </summary>
    /// <param name="keyComparer">The comparer of the keys.</param>
    /// <param name="valueComparer">The comparer of the values.</param>
    public Lookup(
        IEqualityComparer<TKey> keyComparer, IEqualityComparer<TValue> valueComparer,
        bool useSharedLock = false)
    {
        //writeLock = new object();
        content = new ConcurrentDictionary<TKey, Grouping>(keyComparer);
        //content = new Dictionary<TKey, Grouping>(keyComparer);
        comparer = valueComparer;
        _useSharedLock = useSharedLock;
    }

    /// <inheritdoc/>
    public int Count => content.Count;

    /// <inheritdoc/>
    IEnumerable<TValue> ILookup<TKey, TValue>.this[TKey key] => content[key];

    public Grouping this[TKey key] => content[key];


    //private readonly Dictionary<TKey, Grouping> content;
    private readonly ConcurrentDictionary<TKey, Grouping> content;

    //public bool AddValue(TKey key, TValue value)
    //{
    //    if (!content.TryGetValue(key, out var valueSet))
    //    {
    //        lock (writeLock)
    //        {
    //            if (!content.ContainsKey(key))
    //            {
    //                content.Add(key, new Grouping(key, comparer, value));
    //                return true;
    //            }
    //        }

    //        valueSet = content[key];
    //    }

    //    lock (valueSet)
    //    {
    //        return valueSet.Add(value);
    //    }
    //}

    public bool AddValue(TKey key, TValue value)
    {
        var valueSet = content.GetOrAdd(key, static (key, comparer) => new Grouping(key, comparer), comparer);
        lock (valueSet)
        {
            return valueSet.Add(value);
        }
    }
    /*
     *Performed 175 runs. 175 overall...
       Performed 211 runs. 386 overall...
       Performed 204 runs. 590 overall...
       Performed 190 runs. 781 overall...
       Performed 176 runs. 957 overall...
       Performed 155 runs. 1112 overall...
       Performed 155 runs. 1267 overall...
       Performed 144 runs. 1411 overall...
       Unhandled exception. System.Collections.Generic.KeyNotFoundException: The given key '1931' was not present in the dictionary.
     */

    ///// <summary>
    ///// Try to add value to a key. If the key exists, the value will be added into the value set of the key. Otherwise the value set will be created with the value provided.
    ///// </summary>
    ///// <param name="key">The key.</param>
    ///// <param name="value">the value to be added.</param>
    ///// <returns>A value indicating whether the value was added to the value set of the key. <see langword="true"/> means the value was added, otherwise it means the value  already exist in the value set of the key.</returns>
    //public bool AddValue2(TKey key, TValue value)
    //{
    //    if (!content.TryGetValue(key, out var valueSet))
    //    {
    //        lock (writeLock)
    //        {
    //            if (content.TryAdd(key, new Grouping(key, comparer, value)))
    //            {
    //                return true;
    //            }
    //        }

    //        valueSet = content[key];
    //    }
    //    //var valueSet = content.GetOrAdd(key, static (key, state) => new Grouping(key, state.comparer, state.value),
    //    //    factoryArgument: (comparer, value));

    //    lock (valueSet)
    //    {
    //        return valueSet.Add(value);
    //    }
    //}


    /// <inheritdoc/>
    public bool Contains(TKey key) => content.ContainsKey(key);

    /// <inheritdoc/>
    public IEnumerator GetEnumerator() => content.Select(x => x.Value).GetEnumerator();

    /// <inheritdoc/>
    IEnumerator<IGrouping<TKey, TValue>> IEnumerable<IGrouping<TKey, TValue>>.GetEnumerator() =>
        content.Select(x => x.Value).GetEnumerator();

    /// <summary>
    /// Helper class to create a grouping to hold the group inside a lookup partition.
    /// </summary>
    public class Grouping : IGrouping<TKey, TValue>
    {
        /// <summary>
        /// The content of the grouping.
        /// </summary>
        private readonly HashSet<TValue> content;

        /// <summary>
        /// Initializes a new instance of the <see cref="Grouping"/> class.
        /// </summary>
        /// <param name="key">The grouping key.</param>
        /// <param name="equalityComparer">The comparer of the value set.</param>
        /// <param name="initialValue">The value set initial value.</param>
        public Grouping(TKey key, IEqualityComparer<TValue> equalityComparer, TValue initialValue)
        {
            Key = key;
            content = new HashSet<TValue>(equalityComparer)
            {
                initialValue,
            };
        }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="Grouping"/> class.
        /// </summary>
        /// <param name="key">The grouping key.</param>
        /// <param name="equalityComparer">The comparer of the value set.</param>
        public Grouping(TKey key, IEqualityComparer<TValue> equalityComparer)
        {
            Key = key;
            content = new HashSet<TValue>(equalityComparer);
        }

        /// <inheritdoc/>
        public TKey Key { get; }

        /// <summary>
        /// Add a value into the value set.
        /// </summary>
        /// <param name="value">The value to be added.</param>
        /// <returns>A value indicating whether the value was added into the value set. <see langword="true"/> means the value was added into this value set, otherwise it means the value already exist in the value set.</returns>
        public bool Add(TValue value) => content.Add(value);

        public int Count => content.Count;

        /// <inheritdoc/>
        public IEnumerator<TValue> GetEnumerator() => content.GetEnumerator();

        /// <inheritdoc/>
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)content).GetEnumerator();
    }
}