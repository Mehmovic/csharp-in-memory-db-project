using RhinoDB.PreBuild;

namespace RhinoDB.PreBuild.Test;

public class UnmanagedTypeResolverTests {
    [TestCase("int")]
    [TestCase("long")]
    [TestCase("decimal")]
    [TestCase("Guid")]
    [TestCase("DateTime")]
    public void IsUnmanaged_BuiltinPrimitiveOrBlittableBclType_IsTrue(string typeName) {
        Assert.That(UnmanagedTypeResolver.IsUnmanaged(typeName, new Dictionary<string, string>()), Is.True);
    }

    [Test]
    public void IsUnmanaged_StringDirectly_IsFalse() {
        Assert.That(UnmanagedTypeResolver.IsUnmanaged("string", new Dictionary<string, string>()), Is.False);
    }

    [Test]
    public void IsUnmanaged_ProjectLocalFullyUnmanagedCustomType_RecursesAndReturnsTrue() {
        var sources = new Dictionary<string, string> {
            ["FixedPoint.cs"] = """
                [CustomType]
                public readonly partial record struct FixedPoint(int Whole, int Fraction);
                """
        };

        Assert.That(UnmanagedTypeResolver.IsUnmanaged("FixedPoint", sources), Is.True);
    }

    [Test]
    public void IsUnmanaged_ProjectLocalCustomTypeWithAStringField_RecursesAndReturnsFalse() {
        var sources = new Dictionary<string, string> {
            ["PlayerName.cs"] = """
                [CustomType]
                public readonly partial record struct PlayerName(string First, string Last);
                """
        };

        Assert.That(UnmanagedTypeResolver.IsUnmanaged("PlayerName", sources), Is.False);
    }

    [Test]
    public void IsUnmanaged_EnumTypedField_IsTrue() {
        var sources = new Dictionary<string, string> {
            ["Suit.cs"] = "public enum Suit { Clubs, Diamonds, Hearts, Spades }"
        };

        Assert.That(UnmanagedTypeResolver.IsUnmanaged("Suit", sources), Is.True);
    }

    [Test]
    public void IsUnmanaged_TwoLevelDeepNestedCustomType_BothUnmanaged_RecursesAllTheWayAndReturnsTrue() {
        var sources = new Dictionary<string, string> {
            ["FixedPoint.cs"] = """
                [CustomType]
                public readonly partial record struct FixedPoint(int Whole, int Fraction);
                """,
            ["Position.cs"] = """
                [CustomType]
                public readonly partial record struct Position(FixedPoint X, FixedPoint Y);
                """
        };

        Assert.That(UnmanagedTypeResolver.IsUnmanaged("Position", sources), Is.True);
    }

    [Test]
    public void IsUnmanaged_TwoLevelDeepNestedCustomType_InnermostHasAStringField_RecursesAllTheWayAndReturnsFalse() {
        var sources = new Dictionary<string, string> {
            ["Address.cs"] = """
                [CustomType]
                public readonly partial record struct Address(string City);
                """,
            ["Contact.cs"] = """
                [CustomType]
                public readonly partial record struct Contact(string Email, Address HomeAddress);
                """
        };

        Assert.That(UnmanagedTypeResolver.IsUnmanaged("Contact", sources), Is.False);
    }

    [Test]
    public void IsUnmanaged_UnresolvableTypeName_IsFalseByDefault() {
        Assert.That(UnmanagedTypeResolver.IsUnmanaged("SomethingThatDoesNotExistAnywhere", new Dictionary<string, string>()), Is.False);
    }

    [Test]
    public void IsUnmanaged_NullableUnmanagedPrimitive_UnwrapsTheQuestionMarkAndReturnsTrue() {
        Assert.That(UnmanagedTypeResolver.IsUnmanaged("int?", new Dictionary<string, string>()), Is.True);
    }
}
