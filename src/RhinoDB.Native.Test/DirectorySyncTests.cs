namespace RhinoDB.Native.Test;

// Proves the parent-directory-fsync mechanism ColdStore.Open uses after
// creating a fresh persistent-database directory (Docs/02-architecture.md
// "Cold storage" - closes the same "fsync the file but not its directory
// entry" gap a LinkedIn post about SpacetimeDB flagged). On Windows this is
// a documented no-op (DirectorySync.TrySync short-circuits to success
// without calling into libc at all) - only the "doesn't crash, doesn't
// falsely report failure" half of the contract is provable in this
// environment; the real POSIX open/fsync/close path only runs on Linux.
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
}
