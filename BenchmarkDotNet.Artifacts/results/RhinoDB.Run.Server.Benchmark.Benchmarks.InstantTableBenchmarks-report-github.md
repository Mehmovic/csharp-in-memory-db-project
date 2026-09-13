```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9445)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host] : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Dry    : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method | RecordCount | Mean     | Error | Allocated |
|------- |------------ |---------:|------:|----------:|
| **Get**    | **100**         | **3.536 ms** |    **NA** |         **-** |
| **Get**    | **10000**       | **3.585 ms** |    **NA** |         **-** |
| **Get**    | **1000000**     | **2.130 ms** |    **NA** |         **-** |
