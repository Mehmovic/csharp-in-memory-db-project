namespace RhinoDB.Core.Test;

public class PackedVersionTests {
    [Test]
    public void Pack_ThenUnpack_RoundTrips() {
        var packed = PackedVersion.Pack(1, 5, 42);
        var (major, minor, patch) = PackedVersion.Unpack(packed);

        Assert.That(major, Is.EqualTo(1));
        Assert.That(minor, Is.EqualTo(5));
        Assert.That(patch, Is.EqualTo(42));
    }

    [TestCase((byte)1, (ushort)0, (byte)2, (ushort)0)]
    [TestCase((byte)1, (ushort)5, (byte)1, (ushort)6)]
    [TestCase((byte)1, (ushort)5, (byte)2, (ushort)0)]
    public void Pack_OrderingMatchesVersionOrdering_LowerVersionPacksToSmallerValue(byte minorA, ushort patchA, byte minorB, ushort patchB) {
        var a = PackedVersion.Pack(1, minorA, patchA);
        var b = PackedVersion.Pack(1, minorB, patchB);

        Assert.That(a, Is.LessThan(b), "plain numeric <= on packed values must match real version ordering");
    }

    [Test]
    public void Pack_HigherMajor_AlwaysPacksLarger_RegardlessOfMinorPatch() {
        var older = PackedVersion.Pack(1, 255, ushort.MaxValue);
        var newer = PackedVersion.Pack(2, 0, 0);

        Assert.That(older, Is.LessThan(newer));
    }
}
