using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;


namespace Azure.CorrelationPlatform.Common.StringsInterning;

/// <summary>
/// The object of this class is used as a mechanism to intern strings.
/// The maximum number of interned strings can be set and implementation will make sure that this limit is not
/// exceed too much. After reaching minimum limit it will ramp up new cache. The idea is that frequent strings will
/// still be in the new cache, while rare once will be cleared. This implementation is fastest (of which I could think).
///
/// The cache will also have a maximum age in which it will switch to a new cache.  The same ramp strategy is taken
/// to begin preloading the next cache prior to switching over so frequent strings are still captured.
///
/// THIS IS A COPY OF STRINGS INTERNING CACHE TAKEN FROM THE GenevaMetrics.Datastructures with changes around correctly calculating taken memory by the cache instance.
/// </summary>
public sealed class StringsInterningCache
{
    private readonly long _minLimit;
    private readonly long _maxLimit;
    private readonly TimeSpan _rampUpStartThreshold;
    private readonly TimeSpan _recyclePeriod;
    private readonly IDateTimeService? _dateTimeService;
    private readonly bool _timeBaseRecycleEnabled;
    private readonly Action? _recycleCallback;
    private long _currentCount;
    private long _nextCount;
    private long _currentSizeInBytes;
    private long _nextSizeInBytes;
    private int _locker;
    private long _totalCalls;
    private long _rampUpBeginTicks;
    private long _recycleTicks;
    private int _beginRampUp;
    private int _performRecycle;
    private ConcurrentDictionary<string, string> _cache = new ConcurrentDictionary<string, string>();
    private ConcurrentDictionary<string, string> _nextCache = new ConcurrentDictionary<string, string>();

    public static StringsInterningCache Instance { get; } =
        new StringsInterningCache(minLimit: 128_000_000, maxLimit: 129_000_000);

    /// <summary>
    /// Initializes a new instance of the <see cref="StringsInterningCache" /> class.
    /// </summary>
    /// <param name="minLimit">Number of strings in the cache after which replacement process should start.</param>
    /// <param name="maxLimit">Maximum number of strings in cache. This number can be exceeded insufficiently.</param>
    public StringsInterningCache(long minLimit, long maxLimit)
        : this(minLimit, maxLimit, TimeSpan.Zero, TimeSpan.Zero, dateTimeService: null, recycleCallback: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StringsInterningCache" /> class.
    /// </summary>
    /// <param name="minLimit">Number of strings in the cache after which replacement process should start.</param>
    /// <param name="maxLimit">Maximum number of strings in cache. This number can be exceeded insufficiently.</param>
    /// <param name="rampUpStartThreshold">The preiod prior to recycling that ramp up of the next interning cache will begin.</param>
    /// <param name="recyclePeriod">The recycle period to transition caches.</param>
    /// <param name="dateTimeService">The date time service.</param>
    /// <param name="recycleCallback">A callback to be notified when the cache is recycled.</param>
    public StringsInterningCache(long minLimit, long maxLimit, TimeSpan rampUpStartThreshold, TimeSpan recyclePeriod, IDateTimeService? dateTimeService, Action? recycleCallback)
    {
        _minLimit = minLimit;
        _maxLimit = maxLimit;
        _rampUpStartThreshold = rampUpStartThreshold;
        _recyclePeriod = recyclePeriod;
        _dateTimeService = dateTimeService;
        _timeBaseRecycleEnabled = _recyclePeriod.TotalSeconds > 0 && dateTimeService != null;
        LastRecycledTimeUtc = DateTime.MinValue;
        _recycleCallback = recycleCallback;

        if (_timeBaseRecycleEnabled && recyclePeriod.TotalSeconds > 0 && rampUpStartThreshold > recyclePeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rampUpStartThreshold),
                $"Ramp up threshold cannot be larger than recycle period. RampUpSec:{rampUpStartThreshold.TotalSeconds}, RecyclePeriodSec:{recyclePeriod.TotalSeconds}");
        }

