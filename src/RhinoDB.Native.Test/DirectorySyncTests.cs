namespace RhinoDB.Native.Test;

// Proves the parent-directory-fsync mechanism ColdStore.Open uses after creating a fresh
// persistent-database directory (Docs/02-architecture.md "Cold storage" - closes the same
// "fsync the file but not its directory entry" gap a LinkedIn post about a WAL-segment-
// rotating database flagged).
//
// The two platforms take different paths inside TrySync, so each half of the contract is
// asserted only where it can actually run, and the other half is explicitly Ignored rather
// than silently passing. On Windows the POSIX open/fsync/close branch is unreachable here;
// on Linux the short-circuit is. A CI matrix across both is what actually covers the file.
public class DirectorySyncTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-directorysync-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Test]
    public void TrySync_AgainstARealExistingDirectory_Succeeds() {
        var ok = DirectorySync.TrySync(dir, out var errno);

        Assert.That(ok, Is.True);
        Assert.That(errno, Is.EqualTo(0));
    }

    [Test]
    public void TrySync_AgainstADirectoryContainingFiles_StillSucceeds() {
        File.WriteAllText(Path.Combine(dir, "mdbx.dat"), "placeholder");

        var ok = DirectorySync.TrySync(dir, out var errno);

        Assert.That(ok, Is.True);
        Assert.That(errno, Is.EqualTo(0));
    }

    // ---- Windows-only: the documented no-op ----

    [Test]
    public void OnWindows_TrySyncSucceedsWithoutTouchingTheFilesystemAtAll() {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows-only: proves the short-circuit never reaches libc.");

        // A path that cannot exist. If TrySync actually opened the directory, this would fail.
        // It succeeds, which is the proof that the Windows branch returns before any I/O.
        var ok = DirectorySync.TrySync(Path.Combine(dir, "does-not-exist"), out var errno);

        Assert.Multiple(() => {
            Assert.That(ok, Is.True);
            Assert.That(errno, Is.EqualTo(0));
        });
    }

    // ---- POSIX-only: the real open/fsync/close path ----

    [Test]
    public void OnPosix_TrySyncAgainstAMissingDirectory_FailsWithANonZeroErrno() {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX-only: the Windows branch short-circuits before open().");

        var ok = DirectorySync.TrySync(Path.Combine(dir, "does-not-exist"), out var errno);

        Assert.Multiple(() => {
            Assert.That(ok, Is.False, "open() on a missing directory must fail - this is the branch only POSIX reaches.");
            Assert.That(errno, Is.Not.EqualTo(0), "a failure must report why, not just false.");
        });
    }

    [Test]
    public void OnPosix_TrySyncAgainstAFileRatherThanADirectory_ReportsFailure() {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX-only: the Windows branch short-circuits before open().");

        var filePath = Path.Combine(dir, "not-a-directory");
        File.WriteAllText(filePath, "x");

        var ok = DirectorySync.TrySync(filePath, out var errno);

        Assert.Multiple(() => {
            Assert.That(ok, Is.False, "O_RDONLY on a regular file opens but fsync on it is not a directory sync - must not report success.");
            Assert.That(errno, Is.Not.EqualTo(0));
        });
    }
}