```

BenchmarkDotNet v0.13.11, Windows 11 (10.0.22621.2715/22H2/2022Update/SunValley2)
11th Gen Intel Core i7-1185G7 3.00GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 8.0.100
  [Host]     : .NET 7.0.14 (7.0.1423.51910), X64 RyuJIT AVX2
  Job-RZRSSU : .NET 7.0.14-servicing.23519.10, X64 NativeAOT AVX2

Runtime=NativeAOT 7.0  

```
| Method             | Count   | Mean        | Error       | StdDev      | Allocated |
|------------------- |-------- |------------:|------------:|------------:|----------:|
| **String_Intern**      | **10000**   |    **196.8 μs** |     **3.82 μs** |     **3.92 μs** |   **4.11 KB** |
| StringCache_Intern | 10000   |    211.9 μs |     4.15 μs |     5.67 μs |   4.11 KB |
| **String_Intern**      | **100000**  |  **1,680.1 μs** |    **47.58 μs** |   **140.28 μs** |   **4.14 KB** |
| StringCache_Intern | 100000  |  2,102.1 μs |    86.83 μs |   250.53 μs |   4.13 KB |
| **String_Intern**      | **1000000** | **31,059.8 μs** | **1,349.33 μs** | **3,827.82 μs** |   **4.16 KB** |
| StringCache_Intern | 1000000 | 40,368.6 μs | 1,279.83 μs | 3,713.02 μs |   4.15 KB |
