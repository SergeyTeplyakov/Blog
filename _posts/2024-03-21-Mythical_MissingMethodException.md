---
layout: post
title: Figuring out mysterious `MissingMethodException` in a simple C# application
categories: C#
---

As we already know from [C# Language Features vs. Target Frameworks](https://sergeyteplyakov.github.io/Blog/c%23/2024/03/06/CSharp_Language_Features_vs_Target_Frameworks.html) you can use most of the latest C# language features targeting .Net Standard or Full Framework. Some features just work with any target frameworks, but some require special attributes or types to be defined during compilation.

Here is an interesting problem that I've faced recently that took quite a bit of time to figure out.

## Core.csproj
Let's say you have a core library that multi-targets `netstandard2.0` and `net8.0`. The library could have a bunch of stuff, like helpers for `Span<T>`, or just anything else. For the sake of this example, this library just would have one class `Config` type with an `init-only` property:

```csharp
// Core.csproj
// <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
namespace Core;  
public class Config { public int X { get; init; } }
```

Obviously, the code won't compile, since netstandard2.0 version doesn't have `IsExternalInit` type. The solution sounds pretty easy, right? We just add `IsExternalInit.cs` file manually (or with some MSBuild magic) with the following content:

```csharp
#if NETSTANDARD2_0  
    namespace System.Runtime.CompilerServices;  
    internal class IsExternalInit;  
#endif
```

We either can add `IsExternalInit.cs` conditionally to the project itself if the target is `netstandard2.0` or just have `#if NETSTANDARD2_0` inside of it. We can't just add this type for all the targets, but in this case we could face a compilation errors if the `Core` project would have `InternalsVisibleTo` attribute for a test project that target `net8.0` or any other target runtime that has `IsExternalInit` type already defined.

## Library.csproj
Now, we add another library, let's say, `Library.csproj` that targets only `netstandard2.0` that uses our `Core.csproj`. This might be not a super common case, but I've seen quite a few of them in the wild:

```csharp
// Library.csproj
// <TargetFramework>netstandard2.0</TargetFramework>
public static class ConfigFactory  
{  
    public static Config Create(int value) => new () { X = value };  
}
```

## Application.exe
And now we have a console app that targets `net8.0` that just uses the factory:

```csharp
// Application.exe
// <TargetFramework>net8.0</TargetFramework>
using Factory;  
  
var config = ConfigFactory.Create(42);  
  
Console.WriteLine("Done!");
```

Here is the dependency diagram:

![Diagram1](/Blog/assets/diagram1.png "Diagram1")

Would you expect any issues with this code? Me neither, to be honest!
But here is the output:

```
Unhandled exception. System.MissingMethodException: Method not found: 'Void Configuration.Config.set_X(Int32)'.
   at Factory.ConfigFactory.Create(Int32 value)
   at Program.<Main>$(String[] args) in Application/Program.cs:line 3
```

![WAT](/Blog/assets/jackie_chan_meme.jpg "WAT")

You can check the IL, and you'll see that the `set_X(Int32)` "method" (which is a property) is definitely exists in the `Config` class. But why do we get the error? Is it a compiler bug? Not really!

## The root cause
So here is the issue. Even though the `Core.csproj` is multi-targeted, the question is: which version of `Core.dll` is actually deployed in the output of folder? The `core.dll` that targets .netstandard2.0 or the `core.dll` that targets `net8.0`? At runtime there is no such a thing as 'multi-targeting', the multi-targeting is a build-time feature!

Sine `Application` project targets `net8.0` and implicitly references `Core.csproj`, the `net8.0` version is deploy. 

![Diagram2](/Blog/assets/diagram2.png "Diagram2")

Is it a problem? Actually, yes, it is. Let's check the IL for the `ConfigFactory`:
```il
.method public hidebysig static class [Core]Core.Config  
  Create(  
    int32 'value'  
  ) cil managed  
{    
  // [7 47 - 7 67]  
  IL_0000: newobj       instance void [Core]Core.Config::.ctor()  
  IL_0005: dup  
  IL_0006: ldarg.0      // 'value'  
  IL_0007: callvirt     instance void modreq ([Core]System.Runtime.CompilerServices.IsExternalInit) [Core]Core.Config::set_X(int32)  
  IL_000c: nop  
  IL_000d: ret  
  
} // end of method ConfigFactory::Create
```

`Library.csproj`  targets `netstandard2.0` and uses `System.Runtime.CompilerServices.IsExgternalInit` type from `Core.dll`, but at runtime we have `Core.dll` that targets `net8.0` with the following `set_X` property:
```il
.property instance int32 X()  
{  
  .get instance int32 Core.Config::get_X()  
  .set instance void modreq ([System.Runtime]System.Runtime.CompilerServices.IsExternalInit) Core.Config::set_X(int32)  
} // end of property Config::X
```

I.e. the one, that takes `IsExternalInit` from `System.Runtime` dll and not `Core` assembly. Yes, you could have the same types defined in different assemblies, and from the runtime point of view, they're definitely are the two different types.

## Solutions to the issue
So, how can we solve this issue?
The simplest solution is just to use a tool that solved this problem already, for instance, [`PolySharp` nuget package](https://www.nuget.org/packages/PolySharp/). But if this is not an option for you for some reason, there are two solutions available.

First, you can add `IsExternalInit` unconditionally. This might cause a problem with `InternalsVisibleTo` as I mentioned before, and second solution is based on `TypeForwardingAttribute`:

```csharp
#if NETSTANDARD2_0  
namespace System.Runtime.CompilerServices;  
internal class IsExternalInit;  
#else  
[assembly: global::System.Runtime.CompilerServices.TypeForwardedTo(  
    typeof(global::System.Runtime.CompilerServices.IsExternalInit))]  
#endif
```

`TypeForwardedToAttribute` tells the runtime where to look the types that supposed to be in the current assembly. In this case, for `net8.0` case we're telling the runtime that `IsExternalInit` class is located in BCL and everything works just fine. Btw, this is the solution that `PolySharp` library uses under the hood as well.