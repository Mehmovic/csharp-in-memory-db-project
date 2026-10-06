using RhinoDB.Core;
using RhinoDB.Lib.Cold;

namespace RhinoDB.Lib.Prefs.Test;

// RhinoPrefs over a real ColdStore (real libmdbx). Cache expiry is driven by a manual clock and EvictExpired(), the same
// sweep the store's timer runs.
public class RhinoPrefsTests {
    private string dir = "";
    private ColdStore cold = null!;
    private ManualClock clock = null!;
    private RhinoPrefs prefs = null!;

    private sealed class ManualClock : TimeProvider {
        private DateTimeOffset now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private struct NotACustomType { public int X; }

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-prefs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        cold = ColdStore.Open(dir).Unwrap();
        clock = new ManualClock();
        prefs = new RhinoPrefs(cold, TimeSpan.FromMinutes(30), clock);
    }

    [TearDown]
    public void TearDown() {
        prefs.Close();
        cold.Dispose();
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private RhinoPrefs Reopen(TimeSpan? defaultCacheDuration = null) {
        prefs.Close();
        cold.Dispose();
        cold = ColdStore.Open(dir).Unwrap();
        prefs = new RhinoPrefs(cold, defaultCacheDuration ?? TimeSpan.FromMinutes(30), clock);
        return prefs;
    }

    // ---- values ----

    [Test]
    public async Task EveryValueType_RoundTrips() {
        Assert.That((await prefs.SetStringAsync("s", "héllo")).IsOk(), Is.True);
        Assert.That((await prefs.SetInt64Async("i", long.MinValue)).IsOk(), Is.True);
        Assert.That((await prefs.SetDoubleAsync("d", 3.25)).IsOk(), Is.True);
        Assert.That((await prefs.SetBoolAsync("b", true)).IsOk(), Is.True);
        Assert.That((await prefs.SetBytesAsync("x", [1, 2, 3])).IsOk(), Is.True);

        Assert.Multiple(() => {
            Assert.That(prefs.GetString("s").Unwrap(), Is.EqualTo("héllo"));
            Assert.That(prefs.GetInt64("i").Unwrap(), Is.EqualTo(long.MinValue));
            Assert.That(prefs.GetDouble("d").Unwrap(), Is.EqualTo(3.25));
            Assert.That(prefs.GetBool("b").Unwrap(), Is.True);
            Assert.That(prefs.GetBytes("x").Unwrap(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        });
    }

    [Test]
    public async Task EveryPrimitive_RoundTripsItsExtremes_AcrossAReopen() {
        var written = await prefs.Batch()
            .SetByte("u8.min", byte.MinValue).SetByte("u8.max", byte.MaxValue)
            .SetSByte("i8.min", sbyte.MinValue).SetSByte("i8.max", sbyte.MaxValue)
            .SetInt16("i16.min", short.MinValue).SetInt16("i16.max", short.MaxValue)
            .SetUInt16("u16.max", ushort.MaxValue)
            .SetInt32("i32.min", int.MinValue).SetInt32("i32.max", int.MaxValue)
            .SetUInt32("u32.max", uint.MaxValue)
            .SetInt64("i64.min", long.MinValue).SetUInt64("u64.max", ulong.MaxValue)
            .SetSingle("f32", -1.5f).SetSingle("f32.nan", float.NaN)
            .SetDouble("f64", double.Epsilon)
            .SetDecimal("dec", 79228162514264337593543950335m).SetDecimal("dec.frac", -0.0000000000000000000000000001m)
            .SetChar("char", 'ß').SetBool("bool", true)
            .CommitAsync();
        Assert.That(written.IsOk(), Is.True);

        var reopened = Reopen();

        Assert.Multiple(() => {
            Assert.That(reopened.GetByte("u8.min").Unwrap(), Is.EqualTo(byte.MinValue));
            Assert.That(reopened.GetByte("u8.max").Unwrap(), Is.EqualTo(byte.MaxValue));
            Assert.That(reopened.GetSByte("i8.min").Unwrap(), Is.EqualTo(sbyte.MinValue));
            Assert.That(reopened.GetSByte("i8.max").Unwrap(), Is.EqualTo(sbyte.MaxValue));
            Assert.That(reopened.GetInt16("i16.min").Unwrap(), Is.EqualTo(short.MinValue));
            Assert.That(reopened.GetInt16("i16.max").Unwrap(), Is.EqualTo(short.MaxValue));
            Assert.That(reopened.GetUInt16("u16.max").Unwrap(), Is.EqualTo(ushort.MaxValue));
            Assert.That(reopened.GetInt32("i32.min").Unwrap(), Is.EqualTo(int.MinValue));
            Assert.That(reopened.GetInt32("i32.max").Unwrap(), Is.EqualTo(int.MaxValue));
            Assert.That(reopened.GetUInt32("u32.max").Unwrap(), Is.EqualTo(uint.MaxValue));
            Assert.That(reopened.GetInt64("i64.min").Unwrap(), Is.EqualTo(long.MinValue));
            Assert.That(reopened.GetUInt64("u64.max").Unwrap(), Is.EqualTo(ulong.MaxValue));
            Assert.That(reopened.GetSingle("f32").Unwrap(), Is.EqualTo(-1.5f));
            Assert.That(float.IsNaN(reopened.GetSingle("f32.nan").Unwrap()), Is.True);
            Assert.That(reopened.GetDouble("f64").Unwrap(), Is.EqualTo(double.Epsilon));
            Assert.That(reopened.GetDecimal("dec").Unwrap(), Is.EqualTo(79228162514264337593543950335m));
            Assert.That(reopened.GetDecimal("dec.frac").Unwrap(), Is.EqualTo(-0.0000000000000000000000000001m));
            Assert.That(reopened.GetChar("char").Unwrap(), Is.EqualTo('ß'));
            Assert.That(reopened.GetBool("bool").Unwrap(), Is.True);
        });
    }

    [Test]
    public async Task EachSingleSetter_WritesItsType() {
        Assert.Multiple(async () => {
            Assert.That((await prefs.SetByteAsync("a", 1)).IsOk(), Is.True);
            Assert.That((await prefs.SetSByteAsync("b", -1)).IsOk(), Is.True);
            Assert.That((await prefs.SetInt16Async("c", -2)).IsOk(), Is.True);
            Assert.That((await prefs.SetUInt16Async("d", 2)).IsOk(), Is.True);
            Assert.That((await prefs.SetInt32Async("e", -3)).IsOk(), Is.True);
            Assert.That((await prefs.SetUInt32Async("f", 3)).IsOk(), Is.True);
            Assert.That((await prefs.SetUInt64Async("g", 4)).IsOk(), Is.True);
            Assert.That((await prefs.SetSingleAsync("h", 0.5f)).IsOk(), Is.True);
            Assert.That((await prefs.SetDecimalAsync("i", 1.25m)).IsOk(), Is.True);
            Assert.That((await prefs.SetCharAsync("j", 'x')).IsOk(), Is.True);
        });

        Assert.Multiple(() => {
            Assert.That(prefs.GetByte("a").Unwrap(), Is.EqualTo(1));
            Assert.That(prefs.GetSByte("b").Unwrap(), Is.EqualTo(-1));
            Assert.That(prefs.GetInt16("c").Unwrap(), Is.EqualTo(-2));
            Assert.That(prefs.GetUInt16("d").Unwrap(), Is.EqualTo(2));
            Assert.That(prefs.GetInt32("e").Unwrap(), Is.EqualTo(-3));
            Assert.That(prefs.GetUInt32("f").Unwrap(), Is.EqualTo(3));
            Assert.That(prefs.GetUInt64("g").Unwrap(), Is.EqualTo(4));
            Assert.That(prefs.GetSingle("h").Unwrap(), Is.EqualTo(0.5f));
            Assert.That(prefs.GetDecimal("i").Unwrap(), Is.EqualTo(1.25m));
            Assert.That(prefs.GetChar("j").Unwrap(), Is.EqualTo('x'));
        });
    }

    [Test]
    public async Task NumberTypes_DontConvert_AnIntIsNotALong() {
        await prefs.SetInt32Async("n", 5);

        Assert.Multiple(() => {
            Assert.That(prefs.GetInt64("n").GetError().Kind, Is.EqualTo(ErrorKind.PrefTypeMismatch));
            Assert.That(prefs.GetUInt32("n").GetError().Kind, Is.EqualTo(ErrorKind.PrefTypeMismatch));
            Assert.That(prefs.GetInt16("n").GetError().Kind, Is.EqualTo(ErrorKind.PrefTypeMismatch));
            Assert.That(prefs.GetInt32("n").Unwrap(), Is.EqualTo(5));
        });
    }

    [Test]
    public void EveryPrimitive_ReturnsTheCallersDefault_WhenMissing() {
        Assert.Multiple(() => {
            Assert.That(prefs.GetByte("x", 9).Unwrap(), Is.EqualTo(9));
            Assert.That(prefs.GetSByte("x", -9).Unwrap(), Is.EqualTo(-9));
            Assert.That(prefs.GetInt16("x", -9).Unwrap(), Is.EqualTo(-9));
            Assert.That(prefs.GetUInt16("x", 9).Unwrap(), Is.EqualTo(9));
            Assert.That(prefs.GetInt32("x", -9).Unwrap(), Is.EqualTo(-9));
            Assert.That(prefs.GetUInt32("x", 9).Unwrap(), Is.EqualTo(9));
            Assert.That(prefs.GetUInt64("x", 9).Unwrap(), Is.EqualTo(9));
            Assert.That(prefs.GetSingle("x", 9f).Unwrap(), Is.EqualTo(9f));
            Assert.That(prefs.GetDecimal("x", 9m).Unwrap(), Is.EqualTo(9m));
            Assert.That(prefs.GetChar("x", 'z').Unwrap(), Is.EqualTo('z'));
            Assert.That(prefs.GetByteList("x").Unwrap(), Is.Empty);
        });
    }

    [Test]
    public async Task AByteList_RoundTrips_AndIsInterchangeableWithByteArrays() {
        var list = new List<byte> { 4, 5, 6 };
        await prefs.SetByteListAsync("from-list", list);
        await prefs.SetBytesAsync("from-array", [7, 8]);
        list.Add(99);

        Assert.Multiple(() => {
            Assert.That(prefs.GetByteList("from-list").Unwrap(), Is.EqualTo(new byte[] { 4, 5, 6 }), "the caller's later Add doesn't reach the stored value.");
            Assert.That(prefs.GetBytes("from-list").Unwrap(), Is.EqualTo(new byte[] { 4, 5, 6 }));
            Assert.That(prefs.GetByteList("from-array").Unwrap(), Is.EqualTo(new byte[] { 7, 8 }));
        });

        var read = prefs.GetByteList("from-list").Unwrap();
        read.Clear();
        Assert.That(prefs.GetByteList("from-list").Unwrap(), Has.Count.EqualTo(3), "each read is a fresh list.");
    }

    [Test]
    public async Task AByteList_WorksInABatch() {
        Assert.That((await prefs.Batch().SetByteList("l", [1, 2]).SetUInt16("p", 7).CommitAsync()).IsOk(), Is.True);

        Assert.That(Reopen().GetByteList("l").Unwrap(), Is.EqualTo(new byte[] { 1, 2 }));
    }

    [Test]
    public void AMissingKey_ReturnsTheCallersDefault_NotAnError() {
        Assert.Multiple(() => {
            Assert.That(prefs.GetString("nope", "fallback").Unwrap(), Is.EqualTo("fallback"));
            Assert.That(prefs.GetInt64("nope", 7).Unwrap(), Is.EqualTo(7));
            Assert.That(prefs.GetDouble("nope", 1.5).Unwrap(), Is.EqualTo(1.5));
            Assert.That(prefs.GetBool("nope", true).Unwrap(), Is.True);
            Assert.That(prefs.GetBytes("nope").Unwrap(), Is.Empty);
            Assert.That(prefs.HasKey("nope").Unwrap(), Is.False);
        });
    }

    [Test]
    public async Task Values_SurviveClosingAndReopeningTheStore() {
        await prefs.SetInt64Async("season", 7);
        await prefs.SetStringAsync("motd", "welcome");

        var reopened = Reopen();

        Assert.That(reopened.GetInt64("season").Unwrap(), Is.EqualTo(7));
        Assert.That(reopened.GetString("motd").Unwrap(), Is.EqualTo("welcome"));
    }

    [Test]
    public async Task ASecondWrite_ReplacesTheValue_InTheCacheToo() {
        await prefs.SetInt64Async("n", 1);
        Assert.That(prefs.GetInt64("n").Unwrap(), Is.EqualTo(1));

        await prefs.SetInt64Async("n", 2);

        Assert.That(prefs.GetInt64("n").Unwrap(), Is.EqualTo(2));
    }

    [Test]
    public async Task ReadingAKeyAsAnotherType_IsPrefTypeMismatch() {
        await prefs.SetInt64Async("n", 1);

        Assert.That(prefs.GetString("n").GetError().Kind, Is.EqualTo(ErrorKind.PrefTypeMismatch));
        Assert.That(prefs.GetDouble("n").GetError().Kind, Is.EqualTo(ErrorKind.PrefTypeMismatch), "no silent long -> double conversion.");
    }

    [Test]
    public async Task GetBytes_ReturnsACopy() {
        byte[] original = [1, 2, 3];
        await prefs.SetBytesAsync("x", original);
        original[0] = 99;

        var read = prefs.GetBytes("x").Unwrap();
        read[1] = 99;

        Assert.That(prefs.GetBytes("x").Unwrap(), Is.EqualTo(new byte[] { 1, 2, 3 }), "neither the caller's array nor a returned one aliases the stored value.");
    }

    [Test]
    public void AStructThatIsNotACustomType_IsPrefTypeNotRegistered() {
        Assert.That(prefs.Get<NotACustomType>("k").GetError().Kind, Is.EqualTo(ErrorKind.PrefTypeNotRegistered));
        Assert.That(prefs.SetAsync("k", new NotACustomType()).Result.GetError().Kind, Is.EqualTo(ErrorKind.PrefTypeNotRegistered));
    }

    // ---- keys ----

    [TestCase("")]
    [TestCase(null)]
    public async Task AnEmptyOrNullKey_IsPrefKeyInvalid_ForEveryCall(string? key) {
        Assert.Multiple(async () => {
            Assert.That(prefs.GetString(key!).GetError().Kind, Is.EqualTo(ErrorKind.PrefKeyInvalid));
            Assert.That(prefs.HasKey(key!).GetError().Kind, Is.EqualTo(ErrorKind.PrefKeyInvalid));
            Assert.That((await prefs.SetInt64Async(key!, 1)).GetError().Kind, Is.EqualTo(ErrorKind.PrefKeyInvalid));
            Assert.That((await prefs.DeleteAsync(key!)).GetError().Kind, Is.EqualTo(ErrorKind.PrefKeyInvalid));
        });
        await Task.CompletedTask;
    }

    [Test]
    public async Task AKeyOver255Utf8Bytes_IsPrefKeyInvalid_AndExactly255IsFine() {
        Assert.That((await prefs.SetInt64Async(new string('a', 255), 1)).IsOk(), Is.True);
        Assert.That((await prefs.SetInt64Async(new string('a', 256), 1)).GetError().Kind, Is.EqualTo(ErrorKind.PrefKeyInvalid));
        Assert.That((await prefs.SetInt64Async(new string('é', 128), 1)).GetError().Kind, Is.EqualTo(ErrorKind.PrefKeyInvalid), "the limit is UTF-8 bytes, not chars.");
    }

    [Test]
    public async Task Keys_ListsEveryStoredKeyInOrdinalOrder() {
        await prefs.Batch().SetInt64("b", 1).SetInt64("a", 1).SetString("c", "x").CommitAsync();

        Assert.That(prefs.Keys().Unwrap(), Is.EqualTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public async Task Delete_RemovesOneKey_AndDeletingAMissingKeyIsFine() {
        await prefs.SetInt64Async("a", 1);
        await prefs.SetInt64Async("b", 2);

        Assert.That((await prefs.DeleteAsync("a")).IsOk(), Is.True);
        var missing = await prefs.DeleteAsync("never-existed");
        Assert.That(missing.IsOk(), Is.True, missing.IsOk() ? "" : missing.GetError().ToException().ToString());

        Assert.That(prefs.HasKey("a").Unwrap(), Is.False);
        Assert.That(prefs.GetInt64("a", -1).Unwrap(), Is.EqualTo(-1), "a deleted key isn't served from the cache either.");
        Assert.That(prefs.HasKey("b").Unwrap(), Is.True);
    }

    [Test]
    public async Task DeleteAll_EmptiesTheStore_AndTheCache() {
        await prefs.Batch().SetInt64("a", 1).SetInt64("b", 2).CommitAsync();
        prefs.GetInt64("a");

        Assert.That((await prefs.DeleteAllAsync()).IsOk(), Is.True);

        Assert.That(prefs.Keys().Unwrap(), Is.Empty);
        Assert.That(prefs.CachedCount, Is.Zero);
        Assert.That(Reopen().Keys().Unwrap(), Is.Empty, "and it's on disk.");
    }

    // ---- batches ----

    [Test]
    public async Task ABatch_WritesEverythingInOneCommit() {
        var committed = await prefs.Batch()
            .SetString("name", "Rhino")
            .SetInt64("level", 3)
            .SetBool("open", true)
            .Delete("missing")
            .CommitAsync();

        Assert.That(committed.IsOk(), Is.True);
        var reopened = Reopen();
        Assert.That(reopened.GetString("name").Unwrap(), Is.EqualTo("Rhino"));
        Assert.That(reopened.GetInt64("level").Unwrap(), Is.EqualTo(3));
        Assert.That(reopened.GetBool("open").Unwrap(), Is.True);
    }

    [Test]
    public async Task ABatchWithOneInvalidWrite_WritesNothing_AndReportsTheError() {
        var committed = await prefs.Batch()
            .SetInt64("fine", 1)
            .SetInt64("", 2)
            .CommitAsync();

        Assert.That(committed.GetError().Kind, Is.EqualTo(ErrorKind.PrefKeyInvalid));
        Assert.That(prefs.HasKey("fine").Unwrap(), Is.False, "all or nothing.");
    }

    [Test]
    public async Task ManyConcurrentWriters_AllLand() {
        var writes = Enumerable.Range(0, 64).Select(i => prefs.SetInt64Async($"k{i}", i));

        var results = await Task.WhenAll(writes);

        Assert.That(results.All(r => r.IsOk()), Is.True);
        var reopened = Reopen();
        Assert.That(Enumerable.Range(0, 64).All(i => reopened.GetInt64($"k{i}", -1).Unwrap() == i), Is.True);
    }

    // ---- cache ----

    [Test]
    public async Task AValue_LeavesTheCache_AfterGoingUnusedForTheDefaultDuration() {
        await prefs.SetInt64Async("n", 1);
        Assert.That(prefs.IsCached("n"), Is.True, "a write caches its value.");

        clock.Advance(TimeSpan.FromMinutes(29));
        prefs.EvictExpired();
        Assert.That(prefs.IsCached("n"), Is.True);

        clock.Advance(TimeSpan.FromMinutes(1));
        prefs.EvictExpired();
        Assert.That(prefs.IsCached("n"), Is.False);
        Assert.That(prefs.GetInt64("n").Unwrap(), Is.EqualTo(1), "evicted from memory only - it's read back from disk.");
        Assert.That(prefs.IsCached("n"), Is.True, "and cached again by that read.");
    }

    [Test]
    public async Task EveryUse_RestartsTheClock() {
        await prefs.SetInt64Async("n", 1);

        for (var i = 0; i < 5; i++) {
            clock.Advance(TimeSpan.FromMinutes(20));
            prefs.GetInt64("n");
            prefs.EvictExpired();
        }

        Assert.That(prefs.IsCached("n"), Is.True, "used every 20 minutes for 100 minutes - never 30 minutes unused.");
    }

    [Test]
    public async Task APerItemDuration_OverridesTheDefault() {
        await prefs.SetInt64Async("short", 1, cacheFor: TimeSpan.FromMinutes(1));
        await prefs.SetInt64Async("long", 1, cacheFor: TimeSpan.FromHours(2));

        clock.Advance(TimeSpan.FromMinutes(31));
        prefs.EvictExpired();

        Assert.That(prefs.IsCached("short"), Is.False);
        Assert.That(prefs.IsCached("long"), Is.True, "longer than the 30-minute default.");
    }

    [Test]
    public async Task ZeroDuration_IsNeverCached_AndInfinite_IsNeverEvicted() {
        await prefs.SetInt64Async("uncached", 1, cacheFor: TimeSpan.Zero);
        await prefs.SetInt64Async("pinned", 1, cacheFor: Timeout.InfiniteTimeSpan);

        Assert.That(prefs.GetInt64("uncached").Unwrap(), Is.EqualTo(1));
        Assert.That(prefs.IsCached("uncached"), Is.False, "read straight from disk every time.");

        clock.Advance(TimeSpan.FromDays(365));
        prefs.EvictExpired();
        Assert.That(prefs.IsCached("pinned"), Is.True);
    }

    [Test]
    public async Task APerItemDuration_IsStoredWithTheValue_SoItSurvivesEvictionAndRestart() {
        await prefs.SetInt64Async("short", 1, cacheFor: TimeSpan.FromMinutes(1));
        var reopened = Reopen();
        reopened.GetInt64("short");
        Assert.That(reopened.IsCached("short"), Is.True);

        clock.Advance(TimeSpan.FromMinutes(2));
        reopened.EvictExpired();

        Assert.That(reopened.IsCached("short"), Is.False, "reloaded from disk with its own 1-minute duration, not the default.");
    }

    [Test]
    public async Task AValueOnTheDefault_FollowsTheCurrentDefault_NotTheOneItWasWrittenWith() {
        await prefs.SetInt64Async("n", 1);
        var reopened = Reopen(defaultCacheDuration: TimeSpan.FromMinutes(5));
        reopened.GetInt64("n");

        clock.Advance(TimeSpan.FromMinutes(6));
        reopened.EvictExpired();

        Assert.That(reopened.IsCached("n"), Is.False, "Prefs.CacheDuration changed from 30 to 5 minutes - the setting applies.");
    }

    [Test]
    public async Task ANegativeDuration_IsPrefCacheDurationInvalid() {
        var result = await prefs.SetInt64Async("n", 1, cacheFor: TimeSpan.FromSeconds(-5));

        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.PrefCacheDurationInvalid));
        Assert.That(prefs.HasKey("n").Unwrap(), Is.False);
    }

    // ---- lifetime ----

    [Test]
    public async Task AfterTheStoreCloses_EveryCallIsPrefsClosed_NotACrash() {
        await prefs.SetInt64Async("n", 1);
        prefs.Close();

        Assert.That(prefs.GetInt64("n").GetError().Kind, Is.EqualTo(ErrorKind.PrefsClosed));
        Assert.That(prefs.Keys().GetError().Kind, Is.EqualTo(ErrorKind.PrefsClosed));
        Assert.That((await prefs.SetInt64Async("n", 2)).GetError().Kind, Is.EqualTo(ErrorKind.PrefsClosed));
    }

    [Test]
    public void TheColdStore_HandsOutOneInstance_AndClosesItOnDispose() {
        var dir2 = Path.Combine(Path.GetTempPath(), "rhinodb-prefs-tests", Guid.NewGuid().ToString("N"));
        var store = ColdStore.Open(dir2).Unwrap();
        try {
            var first = store.Prefs;
            Assert.That(store.Prefs, Is.SameAs(first));
            Assert.That(first.DefaultCacheDuration, Is.EqualTo(TimeSpan.FromMinutes(30)));

            store.Dispose();

            Assert.That(first.GetInt64("n").GetError().Kind, Is.EqualTo(ErrorKind.PrefsClosed));
        } finally {
            try { Directory.Delete(dir2, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
