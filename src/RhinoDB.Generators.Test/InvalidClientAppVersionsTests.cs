using RhinoDB.Core;

namespace RhinoDB.Generators.Test;

// [assembly: InvalidClientAppVersions(...)] (Part F, Phase 6) - the generator's first-ever assembly-attribute
// read. Genuinely project-scoped, not anchored to any [Table]/[Database] - RhinoClientCompat is always
// emitted, even in a compilation with neither.
public class InvalidClientAppVersionsTests {
    [Test]
    public void NoAttributeDeclared_IsAppVersionInvalidAlwaysFalse() {
        const string source = """
            namespace TestNs;
            public class Marker { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var holderType = asm.GetType("RhinoClientCompat")!;
        var method = holderType.GetMethod("IsAppVersionInvalid")!;

        Assert.That(method.Invoke(null, [PackedVersion.Pack(1, 0, 0)]), Is.False);
        Assert.That(method.Invoke(null, [0u]), Is.False);
    }

    [Test]
    public void ZeroTableOrDatabaseTypesAtAll_HolderIsStillEmitted() {
        const string source = """
            namespace TestNs;
            public class NotARhinoDbTypeAtAll { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);

        Assert.That(asm.GetType("RhinoClientCompat"), Is.Not.Null);
    }

    [Test]
    public void LessThanOrEqualToDeclared_FloorSemantics_InvalidatesThatVersionAndEverythingOlder() {
        var floor = PackedVersion.Pack(1, 5, 0);
        var source = $$"""
            [assembly: RhinoDB.Core.Tables.InvalidClientAppVersions(LessThanOrEqualTo = new uint[] { {{floor}}u })]

            namespace TestNs;
            public class Marker { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var method = asm.GetType("RhinoClientCompat")!.GetMethod("IsAppVersionInvalid")!;

        Assert.That(method.Invoke(null, [PackedVersion.Pack(1, 5, 0)]), Is.True, "exactly at the floor - invalid, must upgrade");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(1, 0, 0)]), Is.True, "older than the floor - invalid");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(1, 5, 1)]), Is.False, "newer than the floor - fine");
    }

    [Test]
    public void NotEqualToDeclared_PinSemantics_OnlyDeclaredVersionsAreAccepted() {
        var pinned = PackedVersion.Pack(2, 0, 0);
        var source = $$"""
            [assembly: RhinoDB.Core.Tables.InvalidClientAppVersions(NotEqualTo = new uint[] { {{pinned}}u })]

            namespace TestNs;
            public class Marker { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var method = asm.GetType("RhinoClientCompat")!.GetMethod("IsAppVersionInvalid")!;

        Assert.That(method.Invoke(null, [PackedVersion.Pack(2, 0, 0)]), Is.False, "the one pinned version - accepted");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(2, 0, 1)]), Is.True, "not the pinned version - rejected, even though it's newer");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(1, 9, 0)]), Is.True, "not the pinned version - rejected, even though it's older");
    }

    [Test]
    public void WhitelistedVersion_OverridesTheFloorRejection() {
        var floor = PackedVersion.Pack(2, 0, 0);
        var whitelisted = PackedVersion.Pack(1, 0, 0);
        var source = $$"""
            [assembly: RhinoDB.Core.Tables.InvalidClientAppVersions(Whitelist = new uint[] { {{whitelisted}}u }, LessThanOrEqualTo = new uint[] { {{floor}}u })]

            namespace TestNs;
            public class Marker { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var method = asm.GetType("RhinoClientCompat")!.GetMethod("IsAppVersionInvalid")!;

        Assert.That(method.Invoke(null, [whitelisted]), Is.False, "explicitly whitelisted - accepted even though it's below the floor");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(1, 5, 0)]), Is.True, "below the floor and NOT whitelisted - still rejected");
    }

    [Test]
    public void WhitelistedVersion_OverridesThePinRejection() {
        var pinned = PackedVersion.Pack(3, 0, 0);
        var whitelisted = PackedVersion.Pack(1, 0, 0);
        var source = $$"""
            [assembly: RhinoDB.Core.Tables.InvalidClientAppVersions(Whitelist = new uint[] { {{whitelisted}}u }, NotEqualTo = new uint[] { {{pinned}}u })]

            namespace TestNs;
            public class Marker { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var method = asm.GetType("RhinoClientCompat")!.GetMethod("IsAppVersionInvalid")!;

        Assert.That(method.Invoke(null, [whitelisted]), Is.False, "explicitly whitelisted - accepted even though it doesn't match the pin");
        Assert.That(method.Invoke(null, [pinned]), Is.False, "matches the pin directly - also accepted");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(9, 9, 9)]), Is.True, "neither whitelisted nor the pin - rejected");
    }

    [Test]
    public void BothModesDeclaredTogether_EitherRuleCanInvalidate() {
        var floor = PackedVersion.Pack(1, 0, 0);
        var pinned = PackedVersion.Pack(3, 0, 0);
        var source = $$"""
            [assembly: RhinoDB.Core.Tables.InvalidClientAppVersions(LessThanOrEqualTo = new uint[] { {{floor}}u }, NotEqualTo = new uint[] { {{pinned}}u })]

            namespace TestNs;
            public class Marker { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var method = asm.GetType("RhinoClientCompat")!.GetMethod("IsAppVersionInvalid")!;

        Assert.That(method.Invoke(null, [PackedVersion.Pack(0, 5, 0)]), Is.True, "below the floor");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(2, 0, 0)]), Is.True, "above the floor but not the pin");
        Assert.That(method.Invoke(null, [PackedVersion.Pack(3, 0, 0)]), Is.False, "above the floor and matches the pin - accepted");
    }
}
