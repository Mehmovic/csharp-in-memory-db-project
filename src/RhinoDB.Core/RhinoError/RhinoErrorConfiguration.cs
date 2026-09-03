using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("RhinoDB.Test.Core")]

namespace RhinoDB.Core.Results;

static public class RhinoErrorConfiguration {
    static public bool Verbose { get; private set; } = true;

    static private bool configured;

    static public void Configure(bool verbose) {
        if (configured) {
            throw new InvalidOperationException(
                "RhinoErrorConfiguration.Configure can only be called once, at process startup.");
        }

        Verbose = verbose;
        configured = true;
    }

    // Test-only escape hatch. Real callers can never reach this
    static internal void ResetForTests(bool verbose) {
        Verbose = verbose;
        configured = false;
    }
}
