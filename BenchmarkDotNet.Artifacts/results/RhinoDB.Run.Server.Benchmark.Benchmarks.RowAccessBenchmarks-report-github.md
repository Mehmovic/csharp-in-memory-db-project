```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]     : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                             | RecordCount | Mean             | Error           | StdDev          | Median           | Allocated |
|----------------------------------- |------------ |-----------------:|----------------:|----------------:|-----------------:|----------:|
| **StorageGet_Value_Narrow**            | **10000**       |         **1.449 ns** |       **0.0288 ns** |       **0.0241 ns** |         **1.450 ns** |         **-** |
| StorageGetRef_ReadOneField_Narrow  | 10000       |         1.585 ns |       0.0252 ns |       0.0224 ns |         1.593 ns |         - |
| StorageGetRef_ReadAllFields_Narrow | 10000       |         1.745 ns |       0.0288 ns |       0.0241 ns |         1.731 ns |         - |
| StorageGet_Value_Wide              | 10000       |         1.924 ns |       0.0438 ns |       0.0366 ns |         1.922 ns |         - |
| StorageGetRef_ReadOneField_Wide    | 10000       |         1.524 ns |       0.0384 ns |       0.0300 ns |         1.520 ns |         - |
| StorageGetRef_ReadAllFields_Wide   | 10000       |         7.592 ns |       0.1736 ns |       0.3506 ns |         7.469 ns |         - |
| FilterProject_Value_Wide           | 10000       |    55,346.524 ns |     821.3217 ns |     685.8409 ns |    55,223.386 ns |         - |
| FilterProject_Ref_Wide             | 10000       |    22,960.115 ns |     388.8374 ns |     670.7254 ns |    22,815.018 ns |         - |
| PassByValue_Sum_Wide               | 10000       |        12.388 ns |       0.0807 ns |       0.0755 ns |        12.414 ns |         - |
| PassByIn_Sum_Wide                  | 10000       |        14.075 ns |       0.2044 ns |       0.1596 ns |        14.061 ns |         - |
| GeneratedOps_Get_Narrow            | 10000       |     3,517.591 ns |      69.1188 ns |     133.1685 ns |     3,478.573 ns |      32 B |
| GeneratedOps_Get_Wide              | 10000       |     3,561.186 ns |      70.5736 ns |      96.6019 ns |     3,521.582 ns |      32 B |
| **StorageGet_Value_Narrow**            | **1000000**     |         **1.542 ns** |       **0.0543 ns** |       **0.1007 ns** |         **1.517 ns** |         **-** |
| StorageGetRef_ReadOneField_Narrow  | 1000000     |         1.528 ns |       0.0163 ns |       0.0144 ns |         1.526 ns |         - |
| StorageGetRef_ReadAllFields_Narrow | 1000000     |         1.650 ns |       0.0495 ns |       0.0755 ns |         1.631 ns |         - |
| StorageGet_Value_Wide              | 1000000     |         1.825 ns |       0.0559 ns |       0.0496 ns |         1.833 ns |         - |
| StorageGetRef_ReadOneField_Wide    | 1000000     |         1.183 ns |       0.0408 ns |       0.0516 ns |         1.164 ns |         - |
| StorageGetRef_ReadAllFields_Wide   | 1000000     |         7.919 ns |       0.1822 ns |       0.4000 ns |         7.892 ns |         - |
| FilterProject_Value_Wide           | 1000000     | 8,427,570.089 ns |  83,680.3227 ns |  74,180.4265 ns | 8,401,045.312 ns |         - |
| FilterProject_Ref_Wide             | 1000000     | 7,891,771.875 ns | 155,959.0228 ns | 223,671.7888 ns | 7,873,106.250 ns |         - |
| PassByValue_Sum_Wide               | 1000000     |        12.474 ns |       0.0933 ns |       0.0779 ns |        12.457 ns |         - |
| PassByIn_Sum_Wide                  | 1000000     |        13.512 ns |       0.4572 ns |       1.2969 ns |        12.946 ns |         - |
| GeneratedOps_Get_Narrow            | 1000000     |     3,390.792 ns |      65.7709 ns |      73.1042 ns |     3,370.749 ns |      32 B |
| GeneratedOps_Get_Wide              | 1000000     |     3,532.090 ns |      43.8437 ns |      53.8440 ns |     3,551.048 ns |      32 B |
