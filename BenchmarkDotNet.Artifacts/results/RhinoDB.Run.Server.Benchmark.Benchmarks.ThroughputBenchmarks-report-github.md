```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9445)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                | Mean       | Error       | StdDev   | Allocated |
|-------------------------------------- |-----------:|------------:|---------:|----------:|
| PersistentOptimisticConcurrentInserts | 9,464.4 ms | 1,786.52 ms | 97.93 ms |   3.77 MB |
| PersistentOptimisticConcurrentUpdates |   452.4 ms |    46.16 ms |  2.53 ms |   3.66 MB |
