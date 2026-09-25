using Microsoft.Build.Locator;

namespace RhinoDB.Tools.Migration.Test;

// MSBuildLocator.RegisterDefaults() must run before any code in this assembly touches
// Microsoft.Build.*/Microsoft.CodeAnalysis.MSBuild types - NUnit guarantees OneTimeSetUp completes before
// any test in the assembly is invoked (and JIT-compiles the method bodies that reference those types), so
// this is the one safe place to call it exactly once.
[SetUpFixture]
public class AssemblySetup {
    [OneTimeSetUp]
    public void RegisterMSBuild() => MSBuildLocator.RegisterDefaults();
}
