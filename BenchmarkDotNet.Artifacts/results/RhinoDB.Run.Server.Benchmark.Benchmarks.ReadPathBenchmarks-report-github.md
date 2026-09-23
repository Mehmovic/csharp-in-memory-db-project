```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                     | RecordCount | Mean       | Error     | StdDev   | Allocated |
|--------------------------- |------------ |-----------:|----------:|---------:|----------:|
| Enqueue_NoOp               | 10000       | 2,643.4 ns | 181.59 ns |  9.95 ns |         - |
| SubmitBatch_NoAwait_NoOp   | 10000       |   186.0 ns | 219.75 ns | 12.05 ns |       1 B |
| Enqueue_Find               | 10000       | 2,578.6 ns | 253.10 ns | 13.87 ns |         - |
| SubmitBatch_NoAwait_Find   | 10000       |   184.5 ns |  78.05 ns |  4.28 ns |       2 B |
| SubmitBatch_AwaitEach_Find | 10000       |   256.8 ns |  45.66 ns |  2.50 ns |       3 B |