        ComputeNextRecyclePeriod();
    }

    /// <summary>
    /// Gets the last recycled time.
    /// </summary>
    public DateTime LastRecycledTimeUtc { get; private set; }

    /// <summary>
    /// Gets the current number of strings in the cache.
    /// </summary>
    public long CacheSize => _currentCount;

    /// <summary>
    /// Gets the number of bytes of strings in the cache.
    /// </summary>
    public long CacheSizeInBytes => _currentSizeInBytes + _nextSizeInBytes;

    /// <summary>
    /// Gets or sets how frequently the current time is tested against when the cache should be ramped up or recycled.
    /// </summary>
    internal int TimeBasedRecycleCheckFrequency { get; set; } = 10000;

    /// <summary>
    /// Clears the cache by recreating the underlaying dictionaries.
    ///
    /// This method is not thread-safe so the caller should make sure that
    /// the call does not interfere with other method calls on this object.
    /// </summary>
    public void Clear()
    {
        _cache = new ConcurrentDictionary<string, string>();
        _currentCount = 0;
        _currentSizeInBytes = 0;

        _nextCache = new ConcurrentDictionary<string, string>();
        _nextCount = 0;
        _nextSizeInBytes = 0;
    }

    /// <summary>
    /// Lookups the string in the cache and returns corresponding interned string.
    /// </summary>
    /// <param name="value">String to lookup.</param>
    /// <returns>Similar interned string.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string? Intern(string value)
    {
        // each string in c# is stored in Unicode format in memory, hence size in bytes is twice the length
        const int BytesInUtf16Char = 2;

        // each string object has 24 bytes overhead
        const int StringObjectOverheadSize = 24;

        // each concurrent dictionary has the following internal overhead per node/record
        const int ConcurrentDictionaryInternalRecordOverheadSize = 68;

        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var callCount = Interlocked.Increment(ref _totalCalls);
        var result = _cache.GetOrAdd(value, value);

        // Determine if time based recycling needs to be checked
        var performRecycleTimeCheck = TimeBasedRecycleCheckFrequency > 0 && callCount % TimeBasedRecycleCheckFrequency == 0;

        if (ReferenceEquals(result, value))
        {
            _ = Interlocked.Increment(ref _currentCount);

            var size = value.Length * BytesInUtf16Char;

            size += StringObjectOverheadSize + ConcurrentDictionaryInternalRecordOverheadSize;

            _ = Interlocked.Add(ref _currentSizeInBytes, size);
        }

        if (Thread.VolatileRead(ref _currentCount) > _minLimit ||
            Thread.VolatileRead(ref _beginRampUp) == 1)
        {
            if (_nextCache.TryAdd(result, result))
            {
                _ = Interlocked.Increment(ref _nextCount);

                var size = result.Length * BytesInUtf16Char;

                size += StringObjectOverheadSize + ConcurrentDictionaryInternalRecordOverheadSize;

                _ = Interlocked.Add(ref _nextSizeInBytes, size);
            }
        }
        else if (_timeBaseRecycleEnabled &&
                 performRecycleTimeCheck &&
                 Thread.VolatileRead(ref _beginRampUp) != 1 &&
                 _dateTimeService!.UtcNow.Ticks > Thread.VolatileRead(ref _rampUpBeginTicks))
        {
            _ = Interlocked.Exchange(ref _beginRampUp, 1);
        }

        if (Thread.VolatileRead(ref _currentCount) > _maxLimit ||
            Thread.VolatileRead(ref _performRecycle) == 1)
        {
            if (Interlocked.CompareExchange(ref _locker, 1, 0) == 0)
            {
                try
                {
                    if (Thread.VolatileRead(ref _currentCount) > _maxLimit ||
                        Thread.VolatileRead(ref _performRecycle) == 1)
                    {
                        LastRecycledTimeUtc = _dateTimeService?.UtcNow ?? DateTime.UtcNow;
                        ComputeNextRecyclePeriod();
                        var temp = _cache;
                        _ = Interlocked.Exchange(ref _currentCount, _nextCount);
                        _ = Interlocked.Exchange(ref _currentSizeInBytes, _nextSizeInBytes);
                        _cache = _nextCache;
                        _ = Interlocked.Exchange(ref _nextCount, 0);
                        _ = Interlocked.Exchange(ref _nextSizeInBytes, 0);
                        _nextCache = temp;
                        _nextCache.Clear();

                        if (_recycleCallback != null)
                        {
#pragma warning disable CA1031 // Do not catch general exception types
                            try
                            {
                                _recycleCallback();
                            }
                            catch (Exception)
                            {
                                // Do nothing on callback throwing.
                            }
#pragma warning restore CA1031 // Do not catch general exception types
                        }
                    }
                }
                finally
                {
                    _ = Interlocked.Exchange(ref _locker, 0);
                }
            }
        }
        else if (_timeBaseRecycleEnabled &&
                performRecycleTimeCheck &&
                Thread.VolatileRead(ref _performRecycle) != 1 &&
                _dateTimeService!.UtcNow.Ticks > Thread.VolatileRead(ref _recycleTicks))
        {
            _ = Interlocked.Exchange(ref _performRecycle, 1);
        }

        return result;
    }

    /// <summary>
    /// Computes the next recycle periods if enabled.
    /// </summary>
    private void ComputeNextRecyclePeriod()
    {
        if (_timeBaseRecycleEnabled)
        {
            var recycleTime = _dateTimeService!.UtcNow.Add(_recyclePeriod);
            var rampUpStart = recycleTime - _rampUpStartThreshold;
            _ = Interlocked.Exchange(ref _rampUpBeginTicks, rampUpStart.Ticks);
            _ = Interlocked.Exchange(ref _recycleTicks, recycleTime.Ticks);
            _ = Interlocked.Exchange(ref _beginRampUp, 0);
            _ = Interlocked.Exchange(ref _performRecycle, 0);
        }
    }
}

public interface IDateTimeService
{
    public DateTime UtcNow => DateTime.Now;
}