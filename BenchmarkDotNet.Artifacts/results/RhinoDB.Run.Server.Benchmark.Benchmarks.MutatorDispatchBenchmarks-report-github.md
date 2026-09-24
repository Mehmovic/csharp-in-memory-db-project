```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method             | Mean     | Error      | StdDev    | Median   | Allocated |
|------------------- |---------:|-----------:|----------:|---------:|----------:|
| Loop_StructMutator | 172.3 ns | 1,168.6 ns |  64.06 ns | 135.4 ns |         - |
| Loop_ClassMutator  | 267.8 ns |   501.0 ns |  27.46 ns | 256.2 ns |         - |
| Loop_Direct        | 398.7 ns | 2,132.8 ns | 116.90 ns | 454.3 ns |         - |
