```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                            | Mean       | Error      | StdDev    | Gen0       | Gen1      | Gen2      | Allocated |
|---------------------------------- |-----------:|-----------:|----------:|-----------:|----------:|----------:|----------:|
| Scale_BulkLoad_1M_BTree           |   133.4 ms |   102.6 ms |   5.62 ms |  1250.0000 |  500.0000 |         - |   11.9 MB |
| Scale_BulkLoad_4M_BTree           | 1,081.6 ms | 4,671.9 ms | 256.08 ms |  5000.0000 | 2000.0000 |         - |  47.59 MB |
| Scale_BulkLoad_10M_BTree          | 3,448.3 ms |   949.5 ms |  52.05 ms | 14000.0000 | 7000.0000 | 1000.0000 | 142.98 MB |
| Scale_BulkLoad_10M_NonUniqueBTree | 3,211.0 ms | 4,773.0 ms | 261.63 ms | 14000.0000 | 7000.0000 | 1000.0000 | 142.98 MB |
