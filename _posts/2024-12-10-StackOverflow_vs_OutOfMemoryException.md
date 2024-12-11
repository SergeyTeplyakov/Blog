---
layout: post
title: StackOverflowException vs. OutOfMemoryException
categories: csharp
---

I was looking into crash dumps for one of our service and was puzzled by one group of crashes. The crashes were caused by `StackOverflowException` with relatively short stack traces. Nothing like an infinite recursion, just a logging method that was allocating 64K on the stack. It was a windows process without any changes in the thread creation logic, so each stack should have 1Mb and it was strange to see a stack overflow in this case.

After digging deeper into the issue, I've realized that the crashing process was running inside a job object with memory constraints and each crash dump memory size was equal to the job object memory limit. So is it possible that the `StackOverflowException` was actually masking an out of memory condition?

The short answer is, yes: If you run in a memory constraint environment, stack allocation can cause `StackOverflowException` because the 1Mb stack memory is not committed during a thread construction.

To reproduce the issue we need two processes. One will create a bunch of threads, allocate quite a bit of memory to be close the memory limits and then stack allocate a decent chunks on each stack, like 64K. And another process will start the first process inside the job object.

For .net3.0+ applications, its possible to [control the memory limit](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-limit) via `System.GC.HeapHardLimit` setting but this won't reproduce  our issue, since the setting control the heap size and not the overall memory size. So the job objects is the way to go.

Here is the code for the first process that will run under ~100Mb limit:

```csharp
static async Task Main(string[] args)
{
    Console.WriteLine("Starting tasks");
    // Will communicate with the threads that they can proceed
    // and stack allocate buffers.
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
    
    // Allocating ~55Mb is enough to hit the issue.
    byte[] data = new byte[55_000_000];
    Console.WriteLine("Allocated a large array");

    Console.WriteLine("Releasing threads");
    @event.Set(); // releasing the threads
    await Task.WhenAll(tasks);
    Console.WriteLine("Done");

    Console.ReadLine();
}
```

And here a launcher:

```csharp
// This is a project in the same solution.
// Getting a path to the executable in the bin/debug
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

This project uses [`Meziantou.Framework.Win32.Jobs`](https://www.nuget.org/packages/Meziantou.Framework.Win32.Jobs), an amazing project that can do a bunch of stuff, including using job objects from .NET.

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