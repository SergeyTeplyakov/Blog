namespace NetStandardLib;

// netstandard2.0 project with PolySharp to enable all C# features
public record Response(DateTime When, int Id = 1, int X = 2, int Y = 3)
{
    public override string ToString()
    {
        return $"{nameof(When)}: {When}, {nameof(Id)}: {Id}, {nameof(X)}: {X}, {nameof(Y)}: {Y}";
    }
}