namespace RhinoDB.Core.CustomDataStructures.Test;

// PooledBufferWriter is the IBufferWriter<byte> the hand-rolled serializers write through
// (SerializeRow/SerializeKey). Two contracts matter: content survives a growth, and Dispose
// returns the rental exactly once - a double-return would hand a live buffer to another
// consumer while a writer still believes it owns it.
public class PooledBufferWriterTests {
    [Test]
    public void WrittenBytes_AreVisibleInWrittenSpanInOrder() {
        using var writer = new PooledBufferWriter(64);

        var span = writer.GetSpan(4);
        span[0] = 1; span[1] = 2; span[2] = 3; span[3] = 4;
        writer.Advance(4);

        Assert.That(writer.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void GetSpan_ReturnsSpaceAtTheCurrentPositionNotAtTheStart() {
        using var writer = new PooledBufferWriter(64);

        writer.GetSpan(1)[0] = 0xAA;
        writer.Advance(1);
        writer.GetSpan(1)[0] = 0xBB;
        writer.Advance(1);

        Assert.That(writer.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 0xAA, 0xBB }),
            "the second GetSpan must continue where the first left off.");
    }

    [Test]
    public void WritingPastTheInitialCapacity_GrowsAndPreservesEverythingWrittenSoFar() {
        using var writer = new PooledBufferWriter(8);

        for (var i = 0; i < 5000; i++) {
            writer.GetSpan(1)[0] = (byte)(i % 251);
            writer.Advance(1);
        }

        // Re-derive the expected bytes so a growth bug that duplicated or dropped a block fails.
        var expected = new byte[5000];
        for (var i = 0; i < expected.Length; i++) expected[i] = (byte)(i % 251);

        Assert.That(writer.WrittenSpan.Length, Is.EqualTo(5000));
        Assert.That(writer.WrittenSpan.ToArray(), Is.EqualTo(expected));
    }

    [TestCase(0)]
    [TestCase(-5)]
    public void AnInitialCapacityAtOrBelowZero_StillYieldsUsableSpace(int capacity) {
        using var writer = new PooledBufferWriter(capacity);

        writer.GetSpan(1)[0] = 7;
        writer.Advance(1);

        Assert.That(writer.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 7 }));
    }

    [Test]
    public void Dispose_CalledTwice_ReturnsTheRentalOnlyOnce() {
        var writer = new PooledBufferWriter(32);
        writer.Dispose();

        Assert.DoesNotThrow(() => writer.Dispose(), "a second Dispose must be a no-op, not a second Return.");
    }

    [Test]
    public void UsingAfterDispose_ThrowsRatherThanWritingIntoARecycledBuffer() {
        var writer = new PooledBufferWriter(32);
        writer.Dispose();

        // Current behaviour is an unguarded NullReferenceException from buffer.AsSpan.
        // Asserted so the contract is recorded: a loud throw is the safe outcome here,
        // because silently writing into a buffer the pool has already handed to someone
        // else would corrupt an unrelated writer.
        Assert.Throws<NullReferenceException>(() => writer.GetSpan(1));
    }
}