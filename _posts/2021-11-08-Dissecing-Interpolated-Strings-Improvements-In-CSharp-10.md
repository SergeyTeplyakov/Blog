# Dissecting interpolated strings improvements in C# 10

There are many interesting features coming into C# 10 and my favorite one is the improvements of interpolated strings.
It may sound weird that the #1 feature for me is not a new one but an improvement of an existing one. That's because I do care a lot about performance and the interpolated string improvements in C# 10 will make my code faster without any changes from my side. But that's not it. The new design is not only allows creating strings faster, but it also allows skipping the string creation altogether!

First, a bit of history. String interpolation is a quite popular concept that was added to C# 6 for creating strings with embedded expressions:

```csharp
int n = 42;
string s = $"n == {n}"; // s is "n == 42"
```

But in the original form, this feature had some performance-related issues caused by a fairly naive implementation. To be fair, the language spec was intentionally vague in terms of how exactly the compiler should translate an interpolated string, so it was possible to have a better and more efficient code generation in the future.

Before C# 10, the compiler used to have a farily simple transformation. The code like `string s = $"n == {n}"` was simply translated `string s = string.Format("n == {0}", n)`. 

Here are a few issues with this approach:
* Extra step is required at runtime to parse the format string, even though the format is known by the compiler.
* Boxing will happen if the captured expression is of a value type.
* `ToString` call on a captured expression is required, meaning that a bunch of transient strings will be allocated in the process.
* No support for constant folding. If the expression is a constant expression, the string will still be constructed at runtime and not at compile time.
* The string is created eagerly and there is no way to avoid string construction if it's not being used at runtime.

Starting from C# 10 all of those issues are solved! 

