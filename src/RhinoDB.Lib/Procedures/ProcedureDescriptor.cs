using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Realtime;

namespace RhinoDB.Lib.Procedures;

public delegate Task<Result<ReadOnlyMemory<byte>>> ProcedureHandler(RhinoHost host, Session session, ReadOnlyMemory<byte> body, CancellationToken ct);

public sealed record ProcedureDescriptor(string Name, uint Hash, bool SingleTransaction, ProcedureHandler Handler);

public sealed record ProcedureFault(string ProcedureName, uint Hash, ErrorKind Kind, Exception Exception, Session Session);
