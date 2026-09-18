```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]    : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  MediumRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method               | Mean     | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------- |---------:|---------:|---------:|------:|--------:|----------:|------------:|
| BTree_PointLookup    | 18.49 ns | 0.634 ns | 0.930 ns |  1.00 |    0.07 |         - |          NA |
| RedBlack_PointLookup | 25.13 ns | 0.583 ns | 0.837 ns |  1.36 |    0.08 |         - |          NA |
