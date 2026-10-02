```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]     : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                             | Mean          | Error        | StdDev        | Median        | Ratio     | RatioSD  | Gen0    | Gen1    | Gen2    | Allocated | Alloc Ratio |
|----------------------------------- |--------------:|-------------:|--------------:|--------------:|----------:|---------:|--------:|--------:|--------:|----------:|------------:|
| Lookup_NumericTuple                |      19.17 ns |     0.368 ns |      1.026 ns |      18.61 ns |      1.00 |     0.07 |       - |       - |       - |         - |          NA |
| Lookup_StringTuple_Ordinal         |      27.78 ns |     0.497 ns |      0.817 ns |      27.66 ns |      1.45 |     0.08 |       - |       - |       - |         - |          NA |
| Lookup_StringTuple_DefaultComparer |      53.73 ns |     1.040 ns |      0.973 ns |      53.24 ns |      2.81 |     0.15 |       - |       - |       - |         - |          NA |
| Range_NumericTuple_Width100        |     449.52 ns |     8.981 ns |     25.035 ns |     436.22 ns |     23.51 |     1.75 |  0.0849 |       - |       - |     536 B |          NA |
| Range_StringTuple_Ordinal_Width100 |     530.40 ns |     6.492 ns |      6.376 ns |     528.17 ns |     27.75 |     1.41 |  0.1001 |       - |       - |     632 B |          NA |
| Range_StringTuple_Default_Width100 |     671.87 ns |    13.462 ns |     30.109 ns |     658.91 ns |     35.15 |     2.34 |  0.1001 |       - |       - |     632 B |          NA |
| Scan_NumericTuple_All              | 255,994.00 ns | 8,979.818 ns | 25,764.784 ns | 249,784.25 ns | 13,391.43 | 1,497.08 | 14.6484 | 14.6484 | 14.6484 |  524320 B |          NA |
| Scan_StringTuple_Ordinal_All       | 254,612.45 ns | 7,364.027 ns | 20,770.369 ns | 252,768.36 ns | 13,319.15 | 1,266.96 | 15.1367 | 15.1367 | 15.1367 |  524318 B |          NA |
