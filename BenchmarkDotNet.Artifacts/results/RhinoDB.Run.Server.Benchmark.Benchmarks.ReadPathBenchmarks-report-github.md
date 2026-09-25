```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]     : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method       | RecordCount | Mean     | Error     | StdDev    | Allocated |
|------------- |------------ |---------:|----------:|----------:|----------:|
| Enqueue_NoOp | 10000       | 3.404 μs | 0.0603 μs | 0.0534 μs |      32 B |
| Enqueue_Find | 10000       | 3.395 μs | 0.0555 μs | 0.0779 μs |      32 B |
