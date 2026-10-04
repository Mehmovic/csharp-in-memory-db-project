using System.Reflection;

using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Realtime;

namespace RhinoDB.Generators.Test;

// [OnInit]/[OnStart]/[OnClientConnect]/[OnClientDisconnect] are discovered project-wide (like
// [Table]/[ChildDatabase<TRoot,TKey>]), never required to be nested inside the [Database] class
// they target. RhinoCtx is non-generic, so which database a hook is for comes from the
// attribute form used, not from the method's own parameter type: the omitted, non-generic form
// ([OnInit]) broadcasts to every declared Root [Database] (never to a Child - same rule [Table]'s
// own omitted form follows); the generic form ([OnInit<TDb>]) pins it to one explicit database,
// Root or Child. All four are protected internal overrides on DbContext, so tests invoke them via
// reflection (BindingFlags.NonPublic), matching this harness's established "call the real generated
// method, don't string-compare the generated source" convention.
public class LifecycleHookTests {
    static private Task<Result> InvokeHook(object db, string methodName, object?[]? args = null) {
        var method = db.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task<Result>)method.Invoke(db, args)!;
    }

    [Test]
    public async Task OmittedOnStart_WithOneDeclaredDatabase_BroadcastsToIt() {
        const string source = """
            using RhinoDB.Core;
            using System.Threading.Tasks;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }

            public static class SomewhereElseEntirely {
                [OnStart]
                public static Task<Result> Start(RhinoCtx ctx) {
                    GameDb.StartWasCalled = true;
                    return Task.FromResult(Result.Ok());
                }
            }

            public partial class GameDb {
                public static bool StartWasCalled;
            }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var db = Activator.CreateInstance(asm.GetType("TestNs.GameDb")!)!;

        var result = await InvokeHook(db, "OnStartAsync");

        Assert.That(result.IsOk(), Is.True);
        Assert.That((bool)asm.GetType("TestNs.GameDb")!.GetField("StartWasCalled")!.GetValue(null)!, Is.True);
    }

    [Test]
    public async Task OmittedOnInit_WithOneDeclaredDatabase_BroadcastsToIt() {
        const string source = """
            using RhinoDB.Core;
            using System.Threading.Tasks;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }

            public static class Seed {
                [OnInit]
                public static Task<Result> Init(RhinoCtx ctx) => Task.FromResult(Result.Ok());
            }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var db = Activator.CreateInstance(asm.GetType("TestNs.GameDb")!)!;

        var result = await InvokeHook(db, "OnInitAsync");

        Assert.That(result.IsOk(), Is.True);
    }

    [Test]
    public void OmittedOnInit_WithNoDatabaseDeclared_ReportsRHINO038() {
        const string source = """
            using RhinoDB.Core;
            using System.Threading.Tasks;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            public static class Seed {
                [OnInit]
                public static Task<Result> Init(RhinoCtx ctx) => Task.FromResult(Result.Ok());
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO038"));
    }

    [Test]
    public async Task DatabaseWithNoOnStartMethod_DefaultsToAHarmlessNoOp() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var db = Activator.CreateInstance(asm.GetType("TestNs.GameDb")!)!;

        var result = await InvokeHook(db, "OnStartAsync");

        Assert.That(result.IsOk(), Is.True, "OnStart is optional - no [OnStart] method must still resolve cleanly.");
    }

    [Test]
    public void InvalidOnStartSignature_NonStaticMethod_ReportsRHINO031() {
        const string source = """
            using RhinoDB.Core;
            using System.Threading.Tasks;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }

            public class NotStatic {
                [OnStart<GameDb>]
                public Task<Result> Start(RhinoCtx ctx) => Task.FromResult(Result.Ok());
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO031"));
    }

    [Test]
    public void InvalidOnInitSignature_WrongReturnType_ReportsRHINO030() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }

            public static class Seed {
                [OnInit<GameDb>]
                public static void Init(RhinoCtx ctx) { }
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO030"));
    }

    [Test]
    public void TwoOnStartMethodsTargetingTheSameDatabase_ReportsRHINO032() {
        const string source = """
            using RhinoDB.Core;
            using System.Threading.Tasks;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }

            public static class HooksA {
                [OnStart<GameDb>]
                public static Task<Result> StartA(RhinoCtx ctx) => Task.FromResult(Result.Ok());
            }

            public static class HooksB {
                [OnStart<GameDb>]
                public static Task<Result> StartB(RhinoCtx ctx) => Task.FromResult(Result.Ok());
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO032"));
    }

    [Test]
    public async Task ExplicitOnStart_OnDifferentDatabases_EachOnlyAffectsItsOwnTarget() {
        const string source = """
            using RhinoDB.Core;
            using System.Threading.Tasks;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class FirstDb : DbContext<FirstDbTransaction> { }

            [Database]
            public partial class SecondDb : DbContext<SecondDbTransaction> { }

            public static class Hooks {
                [OnStart<FirstDb>]
                public static Task<Result> StartFirst(RhinoCtx ctx) {
                    FirstDb.Started = true;
                    return Task.FromResult(Result.Ok());
                }
            }

            public partial class FirstDb { public static bool Started; }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var firstDb = Activator.CreateInstance(asm.GetType("TestNs.FirstDb")!)!;
        var secondDb = Activator.CreateInstance(asm.GetType("TestNs.SecondDb")!)!;

        await InvokeHook(firstDb, "OnStartAsync");
        await InvokeHook(secondDb, "OnStartAsync");

        Assert.That((bool)asm.GetType("TestNs.FirstDb")!.GetField("Started")!.GetValue(null)!, Is.True);
    }

    [Test]
    public void ExplicitOnInit_TargetingAChildDatabase_ReportsRHINO039() {
        // Hooks belong to the Root only - a Child database is purely for runtime creation, detached
        // from hooks/procedures/transformers alike. [OnInit<ChildDb>] must be a compile error, not
        // silently wired, no matter how plausible the Child looks as a target.
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core;
            using System.Threading.Tasks;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class RootDb : DbContext<RootDbTransaction> { }

            [ChildDatabase<RootDb, string>]
            public partial class ChildDb : DbContext<ChildDbTransaction> { }

            [Table<ChildDb>(TableKind.Persistent)]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct SessionRow([PrimaryKey] [property: Key(0)] int Id);

            public static class Hooks {
                [OnInit<ChildDb>]
                public static Task<Result> Init(RhinoCtx ctx) => Task.FromResult(Result.Ok());
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO039"));
    }

    [Test]
    public async Task OnClientConnect_DeclaredAnywhereInTheCompilation_ReceivesTheSession() {
        const string source = """
            using RhinoDB.Core;
            using System.Threading.Tasks;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;
            using RhinoDB.Lib.Realtime;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }

            public static class ConnectionHooks {
                [OnClientConnect]
                public static Task<Result> Connect(RhinoCtx ctx) {
                    GameDb.LastConnectedPrincipal = ctx.Identity.Principal;
                    return Task.FromResult(Result.Ok());
                }
            }

            public partial class GameDb { public static PrincipalId LastConnectedPrincipal; }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var db = Activator.CreateInstance(asm.GetType("TestNs.GameDb")!)!;
        var session = new Session(ConnectionId.NewId(), new Identity(new PrincipalId("alice")));

        var result = await InvokeHook(db, "OnClientConnectAsync", [session]);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(asm.GetType("TestNs.GameDb")!.GetField("LastConnectedPrincipal")!.GetValue(null), Is.EqualTo(new PrincipalId("alice")));
    }

    [Test]
    public async Task OnClientDisconnect_DeclaredAnywhereInTheCompilation_ReceivesTheSession() {
        const string source = """
            using RhinoDB.Core;
            using System.Threading.Tasks;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;
            using RhinoDB.Lib.Realtime;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }

            public static class ConnectionHooks {
                [OnClientDisconnect]
                public static Task<Result> Disconnect(RhinoCtx ctx) {
                    GameDb.DisconnectWasCalled = true;
                    return Task.FromResult(Result.Ok());
                }
            }

            public partial class GameDb { public static bool DisconnectWasCalled; }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var db = Activator.CreateInstance(asm.GetType("TestNs.GameDb")!)!;
        var session = new Session(ConnectionId.NewId(), Identity.Anonymous);

        var result = await InvokeHook(db, "OnClientDisconnectAsync", [session]);

        Assert.That(result.IsOk(), Is.True);
        Assert.That((bool)asm.GetType("TestNs.GameDb")!.GetField("DisconnectWasCalled")!.GetValue(null)!, Is.True);
    }

    [Test]
    public async Task DatabaseWithNoClientConnectMethod_DefaultsToAHarmlessNoOp() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var db = Activator.CreateInstance(asm.GetType("TestNs.GameDb")!)!;
        var session = new Session(ConnectionId.NewId(), Identity.Anonymous);

        var result = await InvokeHook(db, "OnClientConnectAsync", [session]);

        Assert.That(result.IsOk(), Is.True, "OnClientConnect is optional - no [OnClientConnect] method must still resolve cleanly.");
    }

    [Test]
    public void InvalidOnClientConnectSignature_NonStaticMethod_ReportsRHINO033() {
        const string source = """
            using RhinoDB.Core;
            using System.Threading.Tasks;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }

            public class NotStatic {
                [OnClientConnect<GameDb>]
                public Task<Result> Connect(RhinoCtx ctx) => Task.FromResult(Result.Ok());
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO033"));
    }

    [Test]
    public void InvalidOnClientDisconnectSignature_WrongReturnType_ReportsRHINO034() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }

            public static class Hooks {
                [OnClientDisconnect<GameDb>]
                public static void Disconnect(RhinoCtx ctx) { }
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO034"));
    }

    [Test]
    public void TwoOnClientConnectMethodsTargetingTheSameDatabase_ReportsRHINO032() {
        const string source = """
            using RhinoDB.Core;
            using System.Threading.Tasks;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }

            public static class HooksA {
                [OnClientConnect<GameDb>]
                public static Task<Result> ConnectA(RhinoCtx ctx) => Task.FromResult(Result.Ok());
            }

            public static class HooksB {
                [OnClientConnect<GameDb>]
                public static Task<Result> ConnectB(RhinoCtx ctx) => Task.FromResult(Result.Ok());
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO032"));
    }
}
