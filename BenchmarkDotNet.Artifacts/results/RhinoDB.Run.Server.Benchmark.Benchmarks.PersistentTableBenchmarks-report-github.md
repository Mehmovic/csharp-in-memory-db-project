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
| **Get**              | **100**         |     **2.765 μs** |     **0.7168 μs** |   **0.0393 μs** | **0.0534** |     **354 B** |
| InsertOptimistic | 100         |   941.686 μs |   195.8807 μs |  10.7369 μs |      - |     919 B |
| InsertConfirmed  | 100         |   945.265 μs |   231.4598 μs |  12.6871 μs |      - |    1262 B |
| Update           | 100         |   885.146 μs |   313.2175 μs |  17.1685 μs |      - |     550 B |
| **Get**              | **10000**       |     **2.636 μs** |     **0.7879 μs** |   **0.0432 μs** | **0.0534** |     **354 B** |
| InsertOptimistic | 10000       |   980.724 μs |    88.0025 μs |   4.8237 μs |      - |     577 B |
| InsertConfirmed  | 10000       |   957.273 μs |   155.1898 μs |   8.5065 μs |      - |     921 B |
| Update           | 10000       |   924.466 μs |   897.6647 μs |  49.2040 μs |      - |     553 B |
| **Get**              | **1000000**     |     **2.666 μs** |     **1.5483 μs** |   **0.0849 μs** | **0.0534** |     **354 B** |
| InsertOptimistic | 1000000     |   121.061 μs |   828.3829 μs |  45.4065 μs |      - |     576 B |
| InsertConfirmed  | 1000000     | 2,858.646 μs | 2,451.5475 μs | 134.3776 μs |      - |     923 B |
| Update           | 1000000     |   114.921 μs |   534.1382 μs |  29.2779 μs |      - |     551 B |
