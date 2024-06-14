---
layout: post
title: You probably should stop using a custom TaskScheduler
categories: csharp
---

If you don't know what `TaskScheduler` is and you don't have a custom version of it in your project, you probably can skip this post. But if you don't know what it is but you do have one or two in your project, then this post is definitely for you.

Let's start with the basics. The Task Parallel Library (a.k.a. TPL) was introduced in .NET 4 in 2010. And back then it was mostly used for parallel programming rather than for async programming since the async programming was not a first class citizen in C# 4 and .NET 4.

This manifested in the TPL API, for instance, `Task.Factory.StartNew` is taking the delegates that return `void` or `T`, instead of `Task` or `Task<T>`:

```csharp
var task = Task.Factory.StartNew(() =>
								 {
									 Console.WriteLine("Starting work...");
									 Thread.Sleep(1000);
									 Console.WriteLine("Done doing work.");
								 });
```

`Task.Factory.StartNew` has quite a few overloads and one of them takes `TaskScheduler`. What's that? It's a strategy that defines how the tasks are executed at runtime.

By default (if a custom `TaskScheduler` is not passed and `TaskCreationOptions.LongRunning` is not passed either) the default task scheduler is used. This is an internal type called [`ThreadPoolTaskScheduler`](https://source.dot.net/System.Private.CoreLib/R/ede3f49dfeb3b299.html) and it uses the .NET Thread Pool for scheduling tasks.
(If `TaskCreationOptions.LongRunning` is passed to `Task.Factory.Startnew` then a dedicated thread is used to avoid consuming a thread from a thread pool for a long time).

Like with any new technology, when TPL was released, a bunch of nerds got excited and tried to use (and abuse) a new tech as much as possible. And if Microsoft gives you an extensible library some people were thinking its a good idea to ... you know ... extend it.

One of the most common patterns was some kind of concurrency limiting task scheduler that uses a fixed number of dedicated threads to make sure you won't oversubscribe the CPU:

```csharp
public sealed class DedicatedThreadsTaskScheduler : TaskScheduler
{
    private readonly BlockingCollection<Task> _tasks = new BlockingCollection<Task>();
    private readonly List<Thread> _threads;

    public DedicatedThreadsTaskScheduler(int threadCount)
    {
        _threads = Enumerable.Range(0, threadCount).Select(i =>
        {
            var t = new Thread(() =>
            {
                foreach (var task in _tasks.GetConsumingEnumerable())
                {
                    TryExecuteTask(task);
                }
            })
            {
                IsBackground = true,
            };

            t.Start();
            return t;

        }).ToList();
    }

    protected override void QueueTask(Task task) => _tasks.Add(task);

    public override int MaximumConcurrencyLevel => _threads.Count;

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    protected override IEnumerable<Task> GetScheduledTasks() => _tasks;
}
```

There are quite a few other implementations in the wild that do the same thing: [`DedicatedThreadTaskScheduler`](https://github.com/microsoft/BuildXL/blob/9f2e73d9c2f35318c03527414f21ff078cad403d/Public/Src/Utilities/Utilities/Tasks/DedicatedThreadTaskScheduler.cs#L15), [`DedicatedThreadsTaskScheduler`](https://github.com/microsoft/BuildXL/blob/9f2e73d9c2f35318c03527414f21ff078cad403d/Public/Src/Utilities/Utilities/Tasks/DedicatedThreadsTaskScheduler.cs#L17), [`LimitedConcurrencyLevelTaskScheduler`](https://github.com/ChadBurggraf/parallel-extensions-extras/blob/master/TaskSchedulers/LimitedConcurrencyLevelTaskScheduler.cs) and even [`IOCompletionPortTaskScheduler`](https://github.com/ChadBurggraf/parallel-extensions-extras/blob/master/TaskSchedulers/IOCompletionPortTaskScheduler.cs) that uses IO Completion ports to limit the concurrency.

Regardless of the implementation and fanciness, all of them do the same thing: they allow at most given number of tasks being executed at the same time. Here is an example of how we can use it to force having at most 2 tasks running at the same time:

```csharp
var sw = Stopwatch.StartNew();
// Passing 2 as the threadCount to make sure we have at most 2 pending tasks.
var scheduler = new DedicatedThreadsTaskScheduler(threadCount: 2);
var tasks = new List<Task>();
for (int i = 0; i < 5; i++)
{
    int num = i;
    var task = Task.Factory.StartNew(() =>
    {
        Console.WriteLine($"{sw.Elapsed.TotalSeconds}: Starting {num}...");
        Thread.Sleep((num + 1) * 1000);
        Console.WriteLine($"{sw.Elapsed.TotalSeconds}: Finishing {num}");
    }, CancellationToken.None, TaskCreationOptions.None, scheduler);
    
    tasks.Add(task);
}

await Task.WhenAll(tasks);
```

In this case, we're creating tasks in the loop, but in reality it might be in a request handler of some sort. Here is the output:

```
0.0154143: Starting 0...
0.0162219: Starting 1...
1.0262272: Finishing 0
1.0265169: Starting 2...
2.0224863: Finishing 1
2.0227441: Starting 3...
4.0417418: Finishing 2
4.041956: Starting 4...
6.0332304: Finishing 3
9.0453789: Finishing 4
```

As you can see, once the task 0 is done, we instantly schedule task 1 etc, so indeed we limit the concurrency here.

But lets make one small change:
```csharp
static async Task FooBarAsync()
{
    await Task.Run(() => 42);
}

...
var task = Task.Factory.StartNew(() =>
{
    Console.WriteLine($"{sw.Elapsed.TotalSeconds}: Starting {num}...");
    Thread.Sleep((num + 1) * 1000);
    FooBarAsync().GetAwaiter().GetResult();
    Console.WriteLine($"{sw.Elapsed.TotalSeconds}: Finishing {num}");
}, CancellationToken.None, TaskCreationOptions.None, scheduler);
```

And the output is:
```
0.0176502: Starting 1...
0.0180366: Starting 0...
```

Yep. A deadlock! Why? Let's update an example to see the issue better: let's trace the current `TaskScheduler` and reduce the number of created tasks in the loop to 1:

```csharp
static void Trace(string message) => 
    Console.WriteLine($"{message}, TS: {TaskScheduler.Current.GetType().Name}");

static async Task FooBarAsync()
{
    Trace("Starting FooBarAsync");
    await Task.Run(() => 42);
    Trace("Finishing FooBarAsync");
}

static async Task Main(string[] args)
{
    var sw = Stopwatch.StartNew();
    var scheduler = new DedicatedThreadsTaskScheduler(threadCount: 2);
    var tasks = new List<Task>();
    for (int i = 0; i < 1; i++)
    {
        int num = i;
        var task = Task.Factory.StartNew(() =>
        {
            Trace($"{sw.Elapsed.TotalSeconds}: Starting {num}...");
            Thread.Sleep((num + 1) * 1000);
            FooBarAsync().GetAwaiter().GetResult();
            Trace($"{sw.Elapsed.TotalSeconds}: Finishing {num}...");
        }, CancellationToken.None, TaskCreationOptions.None, scheduler);
        
        tasks.Add(task);
    }

	Trace("Done scheduling tasks...");
    await Task.WhenAll(tasks);
}
```

The output is:

```
0.018728: Starting 0..., TS: DedicatedThreadsTaskScheduler
Starting FooBarAsync, TS: DedicatedThreadsTaskScheduler
Finishing FooBarAsync, TS: DedicatedThreadsTaskScheduler
1.028004: Finishing 0..., TS: DedicatedThreadsTaskScheduler
Done scheduling tasks..., TS: ThreadPoolTaskScheduler
```

Now it should be relatively easy to understand, what's going on and why when we tried running more than 2 tasks we got a deadlock. Remember, each step in an async method (the code between `await` keywords) is a task by itself, executed one by one by a task scheduler. And by default the task scheduler is sticky: if it was provided when the task was created, then all the continuations are going to use the same one. It means that **the task scheduler flows through the awaits in the async methods**.

In our case, it means that when `FooAsync` is done, our `DedicatedThreadsTaskScheduler` gets called to run it's continuation. But it's already busy running all the tasks so it can't run a trivial piece of code at the end of `FooAsync`. And because `FooAsync` can't be finished, we can't finish the work the task scheduler runs at a moment. Causing a deadlock.

What can we do to solve this?
## Solutions
There are a few ways how to avoid this issue:
### 1. Use `ConfigureAwait(false)`:

```csharp
static async Task FooBarAsync()
{
    Trace("Starting FooBarAsync");
    await Task.Run(() => 42);
    Trace("Finishing FooBarAsync");
}
```

The issue we're seeing here is very similar to a deadlock in the UI case, when a task is blocked and a single UI thread is unavailable to run the continuation.

We can avoid the issue by making sure we have `ConfigureAwait(false)` in every async method. Here is the output for a single item in a pool with the following `FooBarAsync` impl:

```csharp
static async Task FooBarAsync()
{
    Trace("Starting FooBarAsync");
    await Task.Run(() => 42).ConfigureAwait(false);
    Trace("Finishing FooBarAsync");
}
```

```
0.0397394: Starting 0..., TS: DedicatedThreadsTaskScheduler
Starting FooBarAsync, TS: DedicatedThreadsTaskScheduler
**Finishing FooBarAsync, TS: ThreadPoolTaskScheduler**
1.0876967: Finishing 0..., TS: DedicatedThreadsTaskScheduler
```

One might say that this is the right solution to this problem, but I would disagree with it.
In a real case in one of our projects, a blocking async method was in a library code that is hard to fix. You can make sure that your code follows the best practices by using analyzers, but its not practical to expect that everyone follows them.

The biggest issue here, is that this is an uncommon case. There are many backend systems that work perfectly fine without `ConfigureAwait(false)` because the team doesn't have any UI with synchronization contexts, and the fact that the task schedulers behave the same way is not a widely known thing.

And I just feel that there are just better options.
### 2. Control the concurrency in a more explicit way
I think that concurrency control (a.k.a. rate limiting) is very important aspect of an application, and important aspects should be explicit. 

The `TaskScheduler` is quite low level tool and I would prefer to have something higher level instead. If the work is CPU intensive, then PLINQ, or something like `ActionBlock` from TPL DataFlow is probably a better option. 

If the work is mostly IO-bound and asynchronous, then you can use `Parallel.ForEachAsync`, [`Polly.RateLimiting`](https://github.com/App-vNext/Polly/blob/main/docs/strategies/rate-limiter.md) or a custom helper class based on `SemaphoreSlim`.

## Conclusion
A custom task scheduler is just a tool, and like any tool it might be used correctly or incorrectly. If you need a scheduler that knows about UI, then a task scheduler is for you. But should you use one for concurrency and parallelism control in your app? I would vote against it. It's possible the team had legitimate reasons many years ago, but double check if those reasons exist today.

And yes, remember that blocking async call might bite you in variety of ways and the task scheduler case is just one of them. So I would recommend having a comment on every blocking call explaining why you think its safe and useful to do.