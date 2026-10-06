using System.Text.Json;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// [ChildDatabase<TRoot,TKey>]'s registration half: ChildDatabaseGenerator auto-generates
// AddGeneratedChildDatabases(this RhinoHostBuilder) so nobody calls AddChildDatabase by hand.
// TableGenerator's own share of this attribute (the {ChildDb}Transaction/Ops codegen) is covered
// separately by ChildDatabaseEmitTests.cs. Since the Root/Child types here only exist inside the
// dynamically-compiled fixture assembly, the actual end-to-end activation runs through a static
// helper embedded in that same source (matching this harness's established InvokeHelper pattern),
// rather than trying to reflect generic RhinoHostBuilder calls from the outer, statically-compiled
// test project.
public class ChildDatabaseGeneratorTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-childdatabasegenerator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(
            new RhinoDbConfig { Host = new HostConfig { ColdPath = dir, HttpPort = 0 } },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, GeneratorConfigLoader.ConfigFileName), json);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Test]
    public async Task ValidChildDatabase_IsActivatableThroughTheGeneratedRegistration() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;
            using RhinoDB.Lib.Hosting;
            using System.Threading.Tasks;

            namespace TestNs;

            [Database]
            public partial class RootDb : DbContext<RootDbTransaction> { }

            [ChildDatabase<RootDb, string>]
            public partial class ChildDb : DbContext<ChildDbTransaction> { }

            // Both need at least one Persistent table to get a ColdStore-taking generated constructor -
            // ChildDatabaseOptions.CreateDb is always Func<ColdStore, TChildDb>, since
            // ChildDatabaseRegistry always opens a real ColdStore for every activated child.
            [Table<RootDb>(TableKind.Persistent)]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct RootRow([PrimaryKey] [property: Key(0)] int Id);

            [Table<ChildDb>(TableKind.Persistent)]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct SessionRow([PrimaryKey] [property: Key(0)] int Id);

            public static class TestHelpers {
                public static async Task<bool> Run(string configDir) {
                    var builder = RhinoHostBuilder.Create(configDir)
                    .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(_ => { }))
                        .AddGeneratedChildDatabases()
                        .AddDatabase<RootDb, RootDbTransaction>(o => o.CreateDb = cold => new RootDb(cold));
                    var hostResult = await builder.BuildAsync();
                    if (hostResult.IsError()) return false;
                    var host = hostResult.Unwrap();

                    var childResult = await host.GetOrActivateChildAsync<ChildDb, ChildDbTransaction, string>("session-1");
                    // Cold is internal to RhinoDB.Lib, not visible from this dynamically-compiled
                    // assembly (no InternalsVisibleTo grant for it) - leaving ColdStore handles open
                    // here is fine, the outer test's TearDown deletes the whole temp dir best-effort.
                    host.Dispose();
                    return childResult.IsOk();
                }
            }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);

        Assert.That(asm.GetType("RhinoDB.Lib.Hosting.GeneratedChildDatabases"), Is.Not.Null);

        var task = (Task<bool>)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "Run", dir)!;
        Assert.That(await task, Is.True);
    }

    [Test]
    public void NoChildDatabasesAnywhere_EmitsNoGeneratedRegistrationType() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class RootDb : DbContext<RootDbTransaction> { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);

        Assert.That(asm.GetType("RhinoDB.Lib.Hosting.GeneratedChildDatabases"), Is.Null);
    }

    [Test]
    public void ChildDatabase_WhoseRootIsNotDeclared_ReportsRHINO037() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            public class NotARealRoot { }

            [ChildDatabase<NotARealRoot, string>]
            public partial class ChildDb : DbContext<ChildDbTransaction> { }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO037"));
    }
}
