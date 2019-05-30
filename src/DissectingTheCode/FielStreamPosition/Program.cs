using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace FielStreamPosition
{
    class Program
    {
        static void Main(string[] args)
        {
            new CheckFilePositionSafety().ReadFileStreamPositionFromDifferentThreadWithSafeFileHandleExposed();

        }
    }
}
