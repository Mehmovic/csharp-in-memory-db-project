```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9457)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]     : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-PSIOPF : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Dry        : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

LaunchCount=1  UnrollFactor=1  

```
| Method       | Job        | IterationCount | RunStrategy | WarmupCount | RecordCount | Mean      | Error     | StdDev    |
|------------- |----------- |--------------- |------------ |------------ |------------ |----------:|----------:|----------:|
| **RunMigration** | **Job-PSIOPF** | **3**              | **Monitoring**  | **0**           | **100000**      |  **63.77 ms** |  **94.17 ms** |  **5.162 ms** |
| RunMigration | Dry        | 1              | ColdStart   | 1           | 100000      |  70.82 ms |        NA |  0.000 ms |
| **RunMigration** | **Job-PSIOPF** | **3**              | **Monitoring**  | **0**           | **1000000**     | **648.53 ms** | **631.91 ms** | **34.637 ms** |
| RunMigration | Dry        | 1              | ColdStart   | 1           | 1000000     | 729.98 ms |        NA |  0.000 ms |
