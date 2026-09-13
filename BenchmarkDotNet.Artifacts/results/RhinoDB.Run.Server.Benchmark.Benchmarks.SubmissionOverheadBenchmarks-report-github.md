```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9445)
Unknown processor
.NET SDK 11.0.100-rc.1.26425.128
  [Host]   : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 11.0.0 (11.0.26.42628), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                    | Mean      | Error     | StdDev    | Gen0   | Allocated |
|-------------------------- |----------:|----------:|----------:|-------:|----------:|
| BareTaskCompletionSource  | 20.766 ns |  3.656 ns | 0.2004 ns | 0.0140 |      88 B |
| BareClosureOverEnumAndInt |  6.396 ns | 17.703 ns | 0.9704 ns | 0.0127 |      80 B |
