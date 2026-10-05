namespace RhinoDB.Generators.Test;

// TransactionSugarGenerator's overload-resolution edges, compile-only. The happy paths (Root/Child, with/without
// args, with/without a value) are exercised for real by MultiTransactionTests, whose fixture is written entirely
// in the short form.
public class TransactionSugarGeneratorTests {
    private const string TwoChildrenSharingAStringKey = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;
        using System.Threading.Tasks;

        namespace TestNs;

        [Database]
        public partial class RootDb : DbContext<RootDbTransaction> { }

        [ChildDatabase<RootDb, string>]
        public partial class SessionDb : DbContext<SessionDbTransaction> { }

        [ChildDatabase<RootDb, string>]
        public partial class LobbyDb : DbContext<LobbyDbTransaction> { }

        [Table<SessionDb>(TableKind.Persistent)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Match([PrimaryKey] [property: Key(0)] int Id);

        [Table<LobbyDb>(TableKind.Persistent)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Seat([PrimaryKey] [property: Key(0)] int Id);

        public static class Usage {
        """;

    [Test]
    public void TwoChildrenSharingAKeyType_ABodyThatOnlyBindsForOneOfThem_Resolves() {
        // Implicitly-typed lambdas are bound per candidate - `tx.Match` only exists on SessionDbTransaction, so the
        // LobbyDb overload simply isn't applicable and no annotation is needed.
        var source = TwoChildrenSharingAStringKey + """
                public static PlannedMultiTx Build(RhinoCtx ctx) =>
                    ctx.PlanMultiTx().Add("s1", static (db, tx) => { tx.Match.Insert(new Match(1)); return Result.Ok(); });
            }
            """;

        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoad(source));
    }

    [Test]
    public void TwoChildrenSharingAKeyType_ABodyValidForBoth_NeedsTypedLambdaParameters() {
        var ambiguous = TwoChildrenSharingAStringKey + """
                public static Task<Result> Run(RhinoCtx ctx) => ctx.BeginTx("s1", static (db, tx) => Result.Ok());
            }
            """;
        var typed = TwoChildrenSharingAStringKey + """
                public static Task<Result> Run(RhinoCtx ctx) => ctx.BeginTx("s1", static (SessionDb db, SessionDbTransaction tx) => Result.Ok());
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(ambiguous));
        Assert.That(ex!.Message, Does.Contain("CS0121"), "the known, accepted nuance: nothing in the body tells the two children apart.");
        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoad(typed));
    }

    [Test]
    public void ABodyReturningAValue_BindsToTheTxValueOverload_AndOneWithoutStillReturnsTheTransaction() {
        var source = TwoChildrenSharingAStringKey + """
                public static (TxValue<int> Value, PlannedMultiTx Chain) Build(RhinoCtx ctx) {
                    var chain = ctx.PlanMultiTx();
                    TxValue<int> value = chain.Add(static (db, tx, n) => Result.Ok(n * 2), 21);
                    PlannedMultiTx same = chain.Add(static (db, tx, n) => Result.Ok(), 21);
                    return (value, same);
                }
            }
            """;

        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoad(source));
    }

    [Test]
    public void NoDatabaseAnywhere_EmitsNoSugarType() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad("""
            namespace TestNs;
            public class Marker { }
            """);

        Assert.That(asm.GetType("RhinoDB.Lib.Execution.GeneratedTransactionSugar"), Is.Null);
    }
}
