namespace RhinoDB.Generators.Test;

// rdbsettings.json's Network.Transport is read at compile time: WebSocket is reliable and ordered only, so any use of
// another Delivery mode is a build error (RHINO040) rather than a send that silently behaves differently.
public class DeliveryTransportAnalyzerTests {
    // The real enum lives in RhinoDB.Lib.Server, which this test project doesn't reference - the analyzer finds the
    // type by metadata name, so a same-named declaration stands in for it.
    private const string DeliveryStub = """
        namespace RhinoDB.Lib.Server.Realtime {
            public enum Delivery { ReliableOrdered, Unreliable }
        }
        """;

    private const string UsesUnreliable = DeliveryStub + """

        namespace App {
            using RhinoDB.Lib.Server.Realtime;
            public static class Sender {
                public static Delivery Pick() => Delivery.Unreliable;
            }
        }
        """;

    private const string UsesReliableOnly = DeliveryStub + """

        namespace App {
            using RhinoDB.Lib.Server.Realtime;
            public static class Sender {
                public static Delivery Pick() => Delivery.ReliableOrdered;
            }
        }
        """;

    [Test]
    public void Unreliable_WithWebSocketSelected_IsACompileError() {
        var diagnostics = GeneratorTestHost.RunAnalyzer(new DeliveryTransportAnalyzer(), UsesUnreliable, """{ "Network": { "Transport": "WebSocket" } }""");

        var rhino040 = diagnostics.Single(d => d.Id == "RHINO040");
        Assert.That(rhino040.Severity, Is.EqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.That(rhino040.GetMessage(), Does.Contain("Unreliable").And.Contain("WebSocket"));
    }

    [Test]
    public void Unreliable_WithNoSettingsFile_IsACompileError_BecauseWebSocketIsTheDefault() {
        var diagnostics = GeneratorTestHost.RunAnalyzer(new DeliveryTransportAnalyzer(), UsesUnreliable);

        Assert.That(diagnostics.Count(d => d.Id == "RHINO040"), Is.EqualTo(1));
    }

    [Test]
    public void Unreliable_WithAMalformedSettingsFile_FallsBackToTheDefaultTransport_AndStillErrors() {
        var diagnostics = GeneratorTestHost.RunAnalyzer(new DeliveryTransportAnalyzer(), UsesUnreliable, "{ not json");

        Assert.That(diagnostics.Count(d => d.Id == "RHINO040"), Is.EqualTo(1));
    }

    [Test]
    public void ReliableOrdered_IsAlwaysAllowed() {
        var diagnostics = GeneratorTestHost.RunAnalyzer(new DeliveryTransportAnalyzer(), UsesReliableOnly, """{ "Network": { "Transport": "WebSocket" } }""");

        Assert.That(diagnostics.Where(d => d.Id == "RHINO040"), Is.Empty);
    }

    [Test]
    public void AProjectWithoutTheNetworkLibrary_IsNotAnalyzed() {
        const string source = "namespace App { public enum Delivery { Unreliable } public static class S { public static Delivery D => Delivery.Unreliable; } }";

        var diagnostics = GeneratorTestHost.RunAnalyzer(new DeliveryTransportAnalyzer(), source);

        Assert.That(diagnostics.Where(d => d.Id == "RHINO040"), Is.Empty, "only RhinoDB's own Delivery type is checked, not any enum with that name.");
    }

    [Test]
    public void EveryUse_IsReported_NotJustTheFirst() {
        const string source = DeliveryStub + """

            namespace App {
                using RhinoDB.Lib.Server.Realtime;
                public static class Sender {
                    public static Delivery A() => Delivery.Unreliable;
                    public static Delivery B() => Delivery.Unreliable;
                }
            }
            """;

        var diagnostics = GeneratorTestHost.RunAnalyzer(new DeliveryTransportAnalyzer(), source);

        Assert.That(diagnostics.Count(d => d.Id == "RHINO040"), Is.EqualTo(2));
    }
}
