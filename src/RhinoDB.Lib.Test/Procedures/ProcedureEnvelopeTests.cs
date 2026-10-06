using MemoryPack;

namespace RhinoDB.Lib.Procedures.Test;

// The VersionTolerant object envelope's reader: everything a hostile or broken client can send must come back false,
// never throw or read out of bounds. (Byte-identity with the real serializer is pinned in ProcedureWireTests.)
public class ProcedureEnvelopeTests {
    [Test]
    public void AnEnvelope_RoundTripsItsMembers() {
        var written = ProcedureEnvelope.WriteMemoryPackObject(MemoryPackSerializer.Serialize(7), MemoryPackSerializer.Serialize("ada"), []);

        Assert.That(ProcedureEnvelope.TryReadMemoryPackObject(written, out var members), Is.True);
        Assert.That(members, Has.Length.EqualTo(3));
        Assert.That(MemoryPackSerializer.Deserialize<int>(members[0].Span), Is.EqualTo(7));
        Assert.That(MemoryPackSerializer.Deserialize<string>(members[1].Span), Is.EqualTo("ada"));
        Assert.That(members[2].Length, Is.Zero);
    }

    [Test]
    public void AnEmptyEnvelope_HasNoMembers() {
        Assert.That(ProcedureEnvelope.TryReadMemoryPackObject(ProcedureEnvelope.WriteMemoryPackObject(), out var members), Is.True);
        Assert.That(members, Is.Empty);
    }

    [Test]
    public void AnEmptyBody_IsRejected() {
        Assert.That(ProcedureEnvelope.TryReadMemoryPackObject(ReadOnlyMemory<byte>.Empty, out _), Is.False);
    }

    [Test]
    public void ANullObjectHeader_IsRejected() {
        Assert.That(ProcedureEnvelope.TryReadMemoryPackObject(new byte[] { MemoryPackCode.NullObject }, out _), Is.False);
    }

    [Test]
    public void AMemberLengthPastTheEndOfTheBody_IsRejected() {
        var written = ProcedureEnvelope.WriteMemoryPackObject(MemoryPackSerializer.Serialize("a fairly long string"));

        Assert.That(ProcedureEnvelope.TryReadMemoryPackObject(written.AsMemory(0, written.Length - 3), out _), Is.False);
    }

    [Test]
    public void AHeaderClaimingMoreLengthsThanTheBodyHolds_IsRejected() {
        Assert.That(ProcedureEnvelope.TryReadMemoryPackObject(new byte[] { 5, 1 }, out _), Is.False);
    }
}
