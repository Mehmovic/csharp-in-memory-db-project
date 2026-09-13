```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9445)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                     | CommitCoalescingWindowMs | Mean      | Error    | StdDev    | Allocated |
|--------------------------- |------------------------- |----------:|---------:|----------:|----------:|
| **ConcurrentConfirmedUpdates** | **0**                        |  **1.775 ms** | **7.546 ms** | **0.4136 ms** |  **14.49 KB** |
| **ConcurrentConfirmedUpdates** | **20**                       | **31.087 ms** | **3.100 ms** | **0.1699 ms** |  **11.19 KB** |
