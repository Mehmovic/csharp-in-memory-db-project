namespace RhinoDB.Lib.Prefs.Test;

// The on-disk record codec on its own. Iterates the enum rather than listing kinds, so a kind appended to PrefKind
// without widening TryDecode's range check fails here.
public class PrefRecordTests {
    // PrefKind is internal, so the cases carry the raw on-disk byte.
    static private IEnumerable<byte> EveryKind() => Enum.GetValues<PrefKind>().Select(k => (byte)k);

    [TestCaseSource(nameof(EveryKind))]
    public void EveryKind_RoundTripsThroughEncodeAndTryDecode(byte kindByte) {
        var kind = (PrefKind)kindByte;
        var record = PrefRecord.Encode(kind, 12345, 0xDEADBEEF, [1, 2, 3]);

        var ok = PrefRecord.TryDecode(record, out var decodedKind, out var cacheTicks, out var typeHash, out var payload);
        var payloadBytes = payload.ToArray();

        Assert.Multiple(() => {
            Assert.That(ok, Is.True);
            Assert.That(decodedKind, Is.EqualTo(kind));
            Assert.That(cacheTicks, Is.EqualTo(12345));
            Assert.That(typeHash, Is.EqualTo(0xDEADBEEF));
            Assert.That(payloadBytes, Is.EqualTo(new byte[] { 1, 2, 3 }));
        });
    }

    [Test]
    public void AKindByteOutsideTheEnum_IsRefused() {
        var max = (byte)Enum.GetValues<PrefKind>().Max();

        Assert.Multiple(() => {
            Assert.That(PrefRecord.TryDecode(WithKindByte(0), out _, out _, out _, out _), Is.False, "0 is not a kind.");
            Assert.That(PrefRecord.TryDecode(WithKindByte((byte)(max + 1)), out _, out _, out _, out _), Is.False, "one past the last kind.");
        });
    }

    static private byte[] WithKindByte(byte kind) {
        var record = PrefRecord.Encode(PrefKind.String, 0, 0, []);
        record[1] = kind;
        return record;
    }
}
