```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9445)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host] : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Dry    : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                     | CommitCoalescingWindowMs | Mean      | Error | Allocated |
|--------------------------- |------------------------- |----------:|------:|----------:|
| **ConcurrentConfirmedInserts** | **0**                        |  **9.739 ms** |    **NA** |  **14.39 KB** |
| ConcurrentConfirmedUpdates | 0                        | 13.015 ms |    NA |  13.46 KB |
| **ConcurrentConfirmedInserts** | **20**                       | **34.286 ms** |    **NA** |  **10.09 KB** |
| ConcurrentConfirmedUpdates | 20                       | 35.244 ms |    NA |  10.21 KB |
