using RhinoDB.Core.Tables;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Sandbox;

[Database]
public partial class SandboxDb : DbContext<SandboxDbTransaction> { }
