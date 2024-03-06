using BenchmarkDotNet.Attributes;

namespace StringInterning;

public class CollectionLiterals
{
    //[Benchmark]
    public void InitializeArrayWithCollectionLiteral()
    {
        int[] a = [1,2,3,4,5];
    }

    [Benchmark]
    public void InitializeListWithCollectionLiteral()
    {
        List<int> l = [1,2,3,4,5];
    }

    [Benchmark]
    public void InitializeListWithCollectionInitialization()
    {
        List<int> l = new () {1, 2, 3, 4, 5};
        ReadOnlySpan<int> other = [1, 2, 3];
        
    }

    [Benchmark]
    public void AddRangeIEnumerable()
    {
        List<int> list = [1, 2, 3];
    }
}