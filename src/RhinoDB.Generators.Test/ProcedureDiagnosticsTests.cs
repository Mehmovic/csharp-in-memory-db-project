namespace RhinoDB.Generators.Test;

// What [Procedure] rejects at compile time, each with its own RHINO id (041-047).
public class ProcedureDiagnosticsTests {
    private const string Schema = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core;
        using RhinoDB.Core.Procedures;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class RootDb : DbContext<RootDbTransaction> { }

        [ChildDatabase<RootDb>]
        public partial class MarketDb : DbContext<MarketDbTransaction> { }

        [ChildDatabase<RootDb, string>]
        public partial class SessionDb : DbContext<SessionDbTransaction> { }

        [CustomType]
        public readonly partial record struct PlainMoney(long Amount, string Currency);

        """;

    static private string WithProcedures(string body) => Schema + "public static class Procs {\n" + body + "\n}\n";

    static private List<string> Ids(string body, string? configJson = null) =>
        GeneratorTestHost.GeneratorDiagnostics(WithProcedures(body), configJson)
            .Where(d => d.Id.StartsWith("RHINO04"))
            .Select(d => d.Id)
            .ToList();

    [Test]
    public void ValidProcedures_InBothShapes_HaveNoDiagnostics() {
        var ids = Ids("""
            [Procedure] public static Task<Result> A(RhinoCtx ctx) => Task.FromResult(Result.Ok());
            [Procedure] public static Task<Result<int>> B(RhinoCtx ctx, int x, string s, CancellationToken ct) => Task.FromResult(Result.Ok(x));
            [Procedure] public static ValueTask<Result<long[]>> C(RhinoCtx ctx, Guid id, DateTime at) => new(Result.Ok(new long[0]));
            [Procedure] public static Result D(RootDbTxCtx ctx, int x) => Result.Ok();
            [Procedure] public static Result E(MarketDbTxCtx ctx, string[] tags, PlainMoney money) => Result.Ok();
            """);

        Assert.That(ids, Is.Empty);
    }

    [Test]
    public void NonStatic_IsRHINO041() {
        var diagnostics = GeneratorTestHost.GeneratorDiagnostics(Schema + "public class Instance { [Procedure] public Result D(RootDbTxCtx ctx) => Result.Ok(); }");

        Assert.That(diagnostics.Where(d => d.Id.StartsWith("RHINO04")).Select(d => d.Id), Is.EqualTo(new[] { "RHINO041" }));
    }

    [TestCase("[Procedure] public static Result NoContext() => Result.Ok();", TestName = "NoContextParameter")]
    [TestCase("[Procedure] public static Result Wrong(string s) => Result.Ok();", TestName = "FirstParameterNotAContext")]
    [TestCase("[Procedure] public static Task<int> Wrong(RhinoCtx ctx) => Task.FromResult(1);", TestName = "GeneralNotReturningResult")]
    [TestCase("[Procedure] public static Result Wrong(RhinoCtx ctx) => Result.Ok();", TestName = "GeneralNotAsync")]
    [TestCase("[Procedure] public static int Wrong(RootDbTxCtx ctx) => 1;", TestName = "TransactionNotReturningResult")]
    [TestCase("[Procedure] public static Task<Result> Wrong<T>(RhinoCtx ctx) => Task.FromResult(Result.Ok());", TestName = "Generic")]
    [TestCase("[Procedure] public static Task<Result> Wrong(RhinoCtx ctx, ref int x) => Task.FromResult(Result.Ok());", TestName = "RefParameter")]
    [TestCase("[Procedure] public static Result Wrong(NoSuchDbTxCtx ctx) => Result.Ok();", TestName = "TxCtxOfAnUnknownDatabase")]
    public void AnInvalidSignature_IsRHINO042(string procedure) {
        Assert.That(Ids(procedure), Is.EqualTo(new[] { "RHINO042" }));
    }

    [TestCase("[Procedure] public static Task<Result> Wrong(RootDbTxCtx ctx) => Task.FromResult(Result.Ok());", TestName = "ReturnsATask")]
    [TestCase("[Procedure] public static Result Wrong(RootDbTxCtx ctx, CancellationToken ct) => Result.Ok();", TestName = "TakesACancellationToken")]
    [TestCase("[Procedure] public static Result Wrong(RootDbTxCtx ctx, RhinoCtx other) => Result.Ok();", TestName = "AlsoTakesARhinoCtx")]
    public void ATransactionProcedureThatIsNotSynchronousOrSelfContained_IsRHINO043(string procedure) {
        Assert.That(Ids(procedure), Is.EqualTo(new[] { "RHINO043" }));
    }

    [Test]
    public void ATransactionProcedureOnAKeyedChild_IsRHINO044_PointingAtTheGeneralShape() {
        var diagnostics = GeneratorTestHost.GeneratorDiagnostics(WithProcedures("[Procedure] public static Result Wrong(SessionDbTxCtx ctx) => Result.Ok();"));

        var rhino044 = diagnostics.Single(d => d.Id == "RHINO044");
        Assert.That(rhino044.GetMessage(), Does.Contain("keyed Child").And.Contain("RhinoCtx ctx"));
    }

    [TestCase("Result<int>", TestName = "ResultOfInt")]
    [TestCase("Result<string>", TestName = "ResultOfString")]
    public void ATransactionProcedureReturningAValue_IsRHINO047_PointingAtTheGeneralShape(string returnType) {
        var diagnostics = GeneratorTestHost.GeneratorDiagnostics(WithProcedures($"[Procedure] public static {returnType} Wrong(RootDbTxCtx ctx) => default;"));

        var rhino04 = diagnostics.Where(d => d.Id.StartsWith("RHINO04")).ToList();
        Assert.That(rhino04.Select(d => d.Id), Is.EqualTo(new[] { "RHINO047" }));
        Assert.That(rhino04[0].GetMessage(), Does.Contain("views").And.Contain("Task<Result<T>>"));
    }

    [TestCase("object", TestName = "Object")]
    [TestCase("List<int>", TestName = "ListOfInt")]
    [TestCase("int?", TestName = "NullableInt")]
    [TestCase("int[][]", TestName = "JaggedArray")]
    [TestCase("Action", TestName = "Delegate")]
    public void AnUnsupportedParameterType_IsRHINO045(string type) {
        Assert.That(Ids($"[Procedure] public static Result Wrong(RootDbTxCtx ctx, {type} value) => Result.Ok();"), Is.EqualTo(new[] { "RHINO045" }));
    }

    [Test]
    public void AnUnsupportedResultType_IsRHINO045() {
        Assert.That(Ids("[Procedure] public static Task<Result<Dictionary<int, int>>> Wrong(RhinoCtx ctx) => throw null!;"), Is.EqualTo(new[] { "RHINO045" }));
    }

    [TestCase("VersionedMemoryPack", "[MemoryPackable]")]
    [TestCase("MessagePack", "[MessagePackObject]")]
    public void ACustomTypeWithoutTheProtocolsSerializationAttribute_IsRHINO045(string protocol, string missing) {
        var diagnostics = GeneratorTestHost.GeneratorDiagnostics(
            WithProcedures("[Procedure] public static Result Pay(RootDbTxCtx ctx, PlainMoney money) => Result.Ok();"),
            $$"""{ "Generator": { "ClientProtocol": "{{protocol}}" } }""");

        var rhino045 = diagnostics.Single(d => d.Id == "RHINO045");
        Assert.That(rhino045.GetMessage(), Does.Contain(missing));
    }

    [Test]
    public void ACustomTypeUnderRaw_NeedsNoSerializationAttributes() {
        Assert.That(Ids("[Procedure] public static Result Pay(RootDbTxCtx ctx, PlainMoney money) => Result.Ok();"), Is.Empty);
    }

    [Test]
    public void TwoProceduresWithTheSameName_AreRHINO046() {
        var ids = Ids("""
            [Procedure(Name = "buy")] public static Result A(RootDbTxCtx ctx) => Result.Ok();
            [Procedure(Name = "buy")] public static Result B(RootDbTxCtx ctx) => Result.Ok();
            """);

        Assert.That(ids, Is.EqualTo(new[] { "RHINO046" }));
    }

    [Test]
    public void TheSameMethodNameInTwoClasses_IsRHINO046_UntilOneGetsAName() {
        const string twoClasses = """
            public static class Shop { [Procedure] public static Result Buy(RootDbTxCtx ctx) => Result.Ok(); }
            public static class Market { [Procedure{0}] public static Result Buy(MarketDbTxCtx ctx) => Result.Ok(); }
            """;

        var clashing = GeneratorTestHost.GeneratorDiagnostics(Schema + twoClasses.Replace("{0}", "")).Where(d => d.Id.StartsWith("RHINO04"));
        var named = GeneratorTestHost.GeneratorDiagnostics(Schema + twoClasses.Replace("{0}", "(Name = \"market.buy\")")).Where(d => d.Id.StartsWith("RHINO04"));

        Assert.That(clashing.Select(d => d.Id), Is.EqualTo(new[] { "RHINO046" }));
        Assert.That(named, Is.Empty);
    }
}
