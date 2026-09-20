using RhinoDB.Core.Tables;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Run.Server.Sandbox;

[Database]
public partial class SandboxDb : DbContext<SandboxDbTransaction> { }
