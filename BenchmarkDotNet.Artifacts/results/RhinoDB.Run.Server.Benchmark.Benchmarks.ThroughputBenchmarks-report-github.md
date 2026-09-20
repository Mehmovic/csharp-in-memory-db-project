```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                | Mean     | Error    | StdDev    | Gen0      | Gen1     | Allocated |
|-------------------------------------- |---------:|---------:|----------:|----------:|---------:|----------:|
| InstantConcurrentInserts              | 3.589 ms | 1.496 ms | 0.0820 ms |  375.0000 | 140.6250 |   2.29 MB |
| InstantConcurrentUpdates              | 3.317 ms | 1.862 ms | 0.1021 ms |  367.1875 | 273.4375 |   2.21 MB |
| PersistentOptimisticConcurrentInserts | 9.089 ms | 6.927 ms | 0.3797 ms | 1093.7500 | 687.5000 |  10.21 MB |
| PersistentOptimisticConcurrentUpdates | 8.411 ms | 5.237 ms | 0.2870 ms | 1093.7500 | 843.7500 |   6.56 MB |
