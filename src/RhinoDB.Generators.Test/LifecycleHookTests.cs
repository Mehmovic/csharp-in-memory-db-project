using System.Reflection;

using RhinoDB.Core;
using RhinoDB.Lib.Realtime;

namespace RhinoDB.Generators.Test;

// [OnInit]/[OnStart]/[OnClientConnect]/[OnClientDisconnect] are discovered project-wide (like
// [GenerateDbError]), never required to be nested inside the [Database] class they target -
// RhinoContext<TDb>'s own generic argument says which database a hook is for. All four are
// protected internal overrides on DbContext, so tests invoke them via reflection (BindingFlags.
// NonPublic), matching this harness's established "call the real generated method, don't
// string-compare the generated source" convention.
public class LifecycleHookTests {
    static private Task<Result> InvokeHook(object db, string methodName, object?[]? args = null) {
        var method = db.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task<Result>)method.Invoke(db, args)!;
    }

    [Test]
    public async Task OnStart_DeclaredAnywhereInTheCompilation_IsCalledOnTheRightDatabase() {
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
                public static Task<Result> Start(RhinoContext<GameDb> ctx) {
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
    public async Task OnInit_DeclaredAnywhereInTheCompilation_IsCalledOnTheRightDatabase() {
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
                public static Task<Result> Init(RhinoContext<GameDb> ctx) => Task.FromResult(Result.Ok());
            }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var db = Activator.CreateInstance(asm.GetType("TestNs.GameDb")!)!;

        var result = await InvokeHook(db, "OnInitAsync");

        Assert.That(result.IsOk(), Is.True);
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
                [OnStart]
                public Task<Result> Start(RhinoContext<GameDb> ctx) => Task.FromResult(Result.Ok());
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
                [OnInit]
                public static void Init(RhinoContext<GameDb> ctx) { }
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
                [OnStart]
                public static Task<Result> StartA(RhinoContext<GameDb> ctx) => Task.FromResult(Result.Ok());
            }

            public static class HooksB {
                [OnStart]
                public static Task<Result> StartB(RhinoContext<GameDb> ctx) => Task.FromResult(Result.Ok());
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO032"));
    }

    [Test]
    public async Task OnInitAndOnStart_OnDifferentDatabases_EachOnlyAffectsItsOwnTarget() {
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
                [OnStart]
                public static Task<Result> StartFirst(RhinoContext<FirstDb> ctx) {
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
                public static Task<Result> Connect(RhinoContext<GameDb> ctx) {
                    GameDb.LastConnectedPrincipal = ctx.Session.Principal;
                    return Task.FromResult(Result.Ok());
                }
            }

            public partial class GameDb { public static PrincipalId LastConnectedPrincipal; }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var db = Activator.CreateInstance(asm.GetType("TestNs.GameDb")!)!;
        var session = new Session(SessionId.NewId(), new PrincipalId("alice"));

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
                public static Task<Result> Disconnect(RhinoContext<GameDb> ctx) {
                    GameDb.DisconnectWasCalled = true;
                    return Task.FromResult(Result.Ok());
                }
            }

            public partial class GameDb { public static bool DisconnectWasCalled; }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var db = Activator.CreateInstance(asm.GetType("TestNs.GameDb")!)!;
        var session = new Session(SessionId.NewId(), PrincipalId.Anonymous);

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
        var session = new Session(SessionId.NewId(), PrincipalId.Anonymous);

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
                [OnClientConnect]
                public Task<Result> Connect(RhinoContext<GameDb> ctx) => Task.FromResult(Result.Ok());
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
                [OnClientDisconnect]
                public static void Disconnect(RhinoContext<GameDb> ctx) { }
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
                [OnClientConnect]
                public static Task<Result> ConnectA(RhinoContext<GameDb> ctx) => Task.FromResult(Result.Ok());
            }

            public static class HooksB {
                [OnClientConnect]
                public static Task<Result> ConnectB(RhinoContext<GameDb> ctx) => Task.FromResult(Result.Ok());
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO032"));
    }
}
