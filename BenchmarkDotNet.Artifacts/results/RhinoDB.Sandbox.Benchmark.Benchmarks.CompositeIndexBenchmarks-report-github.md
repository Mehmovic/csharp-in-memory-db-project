```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]     : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                             | Mean         | Error        | StdDev       | Ratio    | RatioSD | Gen0   | Gen1   | Gen2   | Allocated | Alloc Ratio |
|----------------------------------- |-------------:|-------------:|-------------:|---------:|--------:|-------:|-------:|-------:|----------:|------------:|
| Lookup_NumericTuple                |     19.43 ns |     0.144 ns |     0.135 ns |     1.00 |    0.01 |      - |      - |      - |         - |          NA |
| Lookup_StringTuple_Ordinal         |     26.27 ns |     0.546 ns |     1.307 ns |     1.35 |    0.07 |      - |      - |      - |         - |          NA |
| Lookup_StringTuple_DefaultComparer |     49.06 ns |     0.264 ns |     0.234 ns |     2.53 |    0.02 |      - |      - |      - |         - |          NA |
| Range_NumericTuple_Width100        |     78.98 ns |     0.523 ns |     0.489 ns |     4.07 |    0.04 | 0.0854 |      - |      - |     536 B |          NA |
| Range_StringTuple_Ordinal_Width100 |    112.25 ns |     1.057 ns |     0.883 ns |     5.78 |    0.06 | 0.1007 |      - |      - |     632 B |          NA |
| Range_StringTuple_Default_Width100 |    173.10 ns |     1.413 ns |     1.180 ns |     8.91 |    0.08 | 0.1006 |      - |      - |     632 B |          NA |
| Scan_NumericTuple_All              | 90,688.77 ns | 1,491.326 ns | 1,322.022 ns | 4,667.98 |   72.84 | 8.6670 | 8.6670 | 8.6670 |  524306 B |          NA |
| Scan_StringTuple_Ordinal_All       | 91,452.61 ns | 1,767.651 ns | 2,035.629 ns | 4,707.30 |  107.08 | 8.7891 | 8.7891 | 8.7891 |  524306 B |          NA |
