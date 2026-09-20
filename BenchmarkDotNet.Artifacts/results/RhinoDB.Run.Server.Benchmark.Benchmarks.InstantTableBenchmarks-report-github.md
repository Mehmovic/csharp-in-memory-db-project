```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host] : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Dry    : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method     | RecordCount | Mean     | Error | Allocated |
|----------- |------------ |---------:|------:|----------:|
| **Insert**     | **100**         | **1.998 ms** |    **NA** |         **-** |
| InsertArgs | 100         | 2.176 ms |    NA |         - |
| **Insert**     | **10000**       | **2.159 ms** |    **NA** |         **-** |
| InsertArgs | 10000       | 2.698 ms |    NA |         - |
| **Insert**     | **1000000**     | **1.833 ms** |    **NA** |         **-** |
| InsertArgs | 1000000     | 2.241 ms |    NA |         - |
