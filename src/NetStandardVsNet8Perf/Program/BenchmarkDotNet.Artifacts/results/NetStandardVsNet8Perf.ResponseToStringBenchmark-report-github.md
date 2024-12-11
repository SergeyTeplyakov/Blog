```

BenchmarkDotNet v0.13.12, macOS Sonoma 14.3.1 (23D60) [Darwin 23.3.0]
Apple M3 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 8.0.203
  [Host]     : .NET 8.0.3 (8.0.324.11423), Arm64 RyuJIT AdvSIMD
  DefaultJob : .NET 8.0.3 (8.0.324.11423), Arm64 RyuJIT AdvSIMD


```
| Method          | Mean     | Error   | StdDev  | Ratio | Gen0   | Allocated | Alloc Ratio |
|---------------- |---------:|--------:|--------:|------:|-------:|----------:|------------:|
| DefaultToString | 183.4 ns | 2.75 ns | 2.44 ns |  1.00 | 0.0362 |     304 B |        1.00 |
| ToStringFast    | 101.0 ns | 0.94 ns | 0.88 ns |  0.55 | 0.0143 |     120 B |        0.39 |
