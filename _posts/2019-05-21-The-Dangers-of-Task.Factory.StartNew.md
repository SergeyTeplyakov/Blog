---
layout: post
title: The Dangers of Task.Factory.StartNew
categories: async
---

I've faced a very interesting problem recently with one of our production services: the service partially stopped responding to new requests even though some other parts of the service were still working.

To understand the problem, let's review the following code. Suppose we have a service that processes internal requests in a "dedicated thread". To do that it creates a long-running task by passing `TaskCreationOptions.LongRunning` into `Task.Factory.StartNew` method and creates a continuation for error reporting purpose.

```csharp
public class Processor
{
    private Task _task;
    private readonly BlockingCollection<Request> _queue;

    public Processor()
    {
        _task = Task.Factory.StartNew(LoopAsync, TaskCreationOptions.LongRunning);
        _task.ContinueWith(_ =>
        {
            // Trace the error.
            // Maybe even restart the loop.
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Stop() => _queue.CompleteAdding();

    private async Task LoopAsync()
    {
        foreach (var request in _queue.GetConsumingEnumerable())
        {
            await ProcessRequest(request);
        }
    }
}
```

What is the problem with this code? Quite a few, actually. And all of them are related to `LoopAsync` method return type.

First of all, let's think about the long-running aspect. `TaskCreationOptions.LongRunning` indicates that a given operation is such a long running procedure that it deserves a dedicated thread. That makes sense because indeed `LoopAsync` can run for the entire lifetime of the service until `Stop` method is called.

But here is the catch: from CLR's point of view the duration of `LoopAsync` is not "linear" and the operation "finishes" on the first `await`. It means that this code spawns a thread just to wait for and to process the first request. And once the first request is processed, the continuation inside `LoopAsync` is called in a thread pool's thread causing the original thread to die.

The code creates unnecessary threads and this is not the best thing in the world, but this is not the most dangerous part here.

The type of the `_task` field is `Task`, but what is the actual type of the object at runtime? Is it just `System.Threading.Tasks.Task`? The actual type is `Task<Task>`.

`Task.Factory.StartNew` "wraps" the result of a given delegate into the task, and if the delegate itself returns a task, then the result is a task that creates a task.

In this case, it means that the error handling here is completely wrong. **`_task.ContinueWith` creates a continuation of an outer task that will fail only if something will go terribly wrong with the system and the TPL will fail to launch a new thread**. Otherwise, the outer task will succeed "hiding" potential issues with the inner task.

Here is a simpler example:

```csharp
static void Main(string[] args)
{
    var task = Task.Factory.StartNew(async () =>
    {
        Console.WriteLine("Inside the delegate");
        throw new Exception("Error");
        return 42;
    }, TaskCreationOptions.LongRunning);
    task.ContinueWith(
        _ => { Console.WriteLine($"Error: {_.Exception}"); }, 
        TaskContinuationOptions.OnlyOnFaulted);
    Console.ReadLine();
}
```

When we run this code we'll see `Inside the delegate` message on the screen and nothing else. And if we'll check the status of the `task` variable at runtime we'll notice that the task is actually finished successfully and the continuation, that supposes to handle the error, is never called.

What should you do in this case? The simplest solution is just to switch to `Task.Run` that will return an underlying task because the API was designed with async methods in mind.

## Use [`TaskExtensions.Unwrap`](https://referencesource.microsoft.com/#System.Core/System/Threading/Tasks/TaskExtensions.cs,123) extension method to get the underlying task from `Task<Task>` instance.

But if you have to use `Task.Factory.StartNew` because you need to pass some other task creation options, then you can "unwrap" the resulting task to obtain the underlying task instance:

```csharp
static void Main(string[] args)
{
    var task = Task.Factory.StartNew(async () =>
    {
        Console.WriteLine("Inside the delegate");
        throw new Exception("Error");
        return 42;
    }).Unwrap();
    
    // Now, task actually points to the underlying task and the next continuation works as expected.
    task.ContinueWith(
        _ => { Console.WriteLine($"Error: {_.Exception}"); }, 
        TaskContinuationOptions.OnlyOnFaulted);
    Console.ReadLine();
}
```

## Always trace unobserved task exceptions

One way at least to mitigate the issues like this is to always react to unhandled exceptions in tasks. When a task fails but the user fails to "observe" the error, the [`TaskScheduler.UnobservedTaskException`](https://referencesource.microsoft.com/#mscorlib/system/threading/Tasks/TaskScheduler.cs,479) is triggered. Back in .NET 4.0 days unhandled task exceptions were "critical" and were causing an application to crash. Starting from .NET 4.5 the default behavior has changed (*) and unhandled task exceptions may stay unnoticed (use [`<ThrowUnobservedTaskExceptions>`](https://docs.microsoft.com/en-us/dotnet/framework/configure-apps/file-schema/runtime/throwunobservedtaskexceptions-element) configuration section if you want to change it back).

(*) The reason for this change is quite simple: it is extremely simple in this "async" days to get an unobserved task exception. Simple code like this can cause it:

```csharp
var t1 = AsyncMethod1();
var t2 = AsyncMethod2();
// If both t1 and t2 will fail, then t2's error will be unobserved.
await t1;
await t2;
```

## TLDR;
* Never use `Task.Factory.StartNew` with `TaskCreationOptions.LongRunning` if the given delegate is backed by an async method.
* Prefer `Task.Run` over `Task.Factory.StartNew` and use the latter only when you really have to.
* If you have to use `Task.Factory.StartNew` with async methods, always call `Unwrap` to get the underlying task back.
* Always trace unobserved task exceptions, because you never know what kind of subtle issues are hidden in your code. 

If you work on a codebase that was started in .NET 4.0 era, I would highly recommend you search for `Task.Factory.StartNew` usages and double check that you don't have the issues mentioned in this post.