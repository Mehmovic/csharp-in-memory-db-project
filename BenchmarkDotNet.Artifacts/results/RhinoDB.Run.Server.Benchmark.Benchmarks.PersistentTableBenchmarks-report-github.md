```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9445)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method           | RecordCount | Mean         | Error       | StdDev     | Gen0   | Allocated |
|----------------- |------------ |-------------:|------------:|-----------:|-------:|----------:|
| **Get**              | **100**         |     **2.920 μs** |   **0.2735 μs** |  **0.0150 μs** | **0.0534** |     **354 B** |
| InsertOptimistic | 100         | 1,217.253 μs |  75.3890 μs |  4.1323 μs |      - |     608 B |
| InsertConfirmed  | 100         | 1,200.776 μs | 151.8593 μs |  8.3239 μs |      - |    1049 B |
| Update           | 100         |   132.052 μs |  13.0477 μs |  0.7152 μs |      - |     551 B |
| **Get**              | **10000**       |     **2.876 μs** |   **0.0672 μs** |  **0.0037 μs** | **0.0534** |     **354 B** |
| InsertOptimistic | 10000       | 1,038.745 μs | 125.6884 μs |  6.8894 μs |      - |     592 B |
| InsertConfirmed  | 10000       | 1,042.676 μs | 211.7592 μs | 11.6072 μs |      - |     921 B |
| Update           | 10000       |    52.777 μs |  14.9492 μs |  0.8194 μs |      - |     552 B |
| **Get**              | **1000000**     |     **2.769 μs** |   **0.3386 μs** |  **0.0186 μs** | **0.0553** |     **353 B** |
| InsertOptimistic | 1000000     |   139.912 μs |  95.2971 μs |  5.2236 μs |      - |     592 B |
| InsertConfirmed  | 1000000     | 2,429.098 μs | 101.3272 μs |  5.5541 μs |      - |     923 B |
| Update           | 1000000     |    83.210 μs | 332.8139 μs | 18.2427 μs |      - |     552 B |
