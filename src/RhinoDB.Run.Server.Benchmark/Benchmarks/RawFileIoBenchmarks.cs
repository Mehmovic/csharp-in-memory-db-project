using BenchmarkDotNet.Attributes;

namespace RhinoDB.Run.Server.Benchmark.Benchmarks;

[MemoryDiagnoser]
public class RawFileIoBenchmarks {
    private const int RecordSize = 64;
    private const long PageStride = 4096;
    private const long PreallocatedSize = 1L * 1024 * 1024 * 1024;

    private string filePath = null!;
    private FileStream fileStream = null!;
    private byte[] buffer = new byte[RecordSize];
    private long freshOffset;

    [GlobalSetup]
    public void Setup() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-bench");
        Directory.CreateDirectory(dir);
        filePath = Path.Combine(dir, $"rawio-{Guid.NewGuid():N}.dat");
        fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.None);
        fileStream.SetLength(PreallocatedSize);

        fileStream.Seek(0, SeekOrigin.Begin);
        fileStream.Write(buffer, 0, buffer.Length);
        fileStream.Flush(flushToDisk: true);

        freshOffset = PageStride * 1000;
    }

    [GlobalCleanup]
    public void Cleanup() {
        fileStream.Dispose();
        File.Delete(filePath);
    }

    [Benchmark]
    public void AppendFreshPageWriteFsync() {
        fileStream.Seek(freshOffset, SeekOrigin.Begin);
        fileStream.Write(buffer, 0, buffer.Length);
        fileStream.Flush(flushToDisk: true);
        freshOffset += PageStride;
    }

    [Benchmark]
    public void RewriteSettledPageWriteFsync() {
        fileStream.Seek(0, SeekOrigin.Begin);
        fileStream.Write(buffer, 0, buffer.Length);
        fileStream.Flush(flushToDisk: true);
    }
}
