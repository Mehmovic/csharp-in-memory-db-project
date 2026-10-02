```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]     : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-PJJBIC : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

InvocationCount=1  UnrollFactor=1  

```
| Method                   | Mean        | Error     | StdDev     | Median       | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------- |------------:|----------:|-----------:|-------------:|------:|--------:|----------:|------------:|
| BTree_Insert_Ascending   |    58.73 ns |  2.814 ns |   8.120 ns |    57.850 ns |  1.02 |    0.20 |     114 B |        1.00 |
| BTree_Insert_Scattered   |   387.44 ns | 15.121 ns |  42.152 ns |   367.350 ns |  6.72 |    1.16 |         - |        0.00 |
| BTree_Delete             |   418.26 ns | 30.799 ns |  87.372 ns |   377.550 ns |  7.25 |    1.80 |         - |        0.00 |
| Hash_Insert_Ascending    |    10.93 ns |  2.199 ns |   6.380 ns |     9.225 ns |  0.19 |    0.11 |         - |        0.00 |
| Hash_Insert_Scattered    |    47.47 ns |  3.431 ns |   9.450 ns |    46.550 ns |  0.82 |    0.20 |         - |        0.00 |
| Hash_Delete              |    83.16 ns | 13.427 ns |  39.380 ns |    64.850 ns |  1.44 |    0.71 |         - |        0.00 |
| NonUniqueBTree_Insert    | 1,505.29 ns | 49.525 ns | 143.680 ns | 1,484.500 ns | 26.10 |    4.29 |    1273 B |       11.17 |
| NonUniqueBTree_Delete    |   522.82 ns | 31.339 ns |  91.913 ns |   507.800 ns |  9.07 |    2.01 |         - |        0.00 |
| BTree_Insert_ForcedSplit |    41.31 ns |  2.753 ns |   7.943 ns |    40.725 ns |  0.72 |    0.17 |      38 B |        0.33 |
