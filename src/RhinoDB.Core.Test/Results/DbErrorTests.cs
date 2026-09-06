using RhinoDB.Core.Exceptions;
using RhinoDB.Core;

namespace RhinoDB.Test.Core.Results;

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
        Result<int> result = Result<int>.Error(DbError.OffsetOutOfRange());

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
}
