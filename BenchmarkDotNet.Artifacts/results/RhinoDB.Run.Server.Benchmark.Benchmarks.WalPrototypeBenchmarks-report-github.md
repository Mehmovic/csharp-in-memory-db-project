```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9445)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                               | Mean        | Error       | StdDev      | Gen0     | Allocated |
|------------------------------------- |------------:|------------:|------------:|---------:|----------:|
| SingleConfirmedSettledKeyUpdate      |    420.1 μs |    243.1 μs |    13.33 μs |        - |     488 B |
| BatchFreshKeyConfirmedInserts        | 71,535.5 μs | 20,060.2 μs | 1,099.57 μs | 375.0000 | 2432246 B |
| ConcurrentConfirmedFreshKeyInserts   |    446.7 μs |    157.3 μs |     8.62 μs |   0.4883 |    5120 B |
| ConcurrentConfirmedSettledKeyUpdates |    447.7 μs |    211.0 μs |    11.57 μs |   0.4883 |    4967 B |
