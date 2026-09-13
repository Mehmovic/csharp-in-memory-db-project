```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9445)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method           | RecordCount | Mean         | Error         | StdDev      | Gen0   | Allocated |
|----------------- |------------ |-------------:|--------------:|------------:|-------:|----------:|
| **Get**              | **100**         |     **2.864 μs** |     **0.2655 μs** |   **0.0146 μs** | **0.0763** |     **490 B** |
| InsertOptimistic | 100         |   929.923 μs |   410.1098 μs |  22.4795 μs |      - |    1175 B |
| InsertConfirmed  | 100         |   929.890 μs |   195.2531 μs |  10.7025 μs |      - |    1177 B |
| Update           | 100         |   927.061 μs |   506.4541 μs |  27.7605 μs |      - |     808 B |
| **Get**              | **10000**       |     **2.768 μs** |     **0.2019 μs** |   **0.0111 μs** | **0.0782** |     **490 B** |
| InsertOptimistic | 10000       |   934.522 μs |   333.8490 μs |  18.2994 μs |      - |     833 B |
| InsertConfirmed  | 10000       |   921.315 μs |   252.9589 μs |  13.8655 μs |      - |    1177 B |
| Update           | 10000       |   924.233 μs |   491.7188 μs |  26.9528 μs |      - |     809 B |
| **Get**              | **1000000**     |     **2.681 μs** |     **0.8647 μs** |   **0.0474 μs** | **0.0763** |     **489 B** |
| InsertOptimistic | 1000000     |   195.191 μs |   140.0204 μs |   7.6750 μs | 0.1221 |     848 B |
| InsertConfirmed  | 1000000     | 2,400.504 μs | 4,600.1956 μs | 252.1522 μs |      - |    1179 B |
| Update           | 1000000     |   115.540 μs |   198.5706 μs |  10.8843 μs | 0.1221 |     808 B |
