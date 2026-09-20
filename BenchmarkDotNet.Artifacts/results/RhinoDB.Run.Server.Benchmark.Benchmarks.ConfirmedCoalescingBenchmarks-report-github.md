```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]     : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                     | Mean     | Error     | StdDev    | Allocated |
|--------------------------- |---------:|----------:|----------:|----------:|
| ConcurrentConfirmedInserts | 1.221 ms | 0.0334 ms | 0.0942 ms |  15.19 KB |
| ConcurrentConfirmedUpdates | 1.223 ms | 0.0269 ms | 0.0775 ms |  14.85 KB |