Let's look at a practical example that will show most of the benefits of the new implementation. Let's say we have a very simple argument validation library, like [RuntimeContracts](https://github.com/SergeyTeplyakov/RuntimeContracts) and we want to check some invariants by calling `Contract.Assert(predicate, message)` (*). And if the predicate is `false` we want the contract to fail with an optional user-defined error message:

(*) The type name is intentionally the same as in `System.Diagnostics.Contracts` namespace, but the "runtime contracts" do not require any tools for rewriting code before using them.

```csharp
private int _state; // can be changed.

public void DoSomething(int n)
{
    for (int i = 0; i < n; i++)
    {
        Contract.Assert(_state == 42, $"n must be 42 but was {_state}");
    }
}
```

Can you see the issue here? The check is called in the loop and the message will be created on each iteration! This can be very problematic and can cause real issues if the code is on an application's hot path. Let see how we can avoid allocations with the interpolated string improvements.

## Interpolated String Handler basics

Instead of "lowering" an interpolated string to `string.Format` call, the C# 10 compiler now uses "Interpolated String Handlers" pattern.

The handler is a type that follows a specific pattern: it must have a constructor that takes at least 2 arguments: `literalLength` and `formattedCount`, and may take some optional arguments as well as we'll see later, and must have at least two methods: `AppendLiteral(string)` and `AppendFormatted<T>(T)`. The type must also be marked with a special attribute - `InterpolatedStringHandlerAttribute`.

Starting from C# 6 an interpolated string expression was assignable to `string` or `System.FormattableString` and now it can be assigned to any type that follows the aforementioned pattern. Starting with .NET 6 there is a built-in handler called [`DefaultInterpolatedStringHandler`](https://github.com/dotnet/runtime/blob/f54ab52d24ee524a246e463d754e526832850d4a/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/DefaultInterpolatedStringHandler.cs) and by default, the compiler "lowers" an interpolated string expression to it.

```csharp
int n = 0;

// s is System.String
var s = $"n == {n}";

// s2 is of type 'DefaultInterploatedStringHandler'
DefaultInterpolatedStringHandler s2 = $"n == {n}";
```

If you decompile this code you'll see the changes in action:

```csharp
int i = 0;

DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(5, 1);
defaultInterpolatedStringHandler.AppendLiteral("n == ");
defaultInterpolatedStringHandler.AppendFormatted(i);
// s is System.String
string s = defaultInterpolatedStringHandler.ToStringAndClear();

defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(5, 1);
defaultInterpolatedStringHandler.AppendLiteral("n == ");
defaultInterpolatedStringHandler.AppendFormatted(i);
// s2 is of type 'DefaultInterploatedStringHandler'
DefaultInterpolatedStringHandler s2 = defaultInterpolatedStringHandler;
```

The `DefaultInterpolatedStringHandler` is more efficient compared to a regular `string.Format` call in multiple ways:
* No runtime work is required to parse the formatted string. Instead, each placeholder corresponds to a call to `AppendFormatted`.
* The char array used internally for building a final string is rented from an array pool, so in a steady state only a final string is allocated.
* A generic overload of `AppendFormatted<T>(T)` avoids boxing when value types are captured in an interpolated string expression.
* `ISpanFormattable` type is respected, and that allows writing an object's string representation into a `Span<char>` without allocating a separate string. (many built-in types do implement this interface already).
* There is an overload for `AppendFormatted(ReadOnlySpan<char>)` that allows capturing the span of char in the interpolated expression that was not possible before: `string s = $"Str={strArg.AsSpan().Trim()}"`.
* Constant folding is also supported and if all the expressions are known at compile time the final string will be produced by the compiler.

Here is a small benchmark that shows the differences:

```csharp
[MemoryDiagnoser]
public class PerformanceBenchmark
{
    private readonly DateTime _when = DateTime.Now;
    private readonly long _v1 = 1;
    private readonly long _v2 = 2;
    private readonly long _v3 = 3;

    [Benchmark]
    public string StringFormat()
    {
        return string.Format("When: {0}, V1={1}, V2={2}, V3={2}", _when, _v1, _v2, _v3);
    }

    [Benchmark]
    public string NewInterpolation()
    {
        return $"When: {_when}, V1={_v1}, V2={_v2}, V3={_v2}";
    }
}
```

```
|           Method |     Mean |    Error |  StdDev |  Gen 0 | Allocated |
|----------------- |---------:|---------:|--------:|-------:|----------:|
|     StringFormat | 518.0 ns | 10.34 ns | 8.63 ns | 0.0648 |     272 B |
| NewInterpolation | 392.7 ns |  7.55 ns | 6.70 ns | 0.0286 |     120 B |
```

As we can see, the new implementation is 25% faster and allocates less than half of the `string.Format` version.

## What is `ISpanFormattable`?

A default API for getting a string representation of an object is `Object.ToString()` that every (**) type supports. But calling `ToString` by definition causes an extra allocation of a resulting string. And if you need to compose a string from multiple objects it may cause a lot of excessive allocations. To avoid this, many high performance applications instead of using `Object.ToString` also have `void ToString(StringBuilder)` for constructing a composed text without creating an extra string each time.

(**) Not every type per se, because pointers are types and they don't support `ToString()`. And ref structs must define `ToString` methods explicitly because the base version defined in `System.ValueType` is not accessible for them.

But starting with .NET 6 we have `ISpanFormattable` interface that derives from `IFormattable` and has one extra method:

```csharp
namespace System;

public interface ISpanFormattable : IFormattable
{
    /// <summary>
    /// Tries to format the value of the current instance into the provided span of characters.
    /// </summary>
    bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider);
}
```

`ISpanFormattable` allows writing an object's text representation into a `destination` `Span<char>` if the destination is large enough to accept it.

The API of this interface looks scary and maybe labor-intensive to do this manually all the time. Luckily, we can use interpolated strings to write into a `Span<char>` as well!

```csharp
public readonly struct Point : ISpanFormattable
{
    public int X { get; }
    public int Y { get; }
    public Point(int x, int y) => (X, Y) = (x, y);

    public override string ToString() =>
        ToString(format: null, formatProvider: null);

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider) =>
        destination.TryWrite($"X={X}, Y={Y}", out charsWritten);

    public string ToString(string format, IFormatProvider formatProvider) =>
        return string.Create(formatProvider, $"X={X}, Y={Y}");
}
```

In this case, `TryFormat` method calls `MemoryExtensions.TryWrite` that will do exactly what we want: it will try adding a newly produced string into a target span if the destination has enough space.

Besides writing to a span, .NET 6 also updated the `StringBuilder` API like `Append` and `AppendLine` to leverage new interpolated string handlers.

The calls like `stringBuilder.AppendLine($"X = {X}, Y = {Y}");` used to create a separate string that was added to a `StringBuilder` instance. But now both `StringBuilder.Append` and `StringBuilder.AppendLine` are taking `AppendInterpolatedStringHandler` that appends an interpolated string in a very efficient way.

Ok, now it's time to create a custom handler that will solve the issue that we had with our `Contract.Assert` method.

## Custom Interpolated String Handler

Let's start with a special handler type:

```csharp
[InterpolatedStringHandler]
public ref struct ContractMessageInterpolatedStringHandler
{
    // Will delegate all the work here!
    private DefaultInterpolatedStringHandler _handler;

    public ContractMessageInterpolatedStringHandler(int literalLength, int formattedCount, bool predicate, out bool handlerIsValid)
    {
        _handler = default;

        if (predicate)
        {
            // If the predicate is evaluated to 'true', then we don't have to construct a message!
            handlerIsValid = false;
            return;
        }

        handlerIsValid = true;
        _handler = new DefaultInterpolatedStringHandler(literalLength, formattedCount);
    }

    public void AppendLiteral(string s) => _handler.AppendLiteral(s);

    public void AppendFormatted<T>(T t) => _handler.AppendFormatted(t);

    public override string ToString() => _handler.ToStringAndClear();
}
```

Now we can change the `Contract.Assert` signature to take the handler, and by using `InterpolatedStringHandlerArgument` we can "tell" the compiler to pass the `predicate` parameter to the constructor of the handler as well:

```csharp
public static class Contract
{
    // "Telling" the compiler to pass the 'predicate' parameter to the handler.
    public static void Assert(bool predicate, [InterpolatedStringHandlerArgument("predicate")] ref ContractMessageInterpolatedStringHandler handler)
    {
        if (!predicate)
        {
            throw new Exception($"Precondition failed! Message:{handler.ToString()}");
        }
    }
}
```

Let's check what will happen at runtime:

```csharp
int n = 0;
// Contract is not violated! No messages will be constructed!
Contract.Assert(true, $"No side effects! n == {++n}");
```

The output will be:
```
n == 0
```

The compiler emitted the following code:

```csharp
bool predicate = true;
bool handlerIsValid;
var handler = new ContractMessageInterpolatedStringHandler(22, 1, predicate, out handlerIsValid);
if (handlerIsValid)
{
    handler.AppendLiteral("No side effects! n == ");
    handler.AppendFormatted(++i);
}

Contract.Requires(predicate, ref handler);
```

The compiler generates the code that creates an instance of `ContractMessageInterpolatedStringHandler` and passes the length of a string literal and the number of slots. It also passes the predicate flag that the handler checks and sets 'handlerIsValid` depending on its value. And if the handler is invalid (because the assertion is not violated) we completely skip the message construction!

And now we can call `Contract.Assert` with a custom error message in a loop and not be afraid of performance issues caused by excessive message construction!

```csharp
private int _state; // can be set and changed.

public void DoSomething(int n)
{
    for (int i = 0; i < n; i++)
    {
        // No performance issues anymore! The string will never be constructed if the assertion is not violated!
        Contract.Assert(_state == 42, $"n must be 42 but was {_state}");
    }
}
```

## Support for older .NET Frameworks

As always, the C# compiler uses the pattern-based approach for the new interpolated string improvements and it means that we can define required attributes manually in our code (but still put them into `System.Runtime.CompilerServices` namespace) and use the new behavior with the older frameworks.

## `await`-ing in interpolated strings

One thing that you may have noticed is that the interpolation string handlers are ref-structs and you may remember that ref-structs have some restrictions: they can't be "allocated" in the managed heap so they can't be embedded into other non-ref structs or objects. And because of that, they can't be used in async methods.

But the following code was working fine before and should be working just fine in C# 10:

```csharp
public async Task FooAsync()
{
    string s = $"x = {await Task.Run(() => 42)}";
}
```

The language designers knew that the async case would be problematic. So they had a few options: 1) make handlers non-ref structs or 2) use different code generation when async code is involved. They decided to go with the second option and keep the handlers as ref structs and fallback in the async case to the old option and generate `string.Format` call instead.

## Conclusion
* Interpolated strings in C# 10 are faster and produce 0 extra allocations besides the final string.
* The interpolated string handlers allow creating a very expressive, yet efficient API like one we had seen in `Contract.Assert`. The same "trick" can be used by logging frameworks to avoid string creation if the logging level is off.
* Interpolated strings in C# 10 support capturing `ReadOnlySpan<char>` like `string s = "foo bar "; string str = $"Trimmed: {s.AsSpan().Trim()}";`.
* `ISpanFormattable` is a very handy interface that allows an object's string representation to be written into a span without allocating a string.
* `MemoryExtensions.TryWrite` is a building block for implementing `ISpanFormattable` interface using interpolated strings.
* `StringBuilder.Append` and `AppendLine` were updated in .NET 6 to use interpolated string handlers for higher efficiency.