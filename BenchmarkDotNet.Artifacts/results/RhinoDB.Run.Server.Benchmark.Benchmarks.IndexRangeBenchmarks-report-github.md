```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9445)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method          | RangeWidth | Mean         | Error          | StdDev       | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------- |----------- |-------------:|---------------:|-------------:|------:|--------:|-------:|----------:|------------:|
| **LiteBTree_Range** | **1**          |     **33.49 ns** |       **9.508 ns** |     **0.521 ns** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| RedBlack_Range  | 1          |    103.88 ns |      10.783 ns |     0.591 ns |  3.10 |    0.04 | 0.0650 |     408 B |          NA |
|                 |            |              |                |              |       |         |        |           |             |
| **LiteBTree_Range** | **100**        |    **230.98 ns** |     **139.378 ns** |     **7.640 ns** |  **1.00** |    **0.04** |      **-** |         **-** |          **NA** |
| RedBlack_Range  | 100        |    841.01 ns |   2,070.174 ns |   113.473 ns |  3.64 |    0.44 | 0.0648 |     408 B |          NA |
|                 |            |              |                |              |       |         |        |           |             |
| **LiteBTree_Range** | **10000**      | **19,665.88 ns** |  **17,969.610 ns** |   **984.975 ns** |  **1.00** |    **0.06** |      **-** |         **-** |          **NA** |
| RedBlack_Range  | 10000      | 69,210.88 ns | 180,267.060 ns | 9,881.046 ns |  3.53 |    0.46 |      - |     408 B |          NA |
