using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileStreamPosition.NetCore
{
    class Program
    {
        static void Main(string[] args)
        {
            const string path = "test.txt";
            int N = 1_000_000;
            int blockSize = 1024;

            var sw = Stopwatch.StartNew();
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

            Console.WriteLine($"File length: {fileLength}, Expected: {expectedLength}, Correct: {expectedLength == fileLength}, Duration: {sw.ElapsedMilliseconds}ms");
        }
    }
}
