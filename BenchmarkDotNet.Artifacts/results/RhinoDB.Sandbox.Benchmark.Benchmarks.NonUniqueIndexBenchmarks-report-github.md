```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]     : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                                 | Mean             | Error          | StdDev         | Ratio     | RatioSD | Gen0     | Gen1     | Allocated | Alloc Ratio |
|--------------------------------------- |-----------------:|---------------:|---------------:|----------:|--------:|---------:|---------:|----------:|------------:|
| BTreeLookup_LowCardinality_50PerKey    |        197.72 ns |       2.454 ns |       2.175 ns |      1.00 |    0.02 |        - |        - |         - |          NA |
| BTreeLookup_WideDuplicateRun_500PerKey |      1,798.21 ns |      25.542 ns |      22.642 ns |      9.10 |    0.15 |        - |        - |         - |          NA |
| BTreeLookup_HighCardinality_1PerKey    |         52.86 ns |       1.034 ns |       1.308 ns |      0.27 |    0.01 |        - |        - |         - |          NA |
| BTreeLookup_String_LowCardinality      |        398.08 ns |       7.940 ns |      10.324 ns |      2.01 |    0.06 |   0.0138 |        - |      88 B |          NA |
| HashLookup_LowCardinality_50PerKey     |        102.97 ns |       1.621 ns |       1.437 ns |      0.52 |    0.01 |        - |        - |         - |          NA |
| HashLookup_WideDuplicateRun_500PerKey  |        726.54 ns |       8.797 ns |       7.798 ns |      3.68 |    0.05 |        - |        - |         - |          NA |
| HashLookup_HighCardinality_1PerKey     |         28.67 ns |       0.588 ns |       1.254 ns |      0.15 |    0.01 |        - |        - |         - |          NA |
| BTreeRange_OneKey_LowCardinality       |        230.83 ns |       3.396 ns |       3.010 ns |      1.17 |    0.02 |        - |        - |         - |          NA |
| BTreeRange_100Keys_LowCardinality      |     13,985.91 ns |     271.936 ns |     313.162 ns |     70.74 |    1.72 |        - |        - |         - |          NA |
| BTreeGt_LowCardinality                 |     49,590.43 ns |     829.141 ns |     775.579 ns |    250.84 |    4.65 |        - |        - |         - |          NA |
| BTreeScan_All_LowCardinality           |     90,601.17 ns |   1,057.754 ns |     989.424 ns |    458.28 |    6.88 |        - |        - |         - |          NA |
| BTreeScan_All_WideDuplicateRun         |    183,380.20 ns |   3,510.588 ns |   3,283.806 ns |    927.58 |   18.88 |        - |        - |         - |          NA |
| BTreeScan_Except_LowCardinality        |    102,639.90 ns |   1,822.619 ns |   1,615.704 ns |    519.18 |    9.64 |        - |        - |         - |          NA |
| HashScan_All_LowCardinality            |    102,359.03 ns |   1,932.312 ns |   2,300.280 ns |    517.76 |   12.64 |        - |        - |         - |          NA |
| HashScan_All_WideDuplicateRun          |    169,196.93 ns |   3,608.442 ns |  10,639.572 ns |    855.84 |   54.34 |        - |        - |         - |          NA |
| HashScan_Except_LowCardinality         |    102,414.12 ns |   1,987.791 ns |   1,952.276 ns |    518.03 |   11.06 |        - |        - |         - |          NA |
| BTreeBulkLoad_LowCardinality           |  2,146,674.39 ns |  39,845.441 ns |  39,133.551 ns | 10,858.38 |  224.29 | 132.8125 |  74.2188 |  842267 B |          NA |
| BTreeBulkLoad_WideDuplicateRun         |  7,735,020.98 ns |  81,224.825 ns |  72,003.691 ns | 39,125.54 |  545.85 | 203.1250 | 156.2500 | 1307035 B |          NA |
| HashBulkLoad_WideDuplicateRun          |  1,654,253.61 ns |  41,190.909 ns | 118,845.199 ns |  8,367.60 |  604.86 | 884.7656 | 771.4844 | 5564712 B |          NA |
| BTreeDeleteOneOfMany_WideRun           | 10,200,574.06 ns | 101,660.202 ns |  95,093.016 ns | 51,596.88 |  720.92 | 203.1250 | 156.2500 | 1307075 B |          NA |
| HashDeleteOneOfMany_WideRun            |  1,773,099.07 ns |  59,394.627 ns | 174,194.173 ns |  8,968.75 |  882.20 | 882.8125 | 769.5313 | 5564744 B |          NA |
