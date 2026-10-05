using System.Reflection;

namespace RhinoDB.Generators.Test;

// RevisionAtGeneration(int) on a persistent table's Ops class (Phase 4, blocking step 20) - the runtime
// migration engine's answer to "what revision is this table's ON-DISK data actually in?" given nothing but
// G_db, per the RevisionHistoryEntry mechanism added to Descriptor.json/MigrationCreateCommand this session.
public class RevisionAtGenerationTests {
    private const string DescriptorWithRevisionHistory = """
                                                         {
                                                           "Databases": [ { "FullName": "global::TestNs.VaultDb", "Generation": 3, "InvalidGenerations": [], "RetainedFromGeneration": 0 } ],
                                                           "TypeRevisions": { "global::TestNs.Account": 2 },
                                                           "CustomTypes": {},
                                                           "Tables": [
                                                             {
                                                               "DatabaseFullName": "global::TestNs.VaultDb",
                                                               "Accessor": "Account",
                                                               "RowTypeFullName": "global::TestNs.Account",
                                                               "Kind": "Persistent",
                                                               "NameHash": 0,
                                                               "Revision": 2,
                                                               "PrimaryKey": { "Path": "Id", "TypeFullName": "int", "Kind": "Unmanaged" },
                                                               "Fields": [ { "Path": "Id", "TypeFullName": "int", "Kind": "Unmanaged" } ],
                                                               "Indexes": [],
                                                               "RevisionHistory": [ { "Generation": 1, "Revision": 1 }, { "Generation": 3, "Revision": 2 } ]
                                                             }
                                                           ]
                                                         }
                                                         """;

    private const string Source = """
                                  using MemoryPack;
                                  using MessagePack;
                                  using RhinoDB.Core.Tables;
                                  using RhinoDB.Lib.Execution;

                                  namespace TestNs;

                                  [Database]
                                  public partial class VaultDb : DbContext<VaultDbTransaction> { }

                                  [Table<VaultDb>(TableKind.Persistent)]
                                  [MemoryPackable]
                                  [MessagePackObject]
                                  public readonly partial record struct Account([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);
                                  """;

    static private int InvokeRevisionAtGeneration(System.Reflection.Assembly asm, int generation) {
        var opsType = asm.GetType("TestNs.VaultDbAccountOps")!;
        var method = opsType.GetMethod("RevisionAtGeneration", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return (int)method.Invoke(null, [generation])!;
    }

    [Test]
    public void GenerationBeforeAnyRecordedHop_ReturnsRevisionZero() {
        var (asm, _) = GeneratorTestHost.CompileAndLoadWithDescriptor(Source, DescriptorWithRevisionHistory);
        Assert.That(InvokeRevisionAtGeneration(asm, 0), Is.EqualTo(0));
    }

    [Test]
    public void GenerationExactlyAtAHop_ReturnsThatHopsRevision() {
        var (asm, _) = GeneratorTestHost.CompileAndLoadWithDescriptor(Source, DescriptorWithRevisionHistory);
        Assert.That(InvokeRevisionAtGeneration(asm, 1), Is.EqualTo(1));
        Assert.That(InvokeRevisionAtGeneration(asm, 3), Is.EqualTo(2));
    }

    [Test]
    public void GenerationBetweenTwoHops_ReturnsTheEarlierHopsRevision() {
        var (asm, _) = GeneratorTestHost.CompileAndLoadWithDescriptor(Source, DescriptorWithRevisionHistory);
        Assert.That(InvokeRevisionAtGeneration(asm, 2), Is.EqualTo(1));
    }

    [Test]
    public void GenerationPastTheLatestHop_ReturnsTheLatestHopsRevision() {
        var (asm, _) = GeneratorTestHost.CompileAndLoadWithDescriptor(Source, DescriptorWithRevisionHistory);
        Assert.That(InvokeRevisionAtGeneration(asm, 99), Is.EqualTo(2));
    }

    [Test]
    public void NoDescriptorProvided_AlwaysReturnsRevisionZero() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        Assert.That(InvokeRevisionAtGeneration(asm, 5), Is.EqualTo(0));
    }
}
