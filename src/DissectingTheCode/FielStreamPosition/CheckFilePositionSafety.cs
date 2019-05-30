using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;

namespace FielStreamPosition
{
    [TestFixture]
    public class CheckFilePositionSafety
    {
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
                    while (!cts.IsCancellationRequested)
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

        [Test]
        public void ReadFileStreamPositionFromDifferentThreadWithSafeFileHandleExposed()
        {
            const string path = "test.txt";
            int N = 10_000;
            int blockSize = 1024;

            using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(fileStream))
            {
                var handle = fileStream.SafeFileHandle;
                var cts = new CancellationTokenSource();
                // Start a background position reader
                Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        // Tracing the position. In this case, just obtaining it.
                        long currentPosition = fileStream.Position;
                        await Task.Delay(1);
                    }
                });

                for (int i = 0; i < N; i++)
                {
                    // Generate blocks of 'a's, then 'b's etc to 'z's
                    var output = new string((char)('a' + (i % 26)), blockSize);
                    writer.WriteLine(output);
                }

                cts.Cancel();
            }

            var fileLength = new FileInfo(path).Length;
            // Need to count \r\n as well
            var expectedLength = (blockSize + Environment.NewLine.Length) * N;
            Assert.That(fileLength, Is.EqualTo(expectedLength));
        }
    }
}
