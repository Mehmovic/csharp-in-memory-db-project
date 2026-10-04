using System.Reflection;

namespace RhinoDB.Generators.Test;

// [ClientDowngrade(ToRevision=N)] discovery + CanDowngradeTo/DowngradeToRevisionBytes (Part F, Phase 4) -
// the reverse direction of [Migration], and deliberately allowed to be incomplete (not every transform is
// invertible) - no RHINO020-style gap diagnostic exists for this chain, unlike [Migration]'s.
public class DowngradeMethodTests {
    private const string TwoHopSource = """
                                        using RhinoDB.Core.Tables;
                                        using RhinoDB.Lib.Execution;

                                        namespace TestNs;

                                        [Database]
                                        public partial class VaultDb : DbContext<VaultDbTransaction> { }

                                        [FrozenSchema(0)]
                                        public readonly record struct AccountV0([PrimaryKey] int Id);

                                        [FrozenSchema(1)]
                                        public readonly record struct AccountV1([PrimaryKey] int Id, decimal Balance);

                                        [Table<VaultDb>(TableKind.Persistent)]
                                        public readonly partial record struct Account([PrimaryKey] int Id, decimal Balance, string Tier) {
                                            [Migration(0)]
                                            internal static AccountV1 UpgradeFromV0(AccountV0 old) => new AccountV1(old.Id, 0m);
                                            [Migration(1)]
                                            internal static Account UpgradeFromV1(AccountV1 old) => new Account(old.Id, old.Balance, "Bronze");

                                            [ClientDowngrade(1)]
                                            internal static AccountV1 DowngradeToV1(Account current) => new AccountV1(current.Id, current.Balance);
                                            [ClientDowngrade(0)]
                                            internal static AccountV0 DowngradeToV0(AccountV1 v1) => new AccountV0(v1.Id);
                                        }
                                        """;

    static private object InvokeStatic(Assembly asm, string typeName, string methodName, params object?[] args) {
        var type = asm.GetType(typeName)!;
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return method.Invoke(null, args)!;
    }

    [Test]
    public void TwoRegisteredDowngradeHops_CanDowngradeToEveryReachableRevision() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(TwoHopSource);

