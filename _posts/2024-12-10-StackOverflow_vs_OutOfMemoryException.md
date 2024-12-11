---
layout: post
title: StackOverflowException vs. OutOfMemoryException
categories: csharp
---

I was investigating crash dumps for one of our services and faced a puzzling group of crashes. These crashes were caused by a `StackOverflowException` with relatively short stack traces. It wasn't due to infinite recursion, just a logging method that was allocating 64K on the stack. Given that it was a Windows process with standard thread creation logic, each stack should have 1MB, making a stack overflow in this case not possible.

After digging deeper, I realized that the crashing process was running inside a job object with memory constraints. Each crash dump's memory size was equal to the job object's memory limit. So, is it possible that the `StackOverflowException` was actually masking an out-of-memory condition?

The short answer is yes. In a memory-constrained environment, stack allocation can trigger a `StackOverflowException` because the 1MB stack memory is not committed during thread construction, instead the stack memory is just reserved and committed on demand.

To reproduce the issue, we need two processes:

One process will create multiple threads, allocate a significant amount of memory to approach the memory limits, and then perform substantial stack allocations (e.g., 64K) on each stack.

Another process will start the first process inside the job object.

For .NET 3.0+ applications, it's possible to [control the memory limit](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-limit) via the `System.GC.HeapHardLimit` setting. However, this won't reproduce our issue since this setting controls the heap size, not the overall memory size. Thus, using job objects is the way to go.

Here is the code for the first process that will run under ~100Mb limit:

```csharp
static async Task Main(string[] args)
{
    Console.WriteLine("Starting tasks");
    // Will notify the threads to do the stack allocation.
    var @event = new ManualResetEvent(initialState: false);
    var tasks = new List<Task>();
    for (int i = 0; i < 512; i++)
    {
        tasks.Add(
            Task.Factory.StartNew(
                () =>
                {
                    // Waiting for the signal to start stack allocation
                    @event.WaitOne();
                    Console.WriteLine("Allocating...");
                    int bufferSizeOnStack = 64 * 1024;
                    
                    unsafe
                    {
                        var data = stackalloc byte[bufferSizeOnStack];
                    }
                },
                // Forcing construction of a new thread.
                TaskCreationOptions.LongRunning));
    }

    // Waiting for a bit for all the threads to start.
    Thread.Sleep(100);
    Console.WriteLine("Tasks started");

    Console.WriteLine("Allocating a large array");
    
    // Allocating ~55Mb. It's enough to hit the issue.
    byte[] data = new byte[55_000_000];
    Console.WriteLine("Allocated a large array");

    Console.WriteLine("Releasing threads");
    // releasing the threads
    @event.Set();

    await Task.WhenAll(tasks);
    Console.WriteLine("Done");

    Console.ReadLine();
}
```

And here a launcher:

```csharp
// This is a project in the same solution.
// Getting a path to the executable in the bin/debug folder
string fileName = @"..\..\bin\Debug\net472\StackOverflowAndOutOfMemory.exe";

ProcessStartInfo startInfo = new ProcessStartInfo(fileName);
Process? process = Process.Start(startInfo);
JobObject jobObject = new JobObject();
jobObject.SetLimits(new JobObjectLimits()
{
    // Setting a process memory limit to ~100Mb
    ProcessMemoryLimit = 100_000_000,
});

jobObject.AssignProcess(process!);

```

This code uses [`Meziantou.Framework.Win32.Jobs`](https://www.nuget.org/packages/Meziantou.Framework.Win32.Jobs), an amazing project that can do a bunch of stuff, including using job objects from .NET.

And here is the output:

```
Allocating a large array
Allocated a large array
Releasing threads
Allocating...
Allocating...
Allocating...
Allocating...
Allocating...
Allocating...
Allocating...

Process is terminated due to StackOverflowException.
```

And as a conclusion, here is a piece from an amazing post by Raymond Chen ["The case of the stack overflow exception when the stack is nowhere near overflowing"](https://devblogs.microsoft.com/oldnewthing/20220204-00/?p=106219) on why the application can fail with `StackOverflow` even though the stack didn't exhaust 1Mb of space:

> Another possibility is that the system ran out of memory. Even though a megabyte of memory is reserved for the stack, the memory is allocated only on demand, as we learned last time when we took [a closer look at the stack guard page](https://devblogs.microsoft.com/oldnewthing/20220203-00/?p=106215). If the system cannot allocate memory to replace the guard page, then you get a stack overflow exception.