```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]     : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                    | Mean             | Error          | StdDev         | Median           | Ratio      | RatioSD   | Gen0      | Gen1     | Gen2     | Allocated | Alloc Ratio |
|-------------------------- |-----------------:|---------------:|---------------:|-----------------:|-----------:|----------:|----------:|---------:|---------:|----------:|------------:|
| Lookup_Int                |         21.68 ns |       0.032 ns |       0.027 ns |         21.68 ns |       1.00 |      0.00 |         - |        - |        - |         - |          NA |
| Lookup_String             |         36.13 ns |       0.386 ns |       0.342 ns |         36.12 ns |       1.67 |      0.02 |         - |        - |        - |         - |          NA |
| Lookup_Int_Random         |         66.65 ns |       1.330 ns |       1.306 ns |         66.33 ns |       3.08 |      0.06 |         - |        - |        - |         - |          NA |
| Lookup_String_Random      |        134.53 ns |       2.576 ns |       2.283 ns |        133.54 ns |       6.21 |      0.10 |         - |        - |        - |         - |          NA |
| Range_Width1              |         59.62 ns |       1.176 ns |       2.950 ns |         58.67 ns |       2.75 |      0.14 |         - |        - |        - |         - |          NA |
| Range_Width100            |         65.32 ns |       1.325 ns |       3.019 ns |         63.68 ns |       3.01 |      0.14 |         - |        - |        - |         - |          NA |
| Range_Width10000          |        817.30 ns |       1.260 ns |       1.052 ns |        817.29 ns |      37.71 |      0.06 |         - |        - |        - |         - |          NA |
| Scan_All                  |      7,687.20 ns |      51.103 ns |      47.802 ns |      7,665.96 ns |     354.65 |      2.18 |         - |        - |        - |         - |          NA |
| Scan_Except               |      7,858.01 ns |      35.560 ns |      29.694 ns |      7,844.46 ns |     362.53 |      1.39 |         - |        - |        - |         - |          NA |
| BulkLoad_1M               | 18,639,023.07 ns | 371,337.025 ns | 853,208.632 ns | 18,841,181.25 ns | 859,914.89 | 39,086.38 | 1625.0000 | 968.7500 | 312.5000 | 8517734 B |          NA |
| DeleteHalf_ThenInsertHalf |  9,103,126.86 ns |  94,853.392 ns |  93,158.714 ns |  9,072,017.97 ns | 419,974.49 |  4,200.97 |  265.6250 | 187.5000 |        - | 1719462 B |          NA |
