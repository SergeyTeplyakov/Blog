---
layout: post
title: Finalizers are tricker than you might think. Part 1
categories: production_investigations
---

I was looking at some crash dumps recently and found one interesting case that I want to talk about here.

Let's say we want to create a very simple file copier. It takes two paths in the constructor, opens two streams and copies the content from one to another in `CopyAsync` method:

```csharp
#nullable enable
public sealed class FileCopier : IDisposable
{
    private readonly Stream _source;
    private readonly Stream _destination;

    public FileCopier(string sourcePath, string destinationPath)
    {
        _source = new FileStream(sourcePath, FileMode.Open);
        _destination = new FileStream(destinationPath, FileMode.Create);
    }

    public async Task CopyAsync()
        => await _source.CopyToAsync(_destination);

    public void Dispose() => Dispose(disposing: true);

    ~FileCopier()
    {
        Console.WriteLine("Running ~FileCopier");
        Dispose(disposing: false);
    }

    private void Dispose(bool disposing)
    {
        _source.Dispose();
        _destination.Dispose();
    }
}
```

The class implements `IDisposable` interface and has the finalizer to make sure the resources are freed if the `Dispose` method was not called. Here how we could use it:

```csharp
private static async Task Copy(string source, string destination)
{
    using (var copier = new FileCopier(source, destination))
    {
        await copier.CopyAsync();
    }
}

```

```csharp
static async Task Main(string[] args)
{
    string source = "source.txt";
    string destination = "destination.txt";
    
    try
    {
        await Copy(source, destination);
        Console.WriteLine("Copy is done!");
    }
    catch(Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        GC.Collect();
        GC.Collect();
    }
}
```

And here is the output:

```
Error: Could not find file 'C:\Users\seteplia\source\repos\DisposableTrickiness\DisposableTrickiness\bin\Debug\net9.0\source.txt'.
Press 'Enter' to exit.
Running ~FileCopier
Unhandled exception. System.NullReferenceException: Object reference not set to an instance of an object.
   at DisposableTrickiness.FileCopier.Dispose(Boolean disposing) in C:\Users\seteplia\source\repos\DisposableTrickiness\DisposableTrickiness\Program.cs:line 35
   at DisposableTrickiness.FileCopier.Finalize() in C:\Users\seteplia\source\repos\DisposableTrickiness\DisposableTrickiness\Program.cs:line 30
   at System.GC.RunFinalizers()
```

The application prints that the source file is missing, but then the application crashes with `NullReferenceException`.  And the call stack shows that the `NullReferenceException` is coming from the finalizer. WAT?!?

Let's dive deeply into how the finalizers work and why they're probably tricker that you might think!

## Why the application crashes?
The finalizers are executed in a dedicated thread controlled by the Garbage Collector. And like for a regular thread, an unhandled exception from the finalizer thread causes an application to crash.

And since there is a single dedicated thread to run all the finalizers, it's also not a great idea to make blocking calls from it. I don't want to even think what "strange" things would happen for a long running application when its finalizer thread is blocked. Just don't call arbitrary code from the finalizers, and don't allow exceptions to escape from it.
## Why `NullReferenceException` when non-nullability is enabled?
[Nullable reference types](https://learn.microsoft.com/en-us/dotnet/csharp/nullable-references) is purely a compile-time feature with some known restrictions and the finalizers is one of them.

The finalizers are executed for every objects regardless if their constructor was finished successfully or not. Some people with C++ background might think that the finalizers are similar to C++ destructors, which are executed only for the fully constructed instances. But unlike C++ destructors, the finalizers will be executed even if the constructor fails with an exception.

And it means that from the C# compiler's point of view, the `_source` and the `_destination` fields are non-nullable, but if the constructor fails before the fields are assigned, they'll be null during the finalization.
## Should you even touch `_source` and `_destination` from the finalizer?
The solution here is not to use `?.` like  `_source?.Dispose();` and `_destination?.Dispose()` but not to touch "managed resources" from the finalizer in the first place.

There are two reasons for that:
1. The order of the finalization is non-deterministic and
2. The finalizers are designed for cleaning up "unmanaged" resources and the `Stream` is "managed resource".

Let's clarify these points.

Again, if you have a C++ background you might think that the order of finalization is the opposite to the construction order. But this is not the case. The CLR doesn't track the object's dependency chain and the construction order, and just registers all the instances in a global finalization queue. The queue has a special treatment for "critical finalizable" objects (when a class derives from `CriticalFinalizerObject`) and provides a guarantee that the finalization for "normal" objects happens before the finalization of the the "critical" objects. But there is no guarantee in which order the finalization is happening within the normal or critical finalization segment of the queue.

If object A references object B, the finalization for object A might happen before or after the finalization of object B. You just don't have the control over it. 

But the second aspect explains why you should not be touching "managed" resources from the finalizers in the first place: the finalizers are designed to clean up "unmanaged" resources only!
## What's the difference between "managed" and "unmanaged" resources?
In some cases, the terms are important, and understanding the concepts of "managed" and "unmanaged" resources is very crucial in understanding how to deal with the resources properly in .NET.

**TLDR; if its a disposable class - its a "managed resource", if it's `IntPtr` (or something similar) then its an "unmanaged resource". Wrap `IntPtr` into a disposable class with a finalizers, and you'll get a managed resource!**

The CLR automatically "manages" memory: when the instance "goes out of scope" and is not reachable from the application code it become eligible for garbage collection. And when the GC runs, the memory used by the object is reclaimed. There is quite a bit of complexity behind this, but all that complexity is built to deal with memory. 

The CLR can't automatically managed other resources. If the resource is allocated in an unmanaged heap via `malloc` or an opaque handler was obtained from the operating system, then the CLR can't automatically free them when they becomes unreachable. The runtime needs help from the developer.

In order to make the resource "managed", the underlying "unmanaged" resource needs to be wrapped in a class that implements `IDisposable` interface for eager resource clean-up, and should also have a finalizer - for cleaning up the resource when the user forgets to clean it up and the instance is collected by the GC.

```csharp
// ManagedWrapper itself is a managed resource
public class ManagedWrapper : IDisposable
{
	// InpPtr represents an underlying unmanaged resource.
	private readonly IntPtr _resource;
	public ManagedWrapper()
	{
    	_resource = Allocate(); // PInvoke to allocate a resource
	}
	
    public void Dispose()
    {
        Free(_resource); // PInvoke to free a resource
        GC.SuppressFinalize(this);
    }
    
    ~ManagedWrapper() => Free(_resource);
}
```

In this case, `IntPtr _resource` is an unmanaged resource, and the instance of `ManagedWrapper` is a managed resource.

Don't be surprised that you don't see `Dispose(bool disposing)` here. We're going to cover the "Dispose Pattern" in more details in future posts.
## Conclusion
* Unhandled exceptions from the finalizers will cause an application to crash.
* The nullable fields might be `null` in the finalizers when the constructor throws an exception.
* The finalizers should not touch managed resources. They're designed to clean-up unmanaged resources only.
* The classes without unmanaged resources should not have finalizers at all.
* The order of the finalization is not guarantee. 