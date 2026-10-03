```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]     : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                                 | Mean            | Error         | StdDev        | Ratio     | RatioSD | Gen0     | Gen1     | Allocated | Alloc Ratio |
|--------------------------------------- |----------------:|--------------:|--------------:|----------:|--------:|---------:|---------:|----------:|------------:|
| BTreeLookup_LowCardinality_50PerKey    |        61.03 ns |      0.196 ns |      0.184 ns |      1.00 |    0.00 |        - |        - |         - |          NA |
| BTreeLookup_WideDuplicateRun_500PerKey |        69.65 ns |      0.307 ns |      0.240 ns |      1.14 |    0.01 |        - |        - |         - |          NA |
| BTreeLookup_HighCardinality_1PerKey    |        58.90 ns |      0.116 ns |      0.091 ns |      0.97 |    0.00 |        - |        - |         - |          NA |
| BTreeLookup_String_LowCardinality      |       102.29 ns |      0.681 ns |      0.604 ns |      1.68 |    0.01 |   0.0139 |        - |      88 B |          NA |
| HashLookup_LowCardinality_50PerKey     |        92.45 ns |      0.655 ns |      0.580 ns |      1.51 |    0.01 |        - |        - |         - |          NA |
| HashLookup_WideDuplicateRun_500PerKey  |       687.53 ns |      6.419 ns |      6.004 ns |     11.27 |    0.10 |        - |        - |         - |          NA |
| HashLookup_HighCardinality_1PerKey     |        24.06 ns |      0.119 ns |      0.093 ns |      0.39 |    0.00 |        - |        - |         - |          NA |
| BTreeRange_OneKey_LowCardinality       |        56.60 ns |      0.231 ns |      0.180 ns |      0.93 |    0.00 |        - |        - |         - |          NA |
| BTreeRange_100Keys_LowCardinality      |       336.74 ns |      1.000 ns |      0.780 ns |      5.52 |    0.02 |        - |        - |         - |          NA |
| BTreeGt_LowCardinality                 |     1,943.48 ns |      7.609 ns |      7.118 ns |     31.85 |    0.15 |        - |        - |         - |          NA |
| BTreeScan_All_LowCardinality           |     3,697.65 ns |      9.747 ns |      8.139 ns |     60.59 |    0.22 |        - |        - |         - |          NA |
| BTreeScan_All_WideDuplicateRun         |     7,579.95 ns |     27.261 ns |     24.166 ns |    124.20 |    0.53 |        - |        - |         - |          NA |
| BTreeScan_Except_LowCardinality        |     3,953.84 ns |      6.021 ns |      5.028 ns |     64.79 |    0.20 |        - |        - |         - |          NA |
| HashScan_All_LowCardinality            |    86,004.13 ns |    434.582 ns |    362.896 ns |  1,409.25 |    7.04 |        - |        - |         - |          NA |
| HashScan_All_WideDuplicateRun          |   142,970.23 ns |  2,048.743 ns |  1,599.524 ns |  2,342.68 |   26.07 |        - |        - |         - |          NA |
| HashScan_Except_LowCardinality         |    87,645.97 ns |    271.477 ns |    240.658 ns |  1,436.15 |    5.65 |        - |        - |         - |          NA |
| BTreeBulkLoad_LowCardinality           |   458,838.19 ns |  3,031.875 ns |  2,531.753 ns |  7,518.44 |   45.55 |  68.3594 |  29.7852 |  431712 B |          NA |
| BTreeBulkLoad_WideDuplicateRun         | 1,020,813.12 ns | 15,144.462 ns | 14,166.139 ns | 16,726.86 |  229.95 | 136.7188 |  87.8906 |  860985 B |          NA |
| HashBulkLoad_WideDuplicateRun          | 1,110,486.88 ns |  7,808.325 ns |  6,520.305 ns | 18,196.24 |  115.71 | 884.7656 | 771.4844 | 5564713 B |          NA |
| BTreeDeleteOneOfMany_WideRun           |   978,094.45 ns |  7,248.378 ns |  5,659.057 ns | 16,026.88 |  100.49 | 136.7188 |  87.8906 |  860985 B |          NA |
| HashDeleteOneOfMany_WideRun            | 1,201,827.09 ns | 22,818.279 ns | 22,410.601 ns | 19,692.93 |  360.87 | 884.7656 | 771.4844 | 5564745 B |          NA |
