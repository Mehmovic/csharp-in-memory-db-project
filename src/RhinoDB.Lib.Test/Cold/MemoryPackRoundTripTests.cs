using MemoryPack;

namespace RhinoDB.Lib.Cold.Test;

// Account is purpose-built for Stage 5 the same way Player was purpose-built to
// hand-prove Stage 3 (see Docs/03-roadmap.md) - not retrofitted onto an existing
// row type, so this suite can't perturb anything already green. Serialize/deserialize
// correctness only, fully decoupled from libmdbx - no ColdStore involved here at
// all. Reused as-is by Cold/ColdStoreTests.cs, so every Stage 5 test targets the
// same concrete row shape.
[MemoryPackable]
public readonly partial record struct Account(int Id, string Owner, decimal Balance);

public class MemoryPackRoundTripTests {
    [Test]
    public void Serialize_ThenDeserialize_ProducesAnEqualValue() {
        var account = new Account(1, "alice@example.com", 250.75m);

        var bytes = MemoryPackSerializer.Serialize(account);
        var roundTripped = MemoryPackSerializer.Deserialize<Account>(bytes);

        Assert.That(roundTripped, Is.EqualTo(account));
    }

    [Test]
    public void Serialize_TwoDifferentAccounts_ProduceDifferentBytes() {
        var a = new Account(1, "alice@example.com", 250.75m);
        var b = new Account(2, "bob@example.com", 10m);

        var bytesA = MemoryPackSerializer.Serialize(a);
        var bytesB = MemoryPackSerializer.Serialize(b);

        Assert.That(bytesA, Is.Not.EqualTo(bytesB));
    }

    [Test]
    public void Serialize_TheSameValueTwice_ProducesByteIdenticalOutput() {
        // No-version-tags (see Docs/02-architecture.md "Cold storage") means output
        // is a pure function of the value - no timestamp, no per-call nonce, nothing
        // riding along that would make two serializations of an identical value diverge.
        var account = new Account(1, "alice@example.com", 250.75m);

        var first = MemoryPackSerializer.Serialize(account);
        var second = MemoryPackSerializer.Serialize(account);

        Assert.That(first, Is.EqualTo(second));
    }

    [Test]
    public void Serialize_ALongerOwnerString_ProducesProportionallyLongerOutput() {
        // Confirms the format's size tracks actual field content rather than being
        // padded/fixed-width in a way that would hide a version tag inside slack space.
        var shortOwner = new Account(1, "a", 0m);
        var longOwner = new Account(1, new string('a', 200), 0m);

        var shortBytes = MemoryPackSerializer.Serialize(shortOwner);
        var longBytes = MemoryPackSerializer.Serialize(longOwner);

        Assert.That(longBytes.Length, Is.GreaterThan(shortBytes.Length + 190));
    }

    [Test]
    public void Deserialize_BytesFromADifferentAccountValue_NeverEqualsTheOriginal() {
        var original = new Account(1, "alice@example.com", 250.75m);
        var other = new Account(1, "alice@example.com", 250.76m);

        var bytes = MemoryPackSerializer.Serialize(other);
        var roundTripped = MemoryPackSerializer.Deserialize<Account>(bytes);

        Assert.That(roundTripped, Is.Not.EqualTo(original));
    }
}
