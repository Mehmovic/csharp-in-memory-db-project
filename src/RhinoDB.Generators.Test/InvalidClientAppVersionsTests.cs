using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// [assembly: InvalidClientAppVersions(...)] (Part F, Phase 6) - the generator's first-ever assembly-attribute
// read. Genuinely project-scoped, not anchored to any [Table]/[Database] - RhinoClientCompat is always
// emitted, even in a compilation with neither.
public class InvalidClientAppVersionsTests {
    [Test]
    public void NoAttributeDeclared_IsAppVersionInvalidAlwaysFalse() {
        const string source = """
            namespace TestNs;
            public class Marker { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var holderType = asm.GetType("RhinoClientCompat")!;
        var method = holderType.GetMethod("IsAppVersionInvalid")!;

        Assert.That(method.Invoke(null, [PackedVersion.Pack(1, 0, 0)]), Is.False);
        Assert.That(method.Invoke(null, [0u]), Is.False);
    }

    [Test]
    public void ZeroTableOrDatabaseTypesAtAll_HolderIsStillEmitted() {
        const string source = """
            namespace TestNs;
            public class NotARhinoDbTypeAtAll { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);

        Assert.That(asm.GetType("RhinoClientCompat"), Is.Not.Null);
    }

    [Test]
    public void LessThanOrEqualToDeclared_FloorSemantics_InvalidatesThatVersionAndEverythingOlder() {
        var floor = PackedVersion.Pack(1, 5, 0);
        var source = $$"""
            [assembly: RhinoDB.Core.Tables.InvalidClientAppVersions(LessThanOrEqualTo = new uint[] { {{floor}}u })]

            namespace TestNs;
            public class Marker { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var method = asm.GetType("RhinoClientCompat")!.GetMethod("IsAppVersionInvalid")!;

        Assert.That(method.Invoke(null, [PackedVersion.Pack(1, 5, 0)]), Is.True, "exactly at the floor - invalid, must upgrade");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(1, 0, 0)]), Is.True, "older than the floor - invalid");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(1, 5, 1)]), Is.False, "newer than the floor - fine");
    }

    [Test]
    public void NotEqualToDeclared_PinSemantics_OnlyDeclaredVersionsAreAccepted() {
        var pinned = PackedVersion.Pack(2, 0, 0);
        var source = $$"""
            [assembly: RhinoDB.Core.Tables.InvalidClientAppVersions(NotEqualTo = new uint[] { {{pinned}}u })]

            namespace TestNs;
            public class Marker { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var method = asm.GetType("RhinoClientCompat")!.GetMethod("IsAppVersionInvalid")!;

        Assert.That(method.Invoke(null, [PackedVersion.Pack(2, 0, 0)]), Is.False, "the one pinned version - accepted");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(2, 0, 1)]), Is.True, "not the pinned version - rejected, even though it's newer");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(1, 9, 0)]), Is.True, "not the pinned version - rejected, even though it's older");
    }

    [Test]
    public void WhitelistedVersion_OverridesTheFloorRejection() {
        var floor = PackedVersion.Pack(2, 0, 0);
        var whitelisted = PackedVersion.Pack(1, 0, 0);
        var source = $$"""
            [assembly: RhinoDB.Core.Tables.InvalidClientAppVersions(Whitelist = new uint[] { {{whitelisted}}u }, LessThanOrEqualTo = new uint[] { {{floor}}u })]

            namespace TestNs;
            public class Marker { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var method = asm.GetType("RhinoClientCompat")!.GetMethod("IsAppVersionInvalid")!;

        Assert.That(method.Invoke(null, [whitelisted]), Is.False, "explicitly whitelisted - accepted even though it's below the floor");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(1, 5, 0)]), Is.True, "below the floor and NOT whitelisted - still rejected");
    }

    [Test]
    public void WhitelistedVersion_OverridesThePinRejection() {
        var pinned = PackedVersion.Pack(3, 0, 0);
        var whitelisted = PackedVersion.Pack(1, 0, 0);
        var source = $$"""
            [assembly: RhinoDB.Core.Tables.InvalidClientAppVersions(Whitelist = new uint[] { {{whitelisted}}u }, NotEqualTo = new uint[] { {{pinned}}u })]

            namespace TestNs;
            public class Marker { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var method = asm.GetType("RhinoClientCompat")!.GetMethod("IsAppVersionInvalid")!;

        Assert.That(method.Invoke(null, [whitelisted]), Is.False, "explicitly whitelisted - accepted even though it doesn't match the pin");
        Assert.That(method.Invoke(null, [pinned]), Is.False, "matches the pin directly - also accepted");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(9, 9, 9)]), Is.True, "neither whitelisted nor the pin - rejected");
    }

    [Test]
    public void BothModesDeclaredTogether_EitherRuleCanInvalidate() {
        var floor = PackedVersion.Pack(1, 0, 0);
        var pinned = PackedVersion.Pack(3, 0, 0);
        var source = $$"""
            [assembly: RhinoDB.Core.Tables.InvalidClientAppVersions(LessThanOrEqualTo = new uint[] { {{floor}}u }, NotEqualTo = new uint[] { {{pinned}}u })]

            namespace TestNs;
            public class Marker { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var method = asm.GetType("RhinoClientCompat")!.GetMethod("IsAppVersionInvalid")!;

        Assert.That(method.Invoke(null, [PackedVersion.Pack(0, 5, 0)]), Is.True, "below the floor");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(2, 0, 0)]), Is.True, "above the floor but not the pin");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(3, 0, 0)]), Is.False, "above the floor and matches the pin - accepted");
    }

    [Test]
    public async Task AddGeneratedClientCompat_WiresTheDeclaredRuleIntoTheBuiltHostsIsAppVersionInvalid() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-clientappversion-wiring-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            // RhinoClientCompat is internal, specifically so this compiles cleanly despite
            // RhinoDB.Sandbox.MigrationFixture.dll (already referenced by this harness, and itself a
            // TableGenerator consumer) baking in its own copy of the exact same simple name in the exact
            // same global namespace - an internal type never leaks across an assembly boundary, so the
            // fixture's copy is invisible here and this extension call resolves to only one candidate.
            const string source = """
                using MemoryPack;
                using MessagePack;
                using RhinoDB.Core.Tables;
                using RhinoDB.Lib.Execution;
                using RhinoDB.Lib.Hosting;
                using System.Threading.Tasks;

                [assembly: InvalidClientAppVersions(LessThanOrEqualTo = new uint[] { 5u })]

                namespace TestNs;

                [Database]
                public partial class RootDb : DbContext<RootDbTransaction> { }

                [Table<RootDb>(TableKind.Persistent)]
                [MemoryPackable]
                [MessagePackObject]
                public readonly partial record struct Row([PrimaryKey] [property: Key(0)] int Id);

                public static class TestHelpers {
                    public static async Task<(bool Low, bool High)> Run(string configDir) {
                        var builder = RhinoHostBuilder.Create(configDir)
                    .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(_ => { }))
                            .AddGeneratedClientCompat()
                            .AddDatabase<RootDb, RootDbTransaction>(o => o.CreateDb = cold => new RootDb(cold));
                        var hostResult = await builder.BuildAsync();
                        var host = hostResult.Unwrap();
                        var result = (host.IsAppVersionInvalid(1u), host.IsAppVersionInvalid(10u));
                        host.Dispose();
                        return result;
                    }
                }
                """;

            var (asm, _) = GeneratorTestHost.CompileAndLoad(source);

            Assert.That(asm.GetType("RhinoClientCompat")!.GetMethod("AddGeneratedClientCompat"), Is.Not.Null,
                "only emitted when RhinoDB.Lib.Hosting.RhinoHostBuilder actually resolves in this compilation.");

            var task = (Task<(bool Low, bool High)>)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "Run", dir)!;
            var (low, high) = await task;

            Assert.That(low, Is.True, "version 1 is <= the declared floor of 5 - must be invalid.");
            Assert.That(high, Is.False, "version 10 is above the floor - must be valid.");
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
