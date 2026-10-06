using System.Reflection;

namespace RhinoDB.Generators.Test;

// RhinoPrefs.Get<T>/SetAsync<T> for [CustomType] structs: the generated codec registers itself when the assembly loads,
// and the type's hash keeps two custom types from reading each other's values.
public class PrefsCustomTypeTests {
    private const string Source = """
        using System.IO;
        using System.Threading.Tasks;
        using RhinoDB.Core;
        using RhinoDB.Core.CustomTypes;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Cold;
        using RhinoDB.Lib.Execution;
        using RhinoDB.Lib.Prefs;
        using RhinoDB.Lib.Realtime;

        namespace TestNs;

        [Database]
        public partial class GameDb : DbContext<GameDbTransaction> { }

        [CustomType]
        public readonly partial record struct Schedule(int Season, string League, long StartsAtTicks);

        [CustomType]
        public readonly partial record struct Money(long Amount, string Currency);

        public static class TestHelpers {
            static RhinoPrefs PrefsOf(ColdStore cold) => new RhinoCtx(new GameDb(cold), Identity.System).Prefs;

            public static bool IsRegistered() => CustomTypeCodec<Schedule>.IsRegistered && CustomTypeCodec<Money>.IsRegistered;

            public static async Task<string> RoundTripAcrossReopen(string dir) {
                var cold = ColdStore.Open(dir).Unwrap();
                var written = await PrefsOf(cold).SetAsync("schedule", new Schedule(3, "Premier", 42));
                cold.Dispose();
                if (written.IsError()) return "write failed: " + written.GetError().Kind;

                var reopened = ColdStore.Open(dir).Unwrap();
                try {
                    var read = PrefsOf(reopened).Get<Schedule>("schedule").Unwrap();
                    return $"{read.Season}|{read.League}|{read.StartsAtTicks}";
                } finally {
                    reopened.Dispose();
                }
            }

            public static async Task<string> ReadAsTheWrongCustomType(string dir) {
                var cold = ColdStore.Open(dir).Unwrap();
                try {
                    await PrefsOf(cold).SetAsync("price", new Money(5, "EUR"));
                    return PrefsOf(cold).Get<Schedule>("price").GetError().Kind.ToString();
                } finally {
                    cold.Dispose();
                }
            }

            public static async Task<string> ABatchWithACustomType(string dir) {
                var cold = ColdStore.Open(dir).Unwrap();
                try {
                    var committed = await PrefsOf(cold).Batch().Set("price", new Money(9, "USD")).SetInt64("count", 2).CommitAsync();
                    var price = PrefsOf(cold).Get<Money>("price").Unwrap();
                    return $"{committed.IsOk()}|{price.Amount}{price.Currency}|{PrefsOf(cold).GetInt64("count").Unwrap()}";
                } finally {
                    cold.Dispose();
                }
            }

            public static string MissingKeyGivesTheDefault(string dir) {
                var cold = ColdStore.Open(dir).Unwrap();
                try {
                    var read = PrefsOf(cold).Get("none", new Money(1, "X")).Unwrap();
                    return $"{read.Amount}{read.Currency}";
                } finally {
                    cold.Dispose();
                }
            }
        }
        """;

    private Assembly asm = null!;
    private string dir = "";

    [OneTimeSetUp]
    public void Compile() => (asm, _) = GeneratorTestHost.CompileAndLoad(Source);

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-prefs-customtype-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private object? Helper(string method, params object?[] args) => GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", method, args);

    [Test]
    public void EveryCustomType_RegistersItsCodec_WhenItsAssemblyLoads() {
        Assert.That(Helper("IsRegistered"), Is.True);
    }

    [Test]
    public async Task ACustomType_RoundTripsThroughPrefs_AcrossAReopen() {
        Assert.That(await (Task<string>)Helper("RoundTripAcrossReopen", dir)!, Is.EqualTo("3|Premier|42"));
    }

    [Test]
    public async Task ReadingAsAnotherCustomType_IsPrefTypeMismatch() {
        Assert.That(await (Task<string>)Helper("ReadAsTheWrongCustomType", dir)!, Is.EqualTo("PrefTypeMismatch"));
    }

    [Test]
    public async Task ACustomType_WorksInABatch() {
        Assert.That(await (Task<string>)Helper("ABatchWithACustomType", dir)!, Is.EqualTo("True|9USD|2"));
    }

    [Test]
    public void AMissingCustomTypeKey_ReturnsTheDefault() {
        Assert.That(Helper("MissingKeyGivesTheDefault", dir), Is.EqualTo("1X"));
    }
}
