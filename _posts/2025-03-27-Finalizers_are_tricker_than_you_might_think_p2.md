---
layout: post
title: Finalizers are tricker than you might think. Part 2
---

In [the previous part](https://sergeyteplyakov.github.io/Blog/production_investigations/2025/02/26/Finalizers_are_tricker_than_you_might_think_p1.html) we discussed why the finalizers should only deal with unmanaged resources and in this post I want to show that this is not as simple as it sounds.

Let's reiterate again what a native resource is. Native resource is a resource that is not managed by the CLR. Such resource is typically handled by native code and is exposed via the following API:
* a "constructor" that allocates the resource and returns a handle to it
* a "destructor" that cleans the resource up and 
* a set of methods that take a handle to perform an operation

For example, RocksDb is a well-known key-value store written in C++ with C-bindings that allow non-C++ application to consume it via an interoperable API. Here is a naive example of such API.

```csharp
public static class RocksDbNative
{
    private static readonly HashSet<IntPtr> ValidHandles = new();
    public static IntPtr CreateDb()
    {
        // Allocating native resource used by the DB.
        IntPtr handle = 42;
        Trace("Creating Db", handle);
        ValidHandles.Add(handle);
        return handle;
    }

    public static void DestroyDb(IntPtr handle)
    {
        Trace("Destroying Db", handle);
        ValidHandles.Remove(handle);
        // Cleaning up the resources associated with the handle.
    }

    public static void UseDb(IntPtr handle)
    {
        Trace("Starting using Db", handle);
        // Just mimic some extra work a method might do.
        PerformLongRunningPrerequisite();

        // Using the handle
        Trace("Using Db", handle);
        PInvokeIntoDb(handle);
    }

    private static void PInvokeIntoDb(IntPtr handle) {}
    
    private static void Trace(string message, IntPtr handle)
    {
        Console.WriteLine(
            $"{message}. Id: {handle}, IsValid: {IsValid(handle)}.");
    }
    
    public static bool IsValid(IntPtr handle) => ValidHandles.Contains(handle);
    
    private static void PerformLongRunningPrerequisite()
    {
        // Skipped for now.
    }
}
```

We don’t want to use native resource directly, so we’ll create a wrapper - `RocksDbWrapper`:

```csharp
public class RocksDbWrapper : IDisposable
{
    private IntPtr _handle = RocksDbNative.CreateDb();

    public void Dispose() => Dispose(true);

    public void UseRocksDb() => RocksDbNative.UseDb(_handle);

    private void Dispose(bool disposing) => RocksDbNative.DestroyDb(_handle);

    ~RocksDbWrapper() => Dispose(false);
}
```

The class implements `IDisposable` interface for eager and timely resource clean-up and since it has unmanaged resources it also has the finalizer.

And here is the usage:
```csharp
static void UseRocksDbWrapper()
{
    new RocksDbWrapper().UseRocksDb();
    Console.WriteLine("Done using RocksDb!");
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}
```

The `RocksDbWrapper` is disposable but the code doesn’t call `Dispose` and just creates the instance and calls `UseRocksDb`. This is not great, but since the class has a finalizer we should be fine. Right? Right!

Let’s see the output:

```
Creating Db. Id: 42, IsValid: False.
Starting using Db. Id: 42, IsValid: True.
Destroying Db. Id: 42, IsValid: True.
Using Db. Id: 42, **IsValid: False.**
```

**The native handle was closed before it was used in `RocksDbNative.UseDb`**, probably causing a crash in real world. But **WHY??**

**NOTE**: To reproduce all the following examples you need to run the code in `Release` mode and disable tiered compilation for NET5+. Tier0 compilation does not track the lifetime of local variables so you either need to run a method multiple time to propagate it to further tiers or just disable tiered compilation altogether.

First of all, lets check what the body of `PerformLongRunningPrerequisite` does.

```csharp
private static void PerformLongRunningPrerequisite()
{
    Thread.Sleep(100);
    // Code runs long enough to cause the GC to run twice.
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}
```

It forces the two GC cycles, to consistently reproduce this issue. In reality you won’t run the GC twice, so the issue won’t be as easily reproduceable, instead you’ll be getting crashes once in a blue moon, making such investigation very painful.

Let's figure out step by step, why the native resource was cleaned up before it was used. And to get to the bottom of this we need to understand some things about the GC.
## Can a local variable be collected during a method call?
**Yes, it can.** Here is an example:

```csharp
public static void ShowReachability()
{
    object o1 = new object();
    object o2 = new object();
    var wr1 = new WeakReference(o1);
    var wr2 = new WeakReference(o2);
    GC.Collect();
    Console.WriteLine($"o1 alive: { wr1.IsAlive}, o2 alive: { wr2.IsAlive}");
    GC.KeepAlive(o2);
}
```

```
o1 alive: false, o2 alive: true
```

If a local variable is a final root reference, the instance becomes eligible by the GC right after its final usage, and not at the end of the method. This is possible, because the GC tracks when the variable is no longer used, and stores this information in internal data structure called `GCInfo`. And such tracking is only done in release mode and only for a fully optimized code. That's why the behavior is different in debug mode or when the code is compiled by Tier0 JIT.

For more info, see my post [Garbage collection and variable lifetime tracking](https://devblogs.microsoft.com/premier-developer/garbage-collection-and-variable-lifetime-tracking/).
## Can an instance be collected during an instance method call?
And the answer is again, **Yes, it can!** Here is a more complicated example:
```csharp
public class InstanceEligibility
{
    public void Test()
    {
        var wr = new WeakReference(this);
        GC.Collect();
        if (!wr.IsAlive)
        {
            Console.WriteLine("The instance was collected by the GC!");
        }
    }
}
```

 If you run `new InstanceEligibility().Test()`, you'll see that `wr.IsAlive` will be `false` and the message will be printed to the console.

From the CLR’s perspective the instance method is just a static method where the instance is passed implicitly via the first parameter. And similarly to the previous case, the instance becomes eligible by the GC after its final usage in the method.
## Getting everything together

Let’s get back to our example and show exactly what is going on at runtime when the following call is made: `new RocksDbWrapper().UseRocksDb()`:

![Diagram](/Blog/assets/2025_finalizers_p2.png "Diagram")

If you’re asking yourself, is it possible to have this in real world, the answer is: **For sure!**

So, what’s the solution?
## Using Safe Handle
What's the proper way of dealing with native resources? **Wrap them into safe handles**. `SafeHandle` is a special base type from BCL that is designed for managing native resources. Here is an example for our case:
```csharp
public class RocksDbSafeHandle : SafeHandle
{
    private int _released = 0;

    /// <inheritdoc />
    private RocksDbSafeHandle(IntPtr handle) : base(handle, ownsHandle: true) { }

    public static RocksDbSafeHandle Create()
	    => new RocksDbSafeHandle(RocksDbNative.CreateDb());

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        if (Interlocked.CompareExchange(ref _released, 1, 0) == 0)
        {
            RocksDbNative.DestroyDb(handle);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public override bool IsInvalid => _released != 0;

    /// <inheritdoc />
    public override string ToString() => handle.ToString();

    internal IntPtr Handle => this.handle;
}
```

Notice, that we don't have the finalizer here. Instead, we just derive from `SafeHandle` and override two methods: `ReleaseHandle` and `IsValid`. That's it!

And instead of using `IntPtr` we're going to use `RocksDbSafeHandle`, including the `RocksDbNative` class:

```csharp
public static class RocksDbNative
{
	public static void UseDb(RocksDbSafeHandle handle)
	{
	    Trace("Starting using Db", handle);
	    PerformLongRunningPrerequisite();
	
	    // Using the handle
	    Trace("Using Db", handle);
	    PInvokeIntoDb(handle.Handle);
	    // Making sure the ‘handle’ is not collected during PInvoke
	    GC.KeepAlive(handle);
	}
}
```

Now, this is safe, since the `handle` instance itself is not eligible by the GC until the end of the method due to `GC.KeepAlive` (and the `GC.KeepAlive` is very important here to avoid race condition that could cause the resource cleanup during the native call). And yes, the code is still quite involved but the complexity is hidden inside the native layer which is a complicated layer by design. At least your users are safe and they won’t get heisenbugs due to very strange and hard to reproduce race conditions between their code and the GC finalization. 
## Conclusion
**The main goal of this post is to scare you and convince that native resource management in .NET is tricky.** You should clearly design the native-to-managed layer and rely on `SafeHandle` instead of manually managing the native resources. And you  definitely should not expose the native handles beyond such layer to avoid exposing the complexity through the entire application.
## Crazy territory: can the finalizer run before the constructor is done?
Here is even crazier scenario. Is it possible to see “.dtor end” before “.ctor end”?

```csharp
public class CrazyContructor
{
    public CrazyContructor()
    {
        Console.WriteLine(".ctor start");
        Console.WriteLine(".ctor end");
    }
    ~CazyConstructor()
    {
        Console.WriteLine(".dtor end");
    }
}
```

Probably, if you reached that far, you’re not surprised that answer is **Yes!** Constructor is not special for the GC. And if the constructor won’t touch `this` pointer and the `this` pointer is the last root for the instance, then the instance can be collected by the GC. And if the rest of the constructor will run for a long time and two GC cycles will happen at this point, the GC will run the finalizer technically before finishing the construction of the instance!

Here is the full example:
```csharp
public class CrazyContructor
{
    private readonly int _field;
	public CrazyContructor()
	{
	    Console.WriteLine(".ctor start");
	    _field = 42;
	    Console.WriteLine(_field);
	    // We’re not touching ‘this’ pointer after this point.
	    GC.Collect();
	    GC.WaitForPendingFinalizers();
	    GC.Collect();
	    Console.WriteLine(".ctor end");
	}
	~CrazyContructor()
	{
	    Console.Write(".dtor end");
	}
}
```

And if you run just `new CrazyConstructor()`, you’ll get the following output:
```
.ctor start
42
.dtor end.ctor end
```