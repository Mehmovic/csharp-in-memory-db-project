using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Tools.Dev.Test;

// `rhinodb wal prune` acts on a real cold-storage directory through the real ColdStore, because
// the point of the command is that it calls the SAME prune the host's Prune mode calls rather than
// re-implementing it. So these tests stand up a genuine mdbx directory, write genuine archived
// segments into it, and assert on what the command reports and removes.
public class WalPruneCommandTests {
    static private readonly long January = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    static private readonly long February = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    static private readonly long March = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "RhinoDBDevPruneTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // Opens a real ColdStore so the archive directory is created where the command expects it.
    private void WithColdStore(Action<string> body) {
        var cold = RhinoDB.Lib.Cold.ColdStore.Open(dir).Unwrap();
        cold.Dispose();
        body(dir);
    }

    static private DecodedWalEntry StampedEntry(long lsn, long utcTicks) =>
        new(lsn, WalEntryKind.Operation, [new WalChange(1, ChangeKind.Insert, BitConverter.GetBytes(lsn), [9])], utcTicks);

    private void WriteSegments(params (long ticks, uint generation)[] segments) {
        WithColdStore(coldPath => {
            var archiveDir = Path.Combine(coldPath, WalArchive.ArchiveDirectoryName);
            for (var i = 0; i < segments.Length; i++) {
                var error = WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [StampedEntry(i + 1, segments[i].ticks)], segments[i].generation);
                Assert.That(error, Is.Null);
            }
        });
    }

    private string[] SegmentFiles() {
        var archiveDir = Path.Combine(dir, WalArchive.ArchiveDirectoryName);
        return Directory.Exists(archiveDir)
            ? [.. Directory.GetFiles(archiveDir, "*.wal").Select(f => Path.GetFileName(f)!).OrderBy(f => f, StringComparer.Ordinal)]
            : [];
    }

    static private string CaptureOut(Func<int> run, out int exitCode) {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetOut(captured);
        Console.SetError(captured);
        try { exitCode = run(); } finally { Console.SetOut(originalOut); Console.SetError(originalError); }
        return captured.ToString();
    }

    [Test]
    public void DevPrune_DryRunByDefault_ShowsTheSegmentsAndRemovesNothing() {
        WriteSegments((January, 1), (February, 2), (March, 3));

        var output = CaptureOut(() => WalTool.Run([
            "prune", "--cold-path", dir, "--older-than", "2026-02-15T00:00:00Z",
        ]), out var exitCode);

        Assert.That(exitCode, Is.EqualTo(1), "a dry run must not report success - the operator has to pass --yes.");
        Assert.That(SegmentFiles(), Has.Length.EqualTo(3), "dry run must not delete anything");
        Assert.That(output, Does.Contain("DELETE"));
        Assert.That(output, Does.Contain("keep"));
        Assert.That(output, Does.Contain("Dry run only"));
    }

    [Test]
    public void DevPrune_DryRun_ShowsTheResolvedUtcSoTheOperatorCanCheckTheCutoff() {
        WriteSegments((January, 1), (March, 2));

        var output = CaptureOut(() => WalTool.Run([
            "prune", "--cold-path", dir, "--older-than", "2026-02-15T00:00:00Z",
        ]), out _);

        Assert.That(output, Does.Contain("UTC"),
            "the resolved cutoff has to be visible - a prune cutoff read as the wrong zone deletes the wrong history.");
    }

    [Test]
    public void DevPrune_WithYes_RemovesOnlyTheSegmentsWhollyOlderThanTheCutoff() {
        WriteSegments((January, 1), (March, 2));

        var exitCode = WalTool.Run([
            "prune", "--cold-path", dir, "--older-than", "2026-02-15T00:00:00Z", "--yes",
        ]);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(SegmentFiles(), Has.Length.EqualTo(1));
    }

    [Test]
    public void DevPrune_WithYes_AdvancesTheRetentionFloorToTheOldestSurvivingGeneration() {
        // Cutoff sits between February and March, so exactly ONE segment (gen 4) goes and the
        // floor has to land on the generation of the oldest thing still on disk (gen 5).
        WriteSegments((January, 4), (March, 5), (March + 1_000_000_000L, 6));

        WalTool.Run(["prune", "--cold-path", dir, "--older-than", "2026-02-15T00:00:00Z", "--yes"]);

        var cold = RhinoDB.Lib.Cold.ColdStore.Open(dir).Unwrap();
        using (cold) {
            Assert.That(cold.ReadRetainedFromGeneration().Unwrap(), Is.EqualTo(5),
                "the floor means 'oldest generation still on disk' under BOTH policies.");
        }
    }

    [Test]
    public void DevPrune_NothingMatchesThePolicy_SaysSoAndReturnsZero() {
        WriteSegments((March, 1), (March, 2));

        var output = CaptureOut(() => WalTool.Run([
            "prune", "--cold-path", dir, "--older-than", "2026-01-15T00:00:00Z",
        ]), out var exitCode);

        Assert.That(exitCode, Is.EqualTo(0), "nothing to do is a success, not a pending confirmation.");
        Assert.That(output, Does.Contain("Nothing to prune"));
        Assert.That(SegmentFiles(), Has.Length.EqualTo(2));
    }

    [Test]
    public void DevPrune_WithoutAnyPolicy_FailsRatherThanGuessing() {
        var output = CaptureOut(() => WalTool.Run(["prune", "--cold-path", dir]), out var exitCode);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Does.Contain("exactly one of"));
    }

    [Test]
    public void DevPrune_WithBothPolicies_FailsRatherThanGuessing() {
        var output = CaptureOut(() => WalTool.Run([
            "prune", "--cold-path", dir, "--older-than", "2026-02-15T00:00:00Z", "--keep-generations", "2",
        ]), out var exitCode);

        Assert.That(exitCode, Is.EqualTo(1), "two retention policies at once is ambiguous, not a merge.");
        Assert.That(output, Does.Contain("exactly one of"));
    }

    [Test]
    public void DevPrune_WithoutAColdPath_Fails() {
        var output = CaptureOut(() => WalTool.Run(["prune", "--older-than", "2026-02-15T00:00:00Z"]), out var exitCode);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Does.Contain("--cold-path"));
    }

    [Test]
    public void DevPrune_WithAnUnparseableTimestamp_FailsBeforeTouchingTheDatabase() {
        WriteSegments((January, 1));

        var output = CaptureOut(() => WalTool.Run([
            "prune", "--cold-path", dir, "--older-than", "last tuesday", "--yes",
        ]), out var exitCode);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Does.Contain("--older-than"));
        Assert.That(SegmentFiles(), Has.Length.EqualTo(1), "a bad cutoff must never reach the delete path.");
    }

    [Test]
    public void DevPrune_KeepGenerations_KeepsTheMostRecentOnes() {
        WriteSegments((January, 1), (February, 2), (March, 3));

        var exitCode = WalTool.Run(["prune", "--cold-path", dir, "--keep-generations", "2", "--yes"]);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(SegmentFiles(), Has.Length.EqualTo(2), "keep 2 of 3 generations");
    }

    [Test]
    public void DevPrune_KeepGenerationsWithANonNumericValue_Fails() {
        var output = CaptureOut(() => WalTool.Run([
            "prune", "--cold-path", dir, "--keep-generations", "lots", "--yes",
        ]), out var exitCode);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Does.Contain("integer"));
    }
}