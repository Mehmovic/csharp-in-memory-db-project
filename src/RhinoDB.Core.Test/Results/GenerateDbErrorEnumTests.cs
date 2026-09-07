using RhinoDB.Core.Exceptions;

namespace RhinoDB.Core.Results.Test;

// Stands in for a real consuming app's own error enum - proves the
// [GenerateDbError] enum path end to end (this project references
// RhinoDB.Generators as an Analyzer just like a real app would).
// The companion class is always named Err, regardless of the enum's own
// name - a real app is expected to have one such enum per namespace, so a
// single predictable Err.SomeThing() reads better than deriving a name
// from the enum (falls back to Errors only if the enum itself is named Err).
[GenerateDbError]
public enum Error {
    InsufficientFunds,
    PlayerNotEligible,
}

public class GenerateDbErrorEnumTests {
    [Test]
    public void GeneratedFactory_ProducesACustomKindError() {
        DbError error = Err.InsufficientFunds();

        Assert.That(error.Kind, Is.EqualTo(ErrorKind.Custom));
    }

    [Test]
    public void GeneratedFactory_DifferentMembers_ProduceDifferentCodes() {
        Assert.That(
            Err.InsufficientFunds().CustomCode,
            Is.Not.EqualTo(Err.PlayerNotEligible().CustomCode));
    }

    [Test]
    public void Is_MatchesTheCorrespondingEnumMember() {
        DbError error = Err.PlayerNotEligible();

        Assert.That(error.Is(Error.PlayerNotEligible), Is.True);
        Assert.That(error.Is(Error.InsufficientFunds), Is.False);
    }

    [Test]
    public void Is_DoesNotMatchALibraryError() {
        DbError error = DbError.DuplicateKey();

        Assert.That(error.Is(Error.InsufficientFunds), Is.False);
    }

    [Test]
    public void GeneratedFactory_ThroughResult_RoundTrips() {
        var result = Result<int>.Error(Err.InsufficientFunds());

        Assert.That(result.GetError().Is(Error.InsufficientFunds), Is.True);
    }
}
