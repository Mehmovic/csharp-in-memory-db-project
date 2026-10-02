```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                 | Mean          | Error          | StdDev      | Ratio      | RatioSD   | Gen0   | Allocated | Alloc Ratio |
|----------------------- |--------------:|---------------:|------------:|-----------:|----------:|-------:|----------:|------------:|
| Direct_NoGuard         |     0.1478 ns |      0.7678 ns |   0.0421 ns |      1.049 |      0.35 |      - |         - |          NA |
| Guarded_TryCatch       |     0.0000 ns |      0.0000 ns |   0.0000 ns |      0.000 |      0.00 |      - |         - |          NA |
| Guarded_ActuallyThrows |   937.5984 ns |    326.5504 ns |  17.8993 ns |  6,656.899 |  1,435.64 | 0.0343 |     216 B |          NA |
| Run_ReturnsResultError | 3,457.7543 ns |  4,887.1649 ns | 267.8820 ns | 24,549.870 |  5,539.37 |      - |      32 B |          NA |
| Run_DelegateThrows     | 6,459.2100 ns | 11,793.7208 ns | 646.4536 ns | 45,860.045 | 10,661.54 | 0.0458 |     352 B |          NA |
