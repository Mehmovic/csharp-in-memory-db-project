using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Hosting.Test;
using RhinoDB.Lib.Realtime;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Procedures.Test;

// RhinoHost.DispatchProcedureAsync with hand-written handlers: routing, the defensive catch, fault logging. Generated
// handlers are covered in Generators.Test (ProcedureTests).
public class ProcedureDispatchTests {
    private string dir = "";
    private RhinoHost? host;
    private readonly List<ProcedureFault> faults = [];
    private readonly Session session = new Session(ConnectionId.NewId(), Identity.Anonymous, appVersion: 9);

    private sealed class FakeDb(ColdStore cold) : DbContext(cold);

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-procedure-dispatch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        RhinoHostConfigTestHelper.WriteConfig(dir, new HostConfig { ColdPath = dir, HttpEnabled = false, HttpPort = 0 });
        faults.Clear();
    }

    [TearDown]
    public void TearDown() {
        if (host is not null) {
            var cold = host.GetDatabase<FakeDb>().Cold;
            host.Dispose();
            cold?.Dispose();
        }
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task<RhinoHost> Build(params ProcedureDescriptor[] procedures) {
        var builder = RhinoHostBuilder.Create(dir).OnProcedureFault(faults.Add);
        foreach (var procedure in procedures) builder.AddProcedure(procedure);
        host = (await builder.AddDatabase<FakeDb, DefaultTransaction>(o => o.CreateDb = cold => new FakeDb(cold)).BuildAsync()).Unwrap();
        return host;
    }

    static private ProcedureDescriptor Procedure(string name, ProcedureHandler handler, bool singleTransaction = false) =>
        new ProcedureDescriptor(name, NameHash.Compute(name), singleTransaction, handler);

    [Test]
    public async Task ARegisteredProcedure_GetsTheSessionAndBody_AndItsReplyIsReturned() {
        Session? seen = null;
        var built = await Build(Procedure("Echo", (_, s, body, _) => {
            seen = s;
            return Task.FromResult(Result<ReadOnlyMemory<byte>>.Ok(body));
        }));

        var reply = await built.DispatchProcedureAsync(NameHash.Compute("Echo"), session, new byte[] { 1, 2, 3 }, CancellationToken.None);

        Assert.That(reply.Unwrap().ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(seen, Is.SameAs(session));
    }

    [Test]
    public async Task AnUnknownHash_IsUnknownProcedure_AndIsNotAFault() {
        var built = await Build();

        var reply = await built.DispatchProcedureAsync(0xDEADBEEF, session, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.That(reply.GetError().Kind, Is.EqualTo(ErrorKind.UnknownProcedure));
        Assert.That(faults, Is.Empty);
    }

    [Test]
    public async Task AHandlerThatThrowsSynchronously_IsProcedureFailed_AndLoggedOnceWithTheException() {
        var bug = new InvalidOperationException("bug");
        var built = await Build(Procedure("Broken", (_, _, _, _) => throw bug));

        var reply = await built.DispatchProcedureAsync(NameHash.Compute("Broken"), session, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.That(reply.GetError().Kind, Is.EqualTo(ErrorKind.ProcedureFailed));
        Assert.That(faults, Has.Count.EqualTo(1));
        Assert.That(faults[0].Exception, Is.SameAs(bug));
        Assert.That(faults[0].ProcedureName, Is.EqualTo("Broken"));
        Assert.That(faults[0].Session, Is.SameAs(session));
    }

    [Test]
    public async Task AHandlerReturningAFaultedTask_IsProcedureFailed() {
        var built = await Build(Procedure("Faulted", (_, _, _, _) => Task.FromException<Result<ReadOnlyMemory<byte>>>(new TimeoutException())));

        var reply = await built.DispatchProcedureAsync(NameHash.Compute("Faulted"), session, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.That(reply.GetError().Kind, Is.EqualTo(ErrorKind.ProcedureFailed));
        Assert.That(faults.Single().Exception, Is.TypeOf<TimeoutException>());
    }

    [Test]
    public async Task ArgsInvalid_ReturnedByAHandler_IsLoggedAsAFault() {
        var built = await Build(Procedure("Picky", (_, _, _, _) =>
            Task.FromResult(Result<ReadOnlyMemory<byte>>.Error(DbError.ProcedureArgsInvalid(new FormatException("short body"))))));

        await built.DispatchProcedureAsync(NameHash.Compute("Picky"), session, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.That(faults.Single().Kind, Is.EqualTo(ErrorKind.ProcedureArgsInvalid));
        Assert.That(faults[0].Exception, Is.TypeOf<FormatException>(), "the log gets the cause, not the wrapper.");
    }

    [Test]
    public async Task AFaultHookThatThrows_NeverBreaksTheReply() {
        host = (await RhinoHostBuilder.Create(dir)
            .OnProcedureFault(_ => throw new Exception("logger down"))
            .AddProcedure(Procedure("Broken", (_, _, _, _) => throw new InvalidOperationException()))
            .AddDatabase<FakeDb, DefaultTransaction>(o => o.CreateDb = cold => new FakeDb(cold))
            .BuildAsync()).Unwrap();

        var reply = await host.DispatchProcedureAsync(NameHash.Compute("Broken"), session, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.That(reply.GetError().Kind, Is.EqualTo(ErrorKind.ProcedureFailed));
    }

    [Test]
    public async Task Procedures_ListsEveryRegisteredDescriptor() {
        var built = await Build(
            Procedure("A", (_, _, _, _) => Task.FromResult(Result<ReadOnlyMemory<byte>>.Ok(ReadOnlyMemory<byte>.Empty)), singleTransaction: true),
            Procedure("B", (_, _, _, _) => Task.FromResult(Result<ReadOnlyMemory<byte>>.Ok(ReadOnlyMemory<byte>.Empty))));

        Assert.That(built.Procedures.Select(p => (p.Name, p.SingleTransaction)), Is.EquivalentTo(new[] { ("A", true), ("B", false) }));
    }

    [Test]
    public void AddingTwoProceduresWithOneHash_Throws_NamingTheFirst() {
        var builder = RhinoHostBuilder.Create(dir).AddProcedure(Procedure("A", (_, _, _, _) => throw new Exception()));

        var ex = Assert.Throws<ArgumentException>(() => builder.AddProcedure(Procedure("A", (_, _, _, _) => throw new Exception())));

        Assert.That(ex!.Message, Does.Contain("'A'"));
    }

    [Test]
    public void TheDefaultFaultLog_IsOneStderrLine_WithTheProcedureAndTheException() {
        var original = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try {
            ProcedureFaults.WriteToStandardError(new ProcedureFault("Buy", 42, ErrorKind.ProcedureFailed, new InvalidOperationException("boom"), session));
        } finally {
            Console.SetError(original);
        }

        Assert.That(captured.ToString(), Does.StartWith("RhinoDB: procedure 'Buy' (42) failed with ProcedureFailed").And.Contain("boom"));
    }
}