        Assert.That(InvokeStatic(asm, "TestNs.VaultDbAccountOps", "CanDowngradeTo", 2), Is.EqualTo(true), "at the tip - trivially satisfiable, no downgrade needed");
        Assert.That(InvokeStatic(asm, "TestNs.VaultDbAccountOps", "CanDowngradeTo", 1), Is.EqualTo(true));
        Assert.That(InvokeStatic(asm, "TestNs.VaultDbAccountOps", "CanDowngradeTo", 0), Is.EqualTo(true));
        Assert.That(InvokeStatic(asm, "TestNs.VaultDbAccountOps", "CanDowngradeTo", -1), Is.EqualTo(false), "no hop registered this far back");
    }

    [Test]
    public void DowngradeToRevisionBytes_OneHopBack_ComposesTheSingleHopAndEncodesAsThatRevision() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(TwoHopSource);
        var current = Activator.CreateInstance(asm.GetType("TestNs.Account")!, 7, 42m, "Gold")!;

        var bytes = (byte[])InvokeStatic(asm, "TestNs.VaultDbAccountOps", "DowngradeToRevisionBytes", 1, current);
        var decoded = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV1FrozenSchemaOps", "DeserializeRow", bytes)!;

        Assert.That(decoded.GetType().GetProperty("Id")!.GetValue(decoded), Is.EqualTo(7));
        Assert.That(decoded.GetType().GetProperty("Balance")!.GetValue(decoded), Is.EqualTo(42m));
    }

    [Test]
    public void DowngradeToRevisionBytes_TwoHopsBack_ComposesBothHopsAndEncodesAsThatRevision() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(TwoHopSource);
        var current = Activator.CreateInstance(asm.GetType("TestNs.Account")!, 7, 42m, "Gold")!;

        var bytes = (byte[])InvokeStatic(asm, "TestNs.VaultDbAccountOps", "DowngradeToRevisionBytes", 0, current);
        var decoded = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV0FrozenSchemaOps", "DeserializeRow", bytes)!;

        Assert.That(decoded.GetType().GetProperty("Id")!.GetValue(decoded), Is.EqualTo(7));
    }

    [Test]
    public void DowngradeToRevisionBytes_AtTheTip_JustEncodesCurrentDirectly() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(TwoHopSource);
        var current = Activator.CreateInstance(asm.GetType("TestNs.Account")!, 7, 42m, "Gold")!;

        var bytes = (byte[])InvokeStatic(asm, "TestNs.VaultDbAccountOps", "DowngradeToRevisionBytes", 2, current);
        var expectedBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.VaultDbAccountOps", "SerializeRow", current)!;

        Assert.That(bytes, Is.EqualTo(expectedBytes));
    }

    [Test]
    public void OnlyOneOfTwoPossibleHopsRegistered_TheGapMakesEverythingBeyondItUnreachable_NoDiagnosticEither() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [FrozenSchema(0)]
            public readonly record struct AccountV0([PrimaryKey] int Id);
            [FrozenSchema(1)]
            public readonly record struct AccountV1([PrimaryKey] int Id, decimal Balance);

            [Table<VaultDb>(TableKind.Persistent)]
            public readonly partial record struct Account([PrimaryKey] int Id, decimal Balance, string Tier) {
                [Migration(0)]
                internal static AccountV1 UpgradeFromV0(AccountV0 old) => new AccountV1(old.Id, 0m);
                [Migration(1)]
                internal static Account UpgradeFromV1(AccountV1 old) => new Account(old.Id, old.Balance, "Bronze");

                [ClientDowngrade(1)]
                internal static AccountV1 DowngradeToV1(Account current) => new AccountV1(current.Id, current.Balance);
            }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);

        Assert.That(InvokeStatic(asm, "TestNs.VaultDbAccountOps", "CanDowngradeTo", 1), Is.EqualTo(true));
        Assert.That(InvokeStatic(asm, "TestNs.VaultDbAccountOps", "CanDowngradeTo", 0), Is.EqualTo(false), "no hop registered for revision 0, so it's unreachable even though the type exists");
    }

    [Test]
    public void DowngradeMethodIsInstance_ReportsRHINO026() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [FrozenSchema(0)]
            public readonly record struct AccountV0([PrimaryKey] int Id);

            [Table<VaultDb>(TableKind.Persistent)]
            public readonly partial record struct Account([PrimaryKey] int Id) {
                [ClientDowngrade(0)]
                internal AccountV0 DowngradeToV0(Account current) => new AccountV0(current.Id);
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO026"));
    }

    [Test]
    public void DowngradeMethodIsPrivate_ReportsRHINO026() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [FrozenSchema(0)]
            public readonly record struct AccountV0([PrimaryKey] int Id);

            [Table<VaultDb>(TableKind.Persistent)]
            public readonly partial record struct Account([PrimaryKey] int Id) {
                [ClientDowngrade(0)]
                static AccountV0 DowngradeToV0(Account current) => new AccountV0(current.Id);
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO026"));
    }

    [Test]
    public void DowngradeMethodHasWrongParameterCount_ReportsRHINO026() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [FrozenSchema(0)]
            public readonly record struct AccountV0([PrimaryKey] int Id);

            [Table<VaultDb>(TableKind.Persistent)]
            public readonly partial record struct Account([PrimaryKey] int Id) {
                [ClientDowngrade(0)]
                internal static AccountV0 DowngradeToV0() => new AccountV0(0);
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO026"));
    }

    [Test]
    public void DowngradeMethodReturnsVoid_ReportsRHINO026() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [Table<VaultDb>(TableKind.Persistent)]
            public readonly partial record struct Account([PrimaryKey] int Id) {
                [ClientDowngrade(0)]
                internal static void DowngradeToV0(Account current) { }
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO026"));
    }
}
