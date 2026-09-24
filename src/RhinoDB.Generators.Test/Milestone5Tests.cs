using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

public class Milestone5Tests {
    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class ShopDb : DbContext<ShopDbTransaction> { }

        [Table(TableKind.Instant, typeof(ShopDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Club(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Name,
            [property: MemoryPackOrder(2)] [property: Key(2)] decimal Balance) {
            [Validate]
            internal static DbError? ValidateBalance(Club row) => row.Balance < 0 ? DbError.Custom(1) : null;

            [Validate]
            internal static DbError? ValidateName(Club row) => string.IsNullOrWhiteSpace(row.Name) ? DbError.Custom(2) : null;
        }

        // QuerySet/QuerySingle are ref structs and can never cross a dynamic call
        // boundary (see GeneratorTestHost.InvokeHelper) - typed helper instead.
        public static class TestHelpers {
            public static bool ClubFindIsOk(ShopDbClubOps club, int id) => club.Primary.Find(id).HasRow();
        }
        """;

    static private (object Db, Type TxType, System.Reflection.Assembly Assembly) NewDb() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.ShopDb")!;
        var txType = asm.GetType("TestNs.ShopDbTransaction")!;
        var db = Activator.CreateInstance(dbType)!;
        return (db, txType, asm);
    }

    static private object NewClub(System.Reflection.Assembly assembly, int id, string name, decimal balance) {
        var t = assembly.GetType("TestNs.Club")!;
        return Activator.CreateInstance(t, id, name, balance)!;
    }

    [Test]
    public async Task Insert_PassingCustomValidation_Succeeds() {
        var (db, txType, asm) = NewDb();

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, "Arsenal", 100m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
    }

    [Test]
    public async Task Insert_FailingCustomValidation_ReturnsTheCustomError() {
        var (db, txType, asm) = NewDb();

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, "Arsenal", -50m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.Custom));
        Assert.That(result.GetError().CustomCode, Is.EqualTo((ushort)1));
    }

    [Test]
    public async Task Insert_FailingCustomValidation_LeavesTheRowUnapplied() {
        // A Validate() failure - custom or structural - must behave identically:
        // nothing was staged actually lands, exactly like a DuplicateKey failure.
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, "Arsenal", -50m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        var found = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { found = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ClubFindIsOk", (object)((dynamic)tx).Club, 1)!; return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.False);
    }

    [Test]
    public async Task Update_FailingCustomValidation_ReturnsTheCustomError() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, "Arsenal", 100m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Club.Update(1, (dynamic)NewClub(asm, 1, "Arsenal", -1m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.Custom));
        Assert.That(result.GetError().CustomCode, Is.EqualTo((ushort)1));
    }

    [Test]
    public async Task Insert_FailingASecondIndependentValidateMethod_ReturnsThatMethodsError() {
        // Proves every [Validate] method on a row is actually wired in, not
        // just the first one found - the balance check passes here, only the
        // name check fails.
        var (db, txType, asm) = NewDb();

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, "  ", 100m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().CustomCode, Is.EqualTo((ushort)2));
    }

    [Test]
    public void InvalidValidateMethodSignature_ReportsRHINO009() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class BadDb : DbContext<BadDbTransaction> { }

            [Table(TableKind.Instant, typeof(BadDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Widget([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id) {
                [Validate]
                DbError? ValidateSomething() => null;
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO009"));
    }

    [Test]
    public void PrivateValidateMethod_ReportsRHINO009() {
        // Otherwise-correct signature, but private - generated code (a
        // sibling class) can never call it, so this must be a diagnostic,
        // not a confusing CS0122 surfacing out of generated code.
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class PrivateValidateDb : DbContext<PrivateValidateDbTransaction> { }

            [Table(TableKind.Instant, typeof(PrivateValidateDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Widget([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id) {
                [Validate]
                static DbError? ValidateSomething(Widget row) => null;
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO009"));
    }
}
