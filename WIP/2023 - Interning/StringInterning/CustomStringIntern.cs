﻿using Internal.TypeSystem;

namespace StringInterning;

public static class CustomStringIntern
{
    public static string Intern(string str)
    {
        //ArgumentNullException.ThrowIfNull(str);

        return InternTable.GetOrCreateValue(str);
    }

    public static string? IsInterned(string str)
    {
        //ArgumentNullException.ThrowIfNull(str);

        string canonicalString;
        if (!InternTable.TryGetValue(str, out canonicalString))
            return null;
        return canonicalString;
    }

    private sealed class StringInternTable : LockFreeReaderHashtable<string, string>
    {
        protected sealed override bool CompareKeyToValue(string key, string value) => key == value;
        protected sealed override bool CompareValueToValue(string value1, string value2) => value1 == value2;
        protected sealed override string CreateValueFromKey(string key) => key;
        protected sealed override int GetKeyHashCode(string key) => key.GetHashCode();
        protected sealed override int GetValueHashCode(string value) => value.GetHashCode();
    }

    private static StringInternTable InternTable
    {
        get
        {
            if (s_lazyInternTable == null)
            {
                StringInternTable internTable = new StringInternTable();
                internTable.AddOrGetExisting(string.Empty);
                Interlocked.CompareExchange<StringInternTable?>(ref s_lazyInternTable, internTable, null);
            }
            return s_lazyInternTable;
        }
    }

    private static volatile StringInternTable? s_lazyInternTable;
}

