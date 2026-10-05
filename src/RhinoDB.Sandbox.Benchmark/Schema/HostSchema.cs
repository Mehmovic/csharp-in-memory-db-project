using RhinoDB.Core.Tables;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Sandbox.Benchmark.Schema;

[Database]
public partial class HostRootDb : DbContext<HostRootDbTransaction> { }

[ChildDatabase<HostRootDb, int>]
public partial class MatchDb : DbContext<MatchDbTransaction> { }

[ChildDatabase<HostRootDb>]
public partial class LobbyDb : DbContext<LobbyDbTransaction> { }
