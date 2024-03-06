using System.Collections.Concurrent;

namespace StringInterning;

public static class StringCache
{
    private static ConcurrentDictionary<string, string> cache = new(StringComparer.Ordinal);

    public static string Intern(string str)
    {
        return cache.GetOrAdd(str, str);
    }

    public static string? IsInterned(string str)
    {
        cache.TryGetValue(str, out var result);
        return result;
    }
}

public static class StringCacheDefaultCDCtor
{
    private static ConcurrentDictionary<string, string> cache = new();

    public static string Intern(string str)
    {
        return cache.GetOrAdd(str, str);
    }

    public static string? IsInterned(string str)
    {
        cache.TryGetValue(str, out var result);
        return result;
    }
}