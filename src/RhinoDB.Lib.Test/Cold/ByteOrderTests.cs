using RhinoDB.Core;

namespace RhinoDB.Lib.Cold.Test;

// RhinoDB stores are little-endian only, by decision: a big-endian machine gets a clear error at open, never a
// store whose numbers read back byte-swapped.
public class ByteOrderTests {
    [Test]
    public void ABigEndianMachine_IsRefused_WithByteOrderUnsupported() {
        var result = ColdStore.EnsureSupportedByteOrder(isLittleEndian: false);

        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.ByteOrderUnsupported));
        Assert.That(result.GetError().ToException().Message, Does.Contain("little-endian only"));
    }

    [Test]
    public void ALittleEndianMachine_IsAccepted() {
        Assert.That(ColdStore.EnsureSupportedByteOrder(isLittleEndian: true).IsOk(), Is.True);
    }

    [Test]
    public void ThisMachine_IsLittleEndian_SoStoresOpenHere() {
        Assert.That(BitConverter.IsLittleEndian, Is.True, "every platform RhinoDB's tests run on is little-endian.");
    }
}
