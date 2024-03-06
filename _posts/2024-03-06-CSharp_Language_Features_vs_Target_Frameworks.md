---
layout: post
title: C# Language Features vs. Target Frameworks
categories: C#
---

If you check the official [C# language versioning page](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/configure-language-version) you might think that there is a very strong relationship between the target framework and the C# language version.

And indeed, if you won't specify the C# language version implicitly in the project file the version would be picked based on the target framework: C# 12 for .net8, C#11 for .net7, and C# 7.3 for Full Framework:

![CSharp_Lang_Versions](/Blog/assets/2024_csharp_lang_versions.png "C# Language versions vs. Target Frameworks")

And even though the mapping just specifies the defaults, some people believe that the mapping is fixed and, for instance, if you got stuck with Full Framework, you also got stuck with C# 7.3. But this is not the case.

The actual relationship between the C# language version and the target framework is more delicate.

There are 3 ways how the feature might relate to the target framework.

* **Just works**. Some features like enhanced pattern matching, readonly struct members, enhanced usings and static lambdas, just work out of the box. Set the right `langVersion` in a project file and a new feature works regardless of the target framework.
* **Requires a special type definition**. Other features, such as new interpolated strings, non-nullable types, ranges and some others require special types to be discoverable by the compiler. These special types are added to .net core release that corresponds to particular C# version, but you can add them manually to your compilation (see examples below) to get the features working.
* **Runtime specific**. And only a small fraction of all the new language features do require the runtime support. Features like Default Interface Implementations, Inline Arrays or ref fields won't compile if the target framework doesn't support it and if you'll try, you'll get an error: `Error CS9064 : Target runtime doesn't support ref fields`.

The first and the last cases are quite obvious, but the second one requires a bit of extra information. The C# compiler requires the special types to be available during compilation of the project for the feature to be usable, and it doesn't care where the type definition is coming from: from the target framework, from a nuget package, or be part of the project itself.

Here is an example of using init-only setters (available since C# 9) in a project targeting netstandard 2.0:

```csharp
// Project targets netstandard2.0 or net472
public record MyRecord
{
    // System.Runtime.CompilerServices.IsExternalInit class is required.
    public int X { get; init; }
}

namespace System.Runtime.CompilerServices
{
    internal class IsExternalInit { }
}
```

But if you'll try to use some other features, like required members, you would have to add quite a bit of extra types to your compilation:

```csharp
public record class MyRecord
{
    // System.Runtime.CompilerServices.IsExternalInit class is required.
    public int X { get; init; }
    // System.Runtime.CompilerServices.RequiredMemberAttribute,
    // CompilerFeatureRequiredAttribute and
    // System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute are required
    public required int Y { get; set; }    
}

namespace System.Runtime.CompilerServices
{
    internal class IsExternalInit { }
    internal class RequiredMemberAttribute : System.Attribute { }
    internal sealed class CompilerFeatureRequiredAttribute(string featureName) : System.Attribute
    {
        public string FeatureName { get; set; } = featureName;
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    internal class SetsRequiredMembersAttribute : System.Attribute { }
}
```

Adding all the attributes manually to every project is very tedious, so you can rely on some MSBuild magic to add a set of known files based on the target framework. Or you could just use something like [PolySharp](https://github.com/Sergio0694/PolySharp) that uses source generation to add all the required types regardless of the target framework.
## InternalsVisisbleTo catch

There is an issue with the case shown before. Let's say you have `A.csproj` targeting `netstandard2.0` and `A.Tests.csproj` targeting `net8.0` with `InternalVisibleTo("A.Tests")` inside `A.csproj`.

In this case, you won't be able to compile `A.Tests.csproj` with an error about duplicate member definition, since the type like `IsExternalInit` would be available from two places - from `A.csproj` and from `.net8.0` runtime library.

The solution is pretty simple: multitarget `A.csproj` and target both `netstandard2.0` and `net8.0`.

And here I want to show all the language features from C# 12 down to C# 8 with their requirements and a link to a github issue that explains the feature.
# C# 12 Features

| Language Feature                                                                                                           | Requirements                        |
| -------------------------------------------------------------------------------------------------------------------------- | ----------------------------------- |
| [ref-readonly parameters](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-12.0/ref-readonly-parameters.md) | No extra requirements (1)            |
| [Collection expressions](https://github.com/dotnet/csharplang/issues/5354)                                                 | No extra requirements (2)               |
| [Interceptors](https://github.com/dotnet/csharplang/issues/7009)                                                           | `InterceptsLocationAttribute` (3)       |
| [Inline Arrays](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-12.0/inline-arrays.md)                     | Runtime support is required: .net8+ |
| [nameof accessing instance members](https://github.com/dotnet/csharplang/issues/4037)                                      | No extra requirements               |
| [Using aliases for any types](https://github.com/dotnet/csharplang/issues/4284)                                            | No extra requirements               |
| [Primary Constructors](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-12.0/primary-constructors.md)       | No extra requirements               |
| [Lambda optional parameters](https://github.com/dotnet/csharplang/issues/6051)                                             | No extra requirements               |
| [Experimental Attribute](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-12.0/experimental-attribute.md)   | `ExperimentalAttribute` (4)         |

------
(1) ref-readonly parameters is an interesting feature. On one hand, it doesn't require any extra types to be declared manually, but it does rely on an extra type - `System.Runtime.CompilerServices.RequiresLocationAttribute`. But if the compilation is missing this type, the compiler would generate it for you!

(2) `System.Runtime.CompilerServices.CollectionBuilderAttribute` is needed to support collection expression for custom types.

(3) The full type name is `System.Runtime.CompilerServices.InterceptsLocationAttribute`
(4) The full type name is `System.Diagnostics.CodeAnalysis.ExperimentalAttribute`

# C# 11 Features

| Language Feature                                                                                                    | Requirements                                                                                         |
| ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------- |
| [File-local types](https://github.com/dotnet/csharplang/issues/6011)                                                | No extra requirements                                                                                |
| [ref fields](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-11.0/low-level-struct-improvements.md) a.k.a. low level struct enhancements | .net7+                                                                                               |
| [Required properties](https://github.com/dotnet/csharplang/issues/3630)                                             | `RequiredMemberAttribute`, `CompilerFeatureRequiredAttribute`,<br>`SetsRequiredMembersAttribute` (1) |
| [Static abstract members in interfaces](https://github.com/dotnet/csharplang/issues/4436)                           | .net7+                                                                                               |
| [Numeric IntPtr](https://github.com/dotnet/csharplang/issues/6065)                                                  | No extra requirements                                                                                |
| [Unsigned right shift operator](https://github.com/dotnet/csharplang/issues/4682)                                   | No extra requirements                                                                                |
| [utf8 string literals](https://github.com/dotnet/csharplang/issues/184)                                             | System.Memory nuget or .net2.1+                                                                      |
| [Pattern matching on `ReadOnlySpan<char>`](https://github.com/dotnet/csharplang/issues/1881)                        | System.Memory nuget package to get `ReadOnlySpan` itself.                                                                                |
| [Checked Operators](https://github.com/dotnet/csharplang/issues/4665)                                               | No extra requirements                                                                                |
| [auto-default structs](https://github.com/dotnet/csharplang/issues/5737)                                            | No extra requirements                                                                                |
| [Newlines in string interpolations](https://github.com/dotnet/csharplang/issues/4935)                               | No extra requirements                                                                                |
| [List patterns](https://github.com/dotnet/csharplang/issues/3435)                                                   | `System.Index`, `System.Range`(2)                                                                                  |
| [Raw string literals](https://github.com/dotnet/csharplang/issues/4304)                                             | No extra requirements                                                                                |
| [Cache delegates for static method group](https://github.com/dotnet/roslyn/issues/5835)                             | No extra requirements                                                                                |
| [nameof(parameter)](https://github.com/dotnet/csharplang/issues/373)                                                | No extra requirements                                                                                |
| [Relaxing Shift Operator](https://github.com/dotnet/csharplang/issues/4666)                                         | No extra requirements                                                                                |
| [Generic attributes](https://github.com/dotnet/csharplang/issues/124)                                               | No extra requirements                                                                                                     |

---
(1) The full type names are `System.Runtime.CompilerServices.RequiredMemberAttribute`, `System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute` and `System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute`

(2) Some features are going to work only targeting net2.1 or netstandard2.1, for instance the following code requires `System.Runtime.CompilerServices.RuntimeHelpers.GetSubArray` to be available:
```csharp
int[] n = new int[]{ 1 };  
if (n is [1, .. var x, 2])  
{  
}
```

## C# 10 Features
| Language Feature | Requirements |
| ---- | ---- |
| [Record structs](https://github.com/dotnet/csharplang/issues/4334) | No extra requirements |
| [Global using directives](https://github.com/dotnet/csharplang/issues/3428) | No extra requirements |
| [Improved Definite Assignment](https://github.com/dotnet/csharplang/issues/4465) | No extra requirements |
| [Constant Interpolated Strings](https://github.com/dotnet/csharplang/issues/2951) | No extra requirements |
| [Extended Property Patterns](https://github.com/dotnet/csharplang/issues/4394) | No extra requirements |
| [Sealed record ToString](https://github.com/dotnet/csharplang/issues/4174) | No extra requirements |
| [Source generators V2 API](https://github.com/dotnet/roslyn/issues/51257) | No extra requirements |
| [Mix declarations and variables in deconstruction](https://github.com/dotnet/csharplang/issues/125) | No extra requirements |
| [AsyncMethodBuilder override](https://github.com/dotnet/csharplang/issues/1407) | `AsyncMethodBuilderAttribute` (1) |
| [Enhanced `#line` directives](https://github.com/dotnet/csharplang/issues/4747) | No extra requirements |
| [Lambda improvements](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-10.0/lambda-improvements.md) | No extra requirements |
| [Interpolated string improvements](https://github.com/dotnet/csharplang/issues/4487) | `InterpolatedStringHandler`, `InterpolatedStringHandlerArgument` (2) |
| [File-scoped namespaces](https://github.com/dotnet/csharplang/issues/137) | No extra requirements |
| [Paremeterless struct constructors](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-10.0/parameterless-struct-constructors.md) | No extra requirements |
| [`CallerArgumentExpression`](https://github.com/dotnet/csharplang/issues/287) | `CallerArgumentExpressionAttribute` |

---
(1) The full type name is `System.Runtime.CompilerServices.AsyncMethodBuilderAttribute`.
(2) The full type names are `System.Runtime.CompilerServices.InterpolatedStringHandlerAttribute` and `System.Runtime.CompilerServices.InterpolatedStringHandlerArgumentAttribute`.

## C# 9 Features
| Language Feature                                                                                                       | Requirements                     |
| ---------------------------------------------------------------------------------------------------------------------- | -------------------------------- |
| [Target-typed new](https://github.com/dotnet/csharplang/issues/100)                                                    | No extra requirements            |
| [Skip local init](https://github.com/dotnet/csharplang/issues/1738)                                                    | `SkipLocalsInitAttribute`        |
| [Lambda discard parameters](https://github.com/dotnet/csharplang/issues/111)                                           | No extra requirements            |
| [Native ints](https://github.com/dotnet/csharplang/issues/435)                                                         | No extra requirements            |
| [Attributes on local functions](https://github.com/dotnet/csharplang/issues/1888)                                      | No extra requirements            |
| [Function pointers](https://github.com/dotnet/csharplang/issues/191)                                                   | No extra requirements            |
| [Pattern matching improvements](https://github.com/dotnet/csharplang/issues/2850)                                      | No extra requirements            |
| [Static lambdas](https://github.com/dotnet/csharplang/issues/275)                                                      | No extra requirements            |
| [Records](https://github.com/dotnet/csharplang/issues/39)                                                              | No extra requirements            |
| [Target-typed conditional](https://github.com/dotnet/csharplang/issues/2460)                                           | No extra requirements            |
| [Covariant Returns](https://github.com/dotnet/csharplang/issues/2844)                                                  | .net5.0+                         |
| [Extension `GetEnumerator`](https://github.com/dotnet/csharplang/issues/3194)                                          | No extra requirements            |
| [Module initializers](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-9.0/module-initializers.md)      | `ModuleInitializerAttribute` (1) |
| [Extending partials](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-9.0/extending-partial-methods.md) | No extra requirements            |
| [Top level statements](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-9.0/top-level-statements.md)                                                                                                                       | No extra requirements                                 |

---
(1) The full type name is `System.Runtime.CompilerServices.ModuleInitializerAttribute`.

## C# 8 Features
| Language Feature                                                                                                                          | Requirements                                                                                                     |
| ----------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| [Default Interface Methods](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-8.0/default-interface-methods.md)             | .net core 3.1+                                                                                                   |
| [Nullable reference types](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-8.0/nullable-reference-types-specification.md) | A bunch of nullability attributes (1)                                                                            |
| [Recursive Patterns](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-8.0/patterns.md)                                     | No extra requirements                                                                                            |
| [Async streams](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-8.0/async-streams.md)                                     | [Microsoft.Bcl.AsyncInterfaces](https://www.nuget.org/packages/Microsoft.Bcl.AsyncInterfaces/) or .net core 3.1+ |
| [Enhanced usings](https://github.com/dotnet/csharplang/issues/1623)                                                                       | No extra requirements                                                                                            |
| [Ranges](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-8.0/ranges.md)                                                   | `System.Index`, `System.Range`                                                                                   |
| [Null-coalescing assignment](https://github.com/dotnet/csharplang/issues/34)                                                              | No extra requirements                                                                                            |
| [Alternative interpolated strings pattern](https://github.com/dotnet/csharplang/issues/1630)                                              | No extra requirements                                                                                            |
| [stackalloc in nested contexts](https://github.com/dotnet/csharplang/issues/1412)                                                         | No extra requirements                                                                                            |
| [Unmanaged generic structs](https://github.com/dotnet/csharplang/issues/1744)                                                             | No extra requirements                                                                                            |
| [Static local functions](https://github.com/dotnet/csharplang/issues/1565)                                                                | No extra requirements                                                                                            |
| [Readonly members](https://github.com/dotnet/csharplang/issues/1710)                                                                                                                                          | No extra requirements                                                                                                                 |

(1) There are a lot of attributes: - `[AllowNull]`, `[DisallowNull]`, `[DoesNotReturn]`, `[DoesNotReturnIf]`, `[MaybeNull]`, `[MaybeNullWhen]`, `[MemberNotNull], [MemberNotNullWhen]`, `[NotNull]`, `[NotNullIfNotNull]`, `[NotNullWhen]`
