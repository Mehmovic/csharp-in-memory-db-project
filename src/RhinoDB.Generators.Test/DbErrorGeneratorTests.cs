using RhinoDB.Core;
using RhinoDB.Generators;

namespace RhinoDB.Generators.Test;

// DbErrorGenerator produces the whole error surface of RhinoDB.Core: every ErrorKind member,
// every DbError factory, and the ToException mapping. Nothing else exercises the generator
// itself - GenerateDbErrorEnumTests only consumes the file a real build produced, so a
// regression here would still compile and those tests would still pass.
//
// Asserted on emitted text, because the text is the contract: a factory that loses its
// `Exception? inner = null` parameter compiles fine on its own and only breaks at a call site.
//
// NOT COVERED: the [GenerateDbError] enum companion (EmitEnumHelper). It does not fire under
// the in-process driver - no hint name is emitted, with no generator diagnostic and no
// exception - while it demonstrably works in a real build, where RhinoDB.Core.Test's
// Err.InsufficientFunds() is generated at compile time and its tests pass. So that is a
// harness limitation rather than proven-correct logic; covering it needs the cause found first.
public class DbErrorGeneratorTests {
    private const string CorePartial = """
        namespace RhinoDB.Core {
            public readonly partial struct DbError {
                public ErrorKind Kind { get; }
                public ushort CustomCode { get; }
                private readonly Exception? systemException;
                private DbError(ErrorKind kind, Exception? inner) { Kind = kind; systemException = inner; }
            }
        }
        """;

    private const string Exceptions = """
        using System;
        using RhinoDB.Core.Exceptions;

        namespace Probe {
            [GenerateDbError]
            public sealed class AlphaException : Exception {
                public AlphaException(Exception? inner = null) : base("alpha", inner) { }
            }

            // No Exception suffix: the factory must keep the type name unchanged.
            [GenerateDbError]
            public sealed class Beta : Exception {
                public Beta(Exception? inner = null) : base("beta", inner) { }
            }
        }
        """;

    [Test]
    public void EmitCore_IsSkippedInAConsumingApp() {
        Assert.That(Run(Exceptions + CorePartial, "SomeApp").ContainsKey("DbError.g.cs"), Is.False,
            "the library owns ErrorKind and DbError - a second copy in an app would be a duplicate definition");
    }

    [Test]
    public void EmitCore_RunsForTheRhinoDBCoreAssembly() {
        Assert.That(Run(Exceptions + CorePartial, "RhinoDB.Core").ContainsKey("DbError.g.cs"), Is.True);
    }

    [Test]
    public void EmitCore_RunsEvenWithNothingAttributed() {
        var errorKind = ErrorKind(Run(CorePartial, "RhinoDB.Core"));

        Assert.Multiple(() => {
            Assert.That(errorKind, Does.Contain("SystemFailure"));
            Assert.That(errorKind, Does.Contain("Custom"));
        });
    }

    [Test]
    public void ErrorKind_StartsAtSystemFailureAndEndsAtCustom() {
        var members = ErrorKind(Run(Exceptions + CorePartial, "RhinoDB.Core"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(l => l.Trim().TrimEnd(',')).ToArray();

        Assert.Multiple(() => {
            Assert.That(members[0], Is.EqualTo("SystemFailure"));
            Assert.That(members[^1], Is.EqualTo("Custom"));
            Assert.That(members, Does.Contain("Alpha"));
            Assert.That(members, Does.Contain("Beta"));
        });
    }

    [Test]
    public void FactoryName_StripsTheExceptionSuffix() {
        Assert.That(Source(), Does.Contain("public static DbError Alpha(Exception? inner = null) => new(ErrorKind.Alpha, inner);"));
    }

    [Test]
    public void FactoryName_KeepsATypeNameWithoutTheExceptionSuffix() {
        Assert.That(Source(), Does.Contain("public static DbError Beta(Exception? inner = null) => new(ErrorKind.Beta, inner);"));
    }

    [Test]
    public void ToException_MapsEveryKindToItsFullyQualifiedException() {
        Assert.Multiple(() => {
            Assert.That(Source(), Does.Contain("ErrorKind.Alpha => new global::Probe.AlphaException(systemException),"));
            Assert.That(Source(), Does.Contain("ErrorKind.Beta => new global::Probe.Beta(systemException),"));
        });
    }

    [Test]
    public void ToException_ReturnsTheCapturedExceptionUnchangedForSystemFailure() {
        Assert.That(Source(), Does.Contain("ErrorKind.SystemFailure => systemException!"),
            "SystemFailure must surface the original exception, not wrap it - callers match on its type");
    }

    [Test]
    public void ToException_HasAFallbackArmForAnUnknownKind() {
        Assert.That(Source(), Does.Contain("_ => new Exception"));
    }

    [Test]
    public void Custom_StoresItsCodeAndTheInnerException() {
        Assert.Multiple(() => {
            Assert.That(Source(), Does.Contain("public ushort CustomCode { get; }"));
            Assert.That(Source(), Does.Contain("public static DbError Custom(ushort code, Exception? inner = null)"));
            Assert.That(Source(), Does.Contain("Kind = ErrorKind.Custom;"));
            Assert.That(Source(), Does.Contain("systemException = inner;"));
        });
    }

    static private string Source() => Run(Exceptions + CorePartial, "RhinoDB.Core")["DbError.g.cs"];

    static private string ErrorKind(Dictionary<string, string> generated) {
        var text = generated["DbError.g.cs"];
        var start = text.IndexOf("public enum ErrorKind : byte {", StringComparison.Ordinal);
        return text.Substring(start, text.IndexOf('}', start) - start);
    }

    static private Dictionary<string, string> Run(string source, string assemblyName) =>
        GeneratorTestHost.RunDbErrorGenerator(source, assemblyName);
}