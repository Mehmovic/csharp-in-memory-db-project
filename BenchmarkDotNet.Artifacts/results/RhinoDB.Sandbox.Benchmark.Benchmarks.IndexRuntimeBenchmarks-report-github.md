```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]     : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-EHYMSW : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

InvocationCount=1  UnrollFactor=1  

```
| Method                   | Mean         | Error     | StdDev    | Median       | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------- |-------------:|----------:|----------:|-------------:|------:|--------:|----------:|------------:|
| BTree_Insert_Ascending   |    23.551 ns |  1.810 ns |  5.280 ns |    22.950 ns |  1.05 |    0.33 |       9 B |        1.00 |
| BTree_Insert_Scattered   |   313.153 ns |  6.225 ns |  9.125 ns |   310.225 ns | 13.94 |    3.00 |         - |        0.00 |
| BTree_Delete             |   274.768 ns |  5.410 ns |  9.331 ns |   273.425 ns | 12.23 |    2.64 |         - |        0.00 |
| Hash_Insert_Ascending    |     8.922 ns |  1.403 ns |  4.091 ns |     8.575 ns |  0.40 |    0.20 |         - |        0.00 |
| Hash_Insert_Scattered    |    43.620 ns |  2.862 ns |  8.347 ns |    44.675 ns |  1.94 |    0.56 |         - |        0.00 |
| Hash_Delete              |    82.034 ns | 12.121 ns | 35.548 ns |    64.950 ns |  3.65 |    1.79 |         - |        0.00 |
| NonUniqueBTree_Insert    | 1,908.730 ns | 37.031 ns | 42.645 ns | 1,895.600 ns | 84.96 |   18.20 |    1443 B |      160.33 |
| NonUniqueBTree_Delete    |   321.054 ns |  6.370 ns | 10.287 ns |   319.800 ns | 14.29 |    3.08 |         - |        0.00 |
| BTree_Insert_ForcedSplit |    21.382 ns |  1.784 ns |  5.089 ns |    20.900 ns |  0.95 |    0.31 |      23 B |        2.56 |
