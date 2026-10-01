using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Generators.Test;

// Operation-level revert. A throw from inside the generated Apply - not a
// Validate failure, which is rejected before anything is mutated - must leave
// the database exactly as it was before the operation, across every table the
// operation touched. The transaction reverts in reverse table order; only when
// the revert itself fails is the database poisoned (see PooledOperation).
//
// TestOnlyApplyFault is the only seam that makes this testable: Apply is
// generated code with no failure mode of its own, and user code throws before
// Apply ever runs. Reads go through statically-typed helpers compiled inside the
// fixture because QuerySet/QuerySingle are ref structs - see
// GeneratorTestHost.InvokeHelper.
public class OperationRevertTests {
    private const string Source = """
        using System;
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class RevertDb : DbContext<RevertDbTransaction> { }

        [Table(TableKind.Instant, typeof(RevertDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Account(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] int Balance,
            [Index(IndexKind.Hash, Uniqueness.NonUnique)] [property: MemoryPackOrder(2)] [property: Key(2)] int ClubId);

        [Table(TableKind.Instant, typeof(RevertDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Ledger(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] int Amount,
            [Index(IndexKind.Hash, Uniqueness.Unique)] [property: MemoryPackOrder(2)] [property: Key(2)] int AccountId);

        public static class Probe {
            public static int AccountBalance(RevertDbAccountOps a, int id) => a.Primary.Find(id).Get().Unwrap().Balance;
            public static bool AccountExists(RevertDbAccountOps a, int id) => a.Primary.Find(id).HasRow();
            public static int AccountCount(RevertDbAccountOps a) { using var q = a.Iter(); return q.Get().Unwrap().Length; }
            public static int AccountClubCount(RevertDbAccountOps a, int clubId) { using var q = a.Idx.ClubId.Find(clubId); return q.Count; }
            public static int LedgerAmount(RevertDbLedgerOps l, int id) => l.Primary.Find(id).Get().Unwrap().Amount;
            public static bool LedgerExists(RevertDbLedgerOps l, int id) => l.Primary.Find(id).HasRow();
            public static bool LedgerByAccount(RevertDbLedgerOps l, int accountId) => l.Idx.AccountId.Find(accountId).HasRow();
        }
        """;

