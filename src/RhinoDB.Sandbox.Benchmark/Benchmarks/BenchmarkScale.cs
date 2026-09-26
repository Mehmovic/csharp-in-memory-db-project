namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

static internal class BenchmarkScale {
    private const string IncludeBillionEnvVar = "RHINODB_BENCH_INCLUDE_BILLION";

    static public IEnumerable<int> RecordCounts() {
        yield return 100;
        yield return 10_000;
        yield return 1_000_000;
        
        if (Environment.GetEnvironmentVariable(IncludeBillionEnvVar) == "1") {
            yield return 100_000_000;
            yield return 1_000_000_000;
        }
    }
}
