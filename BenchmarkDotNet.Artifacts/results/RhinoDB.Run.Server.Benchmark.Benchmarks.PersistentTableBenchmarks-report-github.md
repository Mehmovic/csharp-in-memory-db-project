```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9445)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host] : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Dry    : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method           | RecordCount | Mean        | Error | Allocated |
|----------------- |------------ |------------:|------:|----------:|
| **Get**              | **100**         |  **3,562.3 μs** |    **NA** |         **-** |
| InsertOptimistic | 100         |  2,639.7 μs |    NA |         - |
| InsertConfirmed  | 100         |  4,600.0 μs |    NA |         - |
| Update           | 100         |  2,824.8 μs |    NA |         - |
| **Get**              | **10000**       |  **3,536.7 μs** |    **NA** |         **-** |
| InsertOptimistic | 10000       |  3,578.4 μs |    NA |         - |
| InsertConfirmed  | 10000       |  4,973.9 μs |    NA |         - |
| Update           | 10000       |  4,077.6 μs |    NA |         - |
| **Get**              | **1000000**     |  **2,952.7 μs** |    **NA** |         **-** |
| InsertOptimistic | 1000000     |    995.7 μs |    NA |         - |
| InsertConfirmed  | 1000000     | 31,947.8 μs |    NA |         - |
| Update           | 1000000     |  1,609.1 μs |    NA |         - |