    static private (object Db, Type TxType, System.Reflection.Assembly Asm) NewDb() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        return (Activator.CreateInstance(asm.GetType("TestNs.RevertDb")!)!, asm.GetType("TestNs.RevertDbTransaction")!, asm);
    }

    static private object NewAccount(System.Reflection.Assembly asm, int id, int balance, int clubId)
        => Activator.CreateInstance(asm.GetType("TestNs.Account")!, id, balance, clubId)!;

    static private object NewLedger(System.Reflection.Assembly asm, int id, int amount, int accountId)
        => Activator.CreateInstance(asm.GetType("TestNs.Ledger")!, id, amount, accountId)!;

    static private object T(object tx, string accessor)
        => tx.GetType().GetProperty(accessor)!.GetValue(tx)!;

    static private Result Run(object db, Type txType, Func<object, object, Result> body)
        => (Result)((Task<Result>)GeneratorTestHost.RunTransactional(db, txType, (ctx, tx) => body(tx, txType), PropagationMode.Optimistic)).Result;

    static private object? Probe(System.Reflection.Assembly asm, string method, object tx, string accessor, params object?[] args)
        => GeneratorTestHost.InvokeHelper(asm, "TestNs.Probe", method, [T(tx, accessor), .. args]);

    // Arms a fault on one table's Apply, at the given change ordinal (0-based).
    static private void ArmFault(object tx, string accessor, int throwAt) {
        var ops = T(tx, accessor);
        var prop = ops.GetType().GetProperty("TestOnlyApplyFault")!;
        // Built with an Expression rather than a lambda literal because the target
        // property is typed as System.Action<int> only inside the dynamically-compiled
        // assembly - there is no static type to write a lambda against here.
        var pi = System.Linq.Expressions.Expression.Parameter(typeof(int), "i");
        var ctor = typeof(InvalidOperationException).GetConstructor([typeof(string)])!;
        var msg = System.Linq.Expressions.Expression.Call(
            typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!,
            System.Linq.Expressions.Expression.Constant("injected fault at change "),
            System.Linq.Expressions.Expression.Convert(pi, typeof(string)));
        var body = System.Linq.Expressions.Expression.Block(
            System.Linq.Expressions.Expression.Throw(
                System.Linq.Expressions.Expression.New(ctor, msg)),
            System.Linq.Expressions.Expression.Empty());
        prop.SetValue(ops, System.Linq.Expressions.Expression
            .Lambda(System.Linq.Expressions.Expression.Convert(
                body, typeof(Action<int>)), pi)
            .Compile());
    }

    // ---- tests ----

    [Test]
    public void AFaultInTheFirstTable_RestoresBothTablesExactly() {
        var (db, txType, asm) = NewDb();

        // Baseline: one account and one ledger row exist before the failing operation.
        Run(db, txType, (tx, _) => {
            ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 100, 7));
            ((dynamic)tx).Ledger.Insert((dynamic)NewLedger(asm, 1, 50, 1));
            return Result.Ok();
        });

        object accountOps = null!, ledgerOps = null!;
        Run(db, txType, (tx, _) => {
            accountOps = T(tx, "Account");
            ledgerOps = T(tx, "Ledger");
            ((dynamic)tx).Account.Update(1, (dynamic)NewAccount(asm, 1, 999, 7));
            ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 2, 555, 7));
            ((dynamic)tx).Ledger.Update(1, (dynamic)NewLedger(asm, 1, 4242, 1));
            ((dynamic)tx).Ledger.Insert((dynamic)NewLedger(asm, 2, 77, 2));
            // Throw while applying the Account table's second change - by then its
            // update is already applied, so its journal has a real entry to restore.
            ArmFault(tx, "Account", 1);
            return Result.Ok();
        });

        // The database is still healthy, so probes can read at all.
        Run(db, txType, (tx, _) => {
            Assert.That((bool)Probe(asm, "AccountExists", tx, "Account", 1)!, Is.True, "account 1 still exists");
            Assert.That((int)Probe(asm, "AccountBalance", tx, "Account", 1)!, Is.EqualTo(100), "balance reverted");
            Assert.That((bool)Probe(asm, "AccountExists", tx, "Account", 2)!, Is.False, "inserted account removed");
            Assert.That((int)Probe(asm, "AccountCount", tx, "Account")!, Is.EqualTo(1));
            Assert.That((int)Probe(asm, "AccountClubCount", tx, "Account", 7)!, Is.EqualTo(1), "index count reverted");

            Assert.That((int)Probe(asm, "LedgerAmount", tx, "Ledger", 1)!, Is.EqualTo(50), "ledger amount reverted");
            Assert.That((bool)Probe(asm, "LedgerExists", tx, "Ledger", 2)!, Is.False, "inserted ledger row removed");
            Assert.That((bool)Probe(asm, "LedgerByAccount", tx, "Ledger", 1)!, Is.True, "ledger index reverted");
            return Result.Ok();
        });
    }

    [Test]
    public void AFaultInTheSecondTable_RevertsTheFirstTablesChangesToo() {
        var (db, txType, asm) = NewDb();

        Run(db, txType, (tx, _) => {
            ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 100, 7));
            ((dynamic)tx).Ledger.Insert((dynamic)NewLedger(asm, 1, 50, 1));
            return Result.Ok();
        });

        // Account applies cleanly and mutates; Ledger then throws. Without an
        // operation-level revert the Account change would survive - this is the
        // cross-table atomicity case a single-table undo would pass.
        var failed = Run(db, txType, (tx, _) => {
            ((dynamic)tx).Account.Update(1, (dynamic)NewAccount(asm, 1, 999, 7));
            ((dynamic)tx).Ledger.Update(1, (dynamic)NewLedger(asm, 1, 4242, 1));
            ArmFault(tx, "Ledger", 0);
            return Result.Ok();
        });

        Assert.That(failed.IsError(), Is.True);
        Assert.That(failed.GetError().Kind, Is.EqualTo(ErrorKind.ApplyFailedButRevertedSuccessfully),
            "both tables were reverted, so this is healthy-database, not ApplyFailed");

        Run(db, txType, (tx, _) => {
            Assert.That((int)Probe(asm, "AccountBalance", tx, "Account", 1)!, Is.EqualTo(100),
                "the first table's applied change must be reverted too");
            Assert.That((int)Probe(asm, "LedgerAmount", tx, "Ledger", 1)!, Is.EqualTo(50));
            Assert.That((int)Probe(asm, "AccountClubCount", tx, "Account", 7)!, Is.EqualTo(1));
            Assert.That((bool)Probe(asm, "LedgerByAccount", tx, "Ledger", 1)!, Is.True, "ledger index reverted");
            return Result.Ok();
        });
    }

    [Test]
    public void AFailedOperation_LeavesTheDatabaseUsableForTheNextOperation() {
        var (db, txType, asm) = NewDb();

        Run(db, txType, (tx, _) => {
            ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 100, 7));
            return Result.Ok();
        });

        var failed = Run(db, txType, (tx, _) => {
            ((dynamic)tx).Account.Update(1, (dynamic)NewAccount(asm, 1, 999, 7));
            ArmFault(tx, "Account", 0);
            return Result.Ok();
        });

        // Its own kind, distinct from a user-code failure (no action needed) and from
        // ApplyFailed (restart required) - and the original cause is still there
        // to log.
        Assert.That(failed.IsError(), Is.True);
        Assert.That(failed.GetError().Kind, Is.EqualTo(ErrorKind.ApplyFailedButRevertedSuccessfully));
        Assert.That(failed.GetError().ToException().InnerException, Is.Not.Null, "the original failure is carried for logging");
        Assert.That(failed.GetError().ToException().InnerException!.Message, Does.Contain("injected fault"));

        // Not poisoned: the next ordinary operation succeeds and commits.
        var next = Run(db, txType, (tx, _) => {
            ((dynamic)tx).Account.Update(1, (dynamic)NewAccount(asm, 1, 250, 7));
            return Result.Ok();
        });
        Assert.That(next.IsOk(), Is.True, "the database stays usable after a reverted failure");

        Run(db, txType, (tx, _) => {
            Assert.That((int)Probe(asm, "AccountBalance", tx, "Account", 1)!, Is.EqualTo(250));
            return Result.Ok();
        });
    }
}
