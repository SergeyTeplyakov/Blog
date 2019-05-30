---
layout: post
title: Shooting Yourself in the Foot with Concurrent Use of FileStream.Position
categories: concurrency
---

Let's explore the following scenario: you have a service that copies files between machines. And to track the progress there is a special "copy watcher" thread (or task) that logs a current position of the target stream by accessing a `FileStream.Position` property.

The question is: how safe or unsafe the access to `FileStream.Position` from another thread is? Of course, without any synchronization in place, the "watcher" could be a bit off and get a previous file position. And because `Position` property is of type `long` the read operation could yield some very weird results on a 32-bit platform for files larger than 2Gb. And, of course, the runtime could potentially do some weird optimizations due to lack of synchronization (even though this is not likely to happen in practice).

But is it possible for the watcher thread to affect the copy operation in a more drastic way? Like to corrupt the file?

Let's do an experiment.

```csharp
[Test]
public void ReadFileStreamPositionFromDifferentThread()
{
    const string path = "test.txt";
    int N = 10_000;
    int blockSize = 1024;

    using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write))
    using (var writer = new StreamWriter(fileStream))
    {
        var cts = new CancellationTokenSource();
        // Start a background position reader
        Task.Run(async () =>
        {
            while (cts.IsCancellationRequested)
            {
                // Tracing the position. In this case, just obtaining it.
                long currentPosition = fileStream.Position;
                await Task.Delay(1);
            }
        });

        for (int i = 0; i < N; i++)
        {
            // Generate blocks of 'a's, then 'b's etc to 'z's
            var output = new string((char)('a' + (i%26)), blockSize);
            writer.WriteLine(output);
        }

        cts.Cancel();
    }

    var fileLength = new FileInfo(path).Length;
    // Need to count \r\n as well
    var expectedLength = (blockSize + Environment.NewLine.Length) * N;
    Assert.That(fileLength, Is.EqualTo(expectedLength));
}
```

We have a very simple code that writes synchronously to a file with blocks of 1024 characters `N` times. We can increase the `N` to be in millions, we can deploy this code to production and never see any errors for years. So we can make a conclusion that it is safe to read the `FileStream.Position` property while the other thread writes the content to the file.

And then we make a simple change. We either call [`FileStream.SafeFileHandle`](https://referencesource.microsoft.com/#mscorlib/system/io/filestream.cs,1424) property on a `FileStream` instance or we start creating a `FileStream` by calling, for instance, `new FileStream(safeHandle, FileAccess.Write)`.

```csharp
[Test]
public void ReadFileStreamPositionFromDifferentThreadWithSafeFileHandleExposed()
{
    const string path = "test.txt";
    int N = 10_000;
    int blockSize = 1024;

    using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write))
    using (var writer = new StreamWriter(fileStream))
    {
        // This is the key difference here: touching SafeFileHandle property.
        var handle = fileStream.SafeFileHandle;
        var cts = new CancellationTokenSource();
        // Start a background position reader
        Task.Run(async () =>
        {
            while (cts.IsCancellationRequested)
            {
                // Tracing the position. In this case, just obtaining it.
                long currentPosition = fileStream.Position;
                await Task.Delay(1);
            }
        });

        for (int i = 0; i < N; i++)
        {
            // Generate blocks of 'a's, then 'b's etc to 'z's
            var output = new string((char)('a' + (i%26)), blockSize);
            writer.WriteLine(output);
        }

        cts.Cancel();
    }

    var fileLength = new FileInfo(path).Length;
    // Need to count \r\n as well
    var expectedLength = (blockSize + Environment.NewLine.Length) * N;
    Assert.That(fileLength, Is.EqualTo(expectedLength));
}
```

And now, if we run the test, we'll get a failure, `Expected: 10260000 But was: 10258976`. What. Is. Going. On. Here?

When the internal file handle is exposed (by calling `FileStream.SafeFileHandle` or by creating a `FileStream` instance by a given `SafeFileHandle`), then a `FileStream` instance forces some additional internal safety checks. If [`FileStream._exposedHandle`](https://referencesource.microsoft.com/#mscorlib/system/io/filestream.cs,97ee1b120c3a577d,references) is true, then every read, write, flush or `Position` getter calls [`VerifyOSHandlePosition`](https://referencesource.microsoft.com/#mscorlib/system/io/filestream.cs,d391a0793d74a40b), that calls `SeekCore(0, SeekOrigin.Current)` that reads the current position of the file and **updates a current position** by [changing `_pos` field](https://referencesource.microsoft.com/#mscorlib/system/io/filestream.cs,1721).

It means, that if `_exposedHandle` is true, the call to `FileStream.Position` is no longer pure! It updates a `FileStream` internal state that can affect a write operation happening in the other thread. To understand the problem, let's take a look at [`FileStream.BeginWriteCore`](https://referencesource.microsoft.com/#mscorlib/system/io/filestream.cs,2270) implementation (that is called from synchronous `Write` as well):

```csharp
unsafe private FileStreamAsyncResult BeginWriteCore(byte[] bytes, int offset, int numBytes, AsyncCallback userCallback, Object stateObject) 
{
    // Create and store async stream class library specific data in the async result
    FileStreamAsyncResult asyncResult = new FileStreamAsyncResult(0, bytes, _handle, userCallback, stateObject, true);
    NativeOverlapped* intOverlapped = asyncResult.OverLapped;

    if (CanSeek) {
        // Make sure we set the length of the file appropriately.
        long len = Length;
        //Console.WriteLine("BeginWrite - Calculating end pos.  pos: "+pos+"  len: "+len+"  numBytes: "+numBytes);
        
        // Make sure we are writing to the position that we think we are
        if (_exposedHandle)
            VerifyOSHandlePosition();
        
        if (_pos + numBytes > len) {
            //Console.WriteLine("BeginWrite - Setting length to: "+(pos + numBytes));
            SetLengthCore(_pos + numBytes);
        }

        // Now set the position to read from in the NativeOverlapped struct
        // For pipes, we should leave the offset fields set to 0.
        intOverlapped->OffsetLow = (int)_pos;
        intOverlapped->OffsetHigh = (int)(_pos>>32);
 
```

If the file is not yet flushed and the next write operation is called when another thread calls `FileStream.Position` property, then the internal `_pos` field can be changed based on actual file position, effectively losing one of the rights and corrupting the content of the file!

No one should assume that a property is thread-safe unless it's clearly stated in the documentation and there are no such claims for any `FileStream` properties. On the other hand, when we think about thread unsafety due to concurrent reads of a property we rarely think about such drastic effects like corrupted files. [Framework Design Guidelines](https://docs.microsoft.com/en-us/dotnet/standard/design-guidelines/property) taught us to treat properties as smart fields without such drastic side effects like IO operations in a property getter.

I do understand that the `FileStream` implementation tries its best to protect us, the users, from undesirable errors and inconsistent state. But I also believe that such side effects, like potential file corruptions, should be more explicitly documented.

TLDR; Reading a `FileStream.Position` from another thread during write operations when a stream's underlying `SafeFileHandle` is exposed, is extremely dangerous and may cause file corruption.

P.S. The issue could happen in full framework as well as in .NET Core.


It was a very important lesson for me, that even a simple change could have a drastic effect on a distributed system.
We've been running a service with concurrent `Position` reads for many years without any issues and a simple change in the code that switched `FileStream` to an "unsafe" mode caused a very strange and hard to understand issues in the system. But that was a very useful lesson for me anyway.


P.S. The issue affects both .NET Framework version as well as .NET Core version of `FileStream`.