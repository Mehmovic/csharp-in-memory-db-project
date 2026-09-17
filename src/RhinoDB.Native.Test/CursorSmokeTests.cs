using System.Text;

namespace RhinoDB.Native.Test;

// Exercises the raw cursor P/Invoke binding directly against the real
// native library - GetFirst/GetNext is all a full-table forward scan needs
// (see RhinoDB.Native/Interop/MdbxCursorOp.cs), added for eager-loading a
// table's every row at startup.
public class CursorSmokeTests {
    private const uint MdbxCreate = 0x40000;
    private const int MdbxNotFound = -30798;

    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-cursor-smoke-" + Guid.NewGuid().ToString("N"));
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
    public void GetFirstThenGetNext_ScansEveryPutKeyInOrder() {
        using var env = OpenEnv();

        env.BeginTxn(0, out var txn);
        using (txn) {
            txn!.OpenDbi("widgets", MdbxCreate, out var dbi);
            txn.Put(dbi, Encoding.UTF8.GetBytes("a"), Encoding.UTF8.GetBytes("1"), 0);
            txn.Put(dbi, Encoding.UTF8.GetBytes("b"), Encoding.UTF8.GetBytes("2"), 0);
            txn.Put(dbi, Encoding.UTF8.GetBytes("c"), Encoding.UTF8.GetBytes("3"), 0);
            txn.Commit();
        }

        env.BeginTxn(0, out txn);
        using (txn) {
            txn!.OpenDbi("widgets", MdbxCreate, out var dbi);
            Assert.That(txn.OpenCursor(dbi, out var cursor), Is.EqualTo(0));
            using (cursor) {
                var keys = new List<string>();
                var values = new List<string>();

                var rc = cursor!.GetFirst(out var key, out var value);
                Assert.That(rc, Is.EqualTo(0));
                keys.Add(Encoding.UTF8.GetString(key));
                values.Add(Encoding.UTF8.GetString(value));

                while ((rc = cursor.GetNext(out key, out value)) == 0) {
                    keys.Add(Encoding.UTF8.GetString(key));
                    values.Add(Encoding.UTF8.GetString(value));
                }

                Assert.That(rc, Is.EqualTo(MdbxNotFound), "cursor must report MDBX_NOTFOUND once exhausted");
                // libmdbx's default B+tree key ordering is lexicographic byte
                // order - "a" < "b" < "c" happens to already sort that way.
                Assert.That(keys, Is.EqualTo(new[] { "a", "b", "c" }));
                Assert.That(values, Is.EqualTo(new[] { "1", "2", "3" }));
            }
            txn.Commit();
        }
    }

    [Test]
    public void GetFirst_OnAnEmptyDatabase_ReturnsNotFound() {
        using var env = OpenEnv();

        env.BeginTxn(0, out var txn);
        using (txn) {
            txn!.OpenDbi("empty", MdbxCreate, out var dbi);
            Assert.That(txn.OpenCursor(dbi, out var cursor), Is.EqualTo(0));
            using (cursor) {
                var rc = cursor!.GetFirst(out _, out _);
                Assert.That(rc, Is.EqualTo(MdbxNotFound));
            }
            txn.Commit();
        }
    }
}
