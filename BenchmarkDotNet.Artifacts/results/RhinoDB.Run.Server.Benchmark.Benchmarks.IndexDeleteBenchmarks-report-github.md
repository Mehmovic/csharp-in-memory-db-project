```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                    | RecordCount | Mean          | Error          | StdDev        | Ratio | RatioSD | Allocated | Alloc Ratio |
|-------------------------- |------------ |--------------:|---------------:|--------------:|------:|--------:|----------:|------------:|
| **BTree_DeleteSequential**    | **100**         |      **14.52 μs** |       **5.574 μs** |      **0.306 μs** |  **1.00** |    **0.03** |         **-** |          **NA** |
| RedBlack_DeleteSequential | 100         |      38.73 μs |     153.425 μs |      8.410 μs |  2.67 |    0.50 |         - |          NA |
| BTree_DeleteRandom        | 100         |      16.70 μs |      19.734 μs |      1.082 μs |  1.15 |    0.07 |         - |          NA |
| RedBlack_DeleteRandom     | 100         |      31.68 μs |      29.266 μs |      1.604 μs |  2.18 |    0.10 |         - |          NA |
|                           |             |               |                |               |       |         |           |             |
| **BTree_DeleteSequential**    | **10000**       |   **1,477.67 μs** |   **2,679.186 μs** |    **146.855 μs** |  **1.01** |    **0.12** |         **-** |          **NA** |
| RedBlack_DeleteSequential | 10000       |   5,505.23 μs |   8,824.702 μs |    483.712 μs |  3.75 |    0.42 |         - |          NA |
| BTree_DeleteRandom        | 10000       |   2,136.27 μs |   4,459.135 μs |    244.420 μs |  1.45 |    0.19 |         - |          NA |
| RedBlack_DeleteRandom     | 10000       |   5,482.83 μs |   8,968.588 μs |    491.599 μs |  3.73 |    0.42 |         - |          NA |
|                           |             |               |                |               |       |         |           |             |
| **BTree_DeleteSequential**    | **1000000**     | **108,594.87 μs** | **115,448.645 μs** |  **6,328.130 μs** |  **1.00** |    **0.07** |         **-** |          **NA** |
| RedBlack_DeleteSequential | 1000000     | 128,746.53 μs | 347,598.258 μs | 19,053.034 μs |  1.19 |    0.16 |         - |          NA |
| BTree_DeleteRandom        | 1000000     | 438,794.77 μs | 524,618.214 μs | 28,756.095 μs |  4.05 |    0.31 |         - |          NA |
| RedBlack_DeleteRandom     | 1000000     | 782,966.60 μs | 733,721.921 μs | 40,217.774 μs |  7.23 |    0.49 |         - |          NA |
