using System.Text;

namespace RhinoDB.Native.Test;

// Exercises the raw P/Invoke binding directly against the real native
// library - this is what proves (or would have disproven) the MdbxVal field
// order and native-library-resolution risks flagged during vendoring.
public class MdbxNativeSmokeTests {
    private const uint MdbxCreate = 0x40000;
    private const uint MdbxNoOverwrite = 0x10;

    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-mdbx-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private MdbxEnvironment OpenEnv() {
        var rc = MdbxEnvironment.Create(out var env);
        Assert.That(rc, Is.EqualTo(0), () => $"mdbx_env_create failed: {MdbxEnvironment.StrError(rc)}");

        Assert.That(env!.SetMaxDbs(4), Is.EqualTo(0));
        Assert.That(env.SetGeometry(-1, -1, -1, -1, -1, -1), Is.EqualTo(0));

        var openRc = env.Open(dir, 0, 0664);
        Assert.That(openRc, Is.EqualTo(0), () => $"mdbx_env_open failed: {MdbxEnvironment.StrError(openRc)}");
        return env;
    }

    [Test]
    public void EnvCreateOpenClose_RoundTrips() {
        using var env = OpenEnv();
    }

    [Test]
    public void DbiOpen_ThenPutAndGet_RoundTripsTheValue() {
        using var env = OpenEnv();

        var beginRc = env.BeginTxn(0, out var txn);
        Assert.That(beginRc, Is.EqualTo(0), () => $"mdbx_txn_begin_ex failed: {MdbxEnvironment.StrError(beginRc)}");
        using (txn) {
            Assert.That(txn!.OpenDbi("widgets", MdbxCreate, out var dbi), Is.EqualTo(0));

            var key = Encoding.UTF8.GetBytes("hello");
            var value = Encoding.UTF8.GetBytes("world");
            Assert.That(txn.Put(dbi, key, value, 0), Is.EqualTo(0));

            Assert.That(txn.Get(dbi, key, out var readBack), Is.EqualTo(0));
            Assert.That(Encoding.UTF8.GetString(readBack), Is.EqualTo("world"));

            Assert.That(txn.Commit(), Is.EqualTo(0));
        }
    }

    [Test]
    public void Delete_ThenGet_ReturnsNotFound() {
        using var env = OpenEnv();

        env.BeginTxn(0, out var txn);
        using (txn) {
            txn!.OpenDbi("widgets", MdbxCreate, out var dbi);
            var key = Encoding.UTF8.GetBytes("key-to-delete");
            txn.Put(dbi, key, Encoding.UTF8.GetBytes("value"), 0);
            txn.Commit();
        }

        env.BeginTxn(0, out txn);
        using (txn) {
            txn!.OpenDbi("widgets", MdbxCreate, out var dbi);
            var key = Encoding.UTF8.GetBytes("key-to-delete");

            Assert.That(txn.Delete(dbi, key), Is.EqualTo(0));

            var getRc = txn.Get(dbi, key, out _);
            Assert.That(getRc, Is.Not.EqualTo(0), "expected MDBX_NOTFOUND (non-zero) after delete");

            txn.Commit();
        }
    }

    [Test]
    public void NoOverwritePut_OnAnExistingKey_ReturnsKeyExist() {
        using var env = OpenEnv();

        env.BeginTxn(0, out var txn);
        using (txn) {
            txn!.OpenDbi("widgets", MdbxCreate, out var dbi);
            var key = Encoding.UTF8.GetBytes("dup-key");
            Assert.That(txn.Put(dbi, key, Encoding.UTF8.GetBytes("first"), 0), Is.EqualTo(0));

            var dupRc = txn.Put(dbi, key, Encoding.UTF8.GetBytes("second"), MdbxNoOverwrite);
            Assert.That(dupRc, Is.Not.EqualTo(0), "expected MDBX_KEYEXIST (non-zero) on NOOVERWRITE duplicate put");

            txn.Commit();
        }
    }

    [Test]
    public void AbortedTransaction_DiscardsThePut() {
        using var env = OpenEnv();

        env.BeginTxn(0, out var txn);
        using (txn) {
            txn!.OpenDbi("widgets", MdbxCreate, out var dbi);
            txn.Put(dbi, Encoding.UTF8.GetBytes("never-committed"), Encoding.UTF8.GetBytes("value"), 0);
            Assert.That(txn.Abort(), Is.EqualTo(0));
        }

        env.BeginTxn(0, out txn);
        using (txn) {
            txn!.OpenDbi("widgets", MdbxCreate, out var dbi);
            var getRc = txn.Get(dbi, Encoding.UTF8.GetBytes("never-committed"), out _);
            Assert.That(getRc, Is.Not.EqualTo(0), "the aborted put must not be visible");
            txn.Commit();
        }
    }
}
