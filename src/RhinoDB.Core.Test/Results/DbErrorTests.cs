using RhinoDB.Core.Exceptions;

namespace RhinoDB.Core.Results.Test;

public class DbErrorTests
{
    [Test]
    public void OffsetOutOfRange_ToException_BuildsTheGeneratedExceptionType()
    {
        DbError error = DbError.OffsetOutOfRange();

        var exception = error.ToException();

        Assert.That(exception, Is.InstanceOf<OffsetOutOfRangeException>());
    }

    [Test]
    public void OffsetOutOfRange_ThroughResult_RoundTripsToTheGeneratedExceptionType()
    {
        var result = Result<int>.Error(DbError.OffsetOutOfRange());

        Assert.Throws<OffsetOutOfRangeException>(() => result.Unwrap());
    }

    [Test]
    public void Kind_IsExposedOnTheError()
    {
        DbError error = DbError.DuplicateKey();

        Assert.That(error.Kind, Is.EqualTo(ErrorKind.DuplicateKey));
    }

    [Test]
    public void DifferentKinds_ProduceDifferentExceptionTypes()
    {
        Assert.That(DbError.DuplicateKey().ToException(), Is.InstanceOf<DuplicateKeyException>());
        Assert.That(DbError.IndexKeyNotFound().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
        Assert.That(DbError.PrimaryKeyImmutable().ToException(), Is.InstanceOf<PrimaryKeyImmutableException>());
    }

    // Custom is the app-extensibility escape hatch - a game (or any consumer) needs to
    // report its own error kinds through the exact same Result<T>/DbError vocabulary
    // without RhinoDB.Core knowing about them ahead of time. No exception type is
    // required - a ushort code is enough to identify which app-defined error this is;
    // ToException() synthesizes a plain Exception on demand instead of storing one.
    [Test]
    public void Custom_KindIsSetToCustom()
    {
        DbError error = DbError.Custom(7);

        Assert.That(error.Kind, Is.EqualTo(ErrorKind.Custom));
    }

    [Test]
    public void Custom_ExposesTheGivenCode()
    {
        DbError error = DbError.Custom(12345);

        Assert.That(error.CustomCode, Is.EqualTo((ushort)12345));
    }

    [Test]
    public void Custom_DifferentCodes_AreDistinguishable()
    {
        Assert.That(DbError.Custom(1).CustomCode, Is.Not.EqualTo(DbError.Custom(2).CustomCode));
    }

    [Test]
    public void Custom_ToException_SynthesizesAnExceptionWithoutRequiringOneUpFront()
    {
        DbError error = DbError.Custom(3);

        var exception = error.ToException();

        Assert.That(exception, Is.Not.Null);
    }

    [Test]
    public void Custom_ThroughResult_RoundTripsAndThrowsOnUnwrap()
    {
        var result = Result<int>.Error(DbError.Custom(99));

        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.Custom));
        Assert.Throws<Exception>(() => result.Unwrap());
    }
}
