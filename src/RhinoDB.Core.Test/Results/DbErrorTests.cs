using RhinoDB.Core.Exceptions;

namespace RhinoDB.Core.Results.Test;

public class DbErrorTests
{
    [Test]
    public void OffsetOutOfRange_ToException_BuildsTheGeneratedExceptionType()
    {
        var error = DbError.OffsetOutOfRange();

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
        var error = DbError.DuplicateKey();

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
        var error = DbError.Custom(7);

        Assert.That(error.Kind, Is.EqualTo(ErrorKind.Custom));
    }

    [Test]
    public void Custom_ExposesTheGivenCode()
    {
        var error = DbError.Custom(12345);

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
        var error = DbError.Custom(3);

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

    // DbError's own half (Kind/CustomCode/Custom/SystemFailure) is hand written rather than
    // emitted, so the properties below are the parts a generator change cannot cover.
    // CustomCode in particular moved here from the generator, which means its contract is
    // now owned by this file and needs pinning directly.
    [Test]
    public void Custom_WithAnInnerException_KeepsItAsTheInnerException()
    {
        var cause = new InvalidOperationException("the real cause");

        var error = DbError.Custom(42, cause);

        Assert.That(error.ToException().InnerException, Is.SameAs(cause));
    }

    [Test]
    public void Custom_ToException_NamesTheCodeSoTheFailureIsDiagnosable()
    {
        var exception = DbError.Custom(4242).ToException();

        Assert.That(exception.Message, Does.Contain("4242"),
            "a synthesized exception has to carry the code or the error is unactionable.");
    }

    [Test]
    public void ALibraryError_LeavesCustomCodeAtZero_ReservingItForTheCustomPath()
    {
        Assert.That(DbError.DuplicateKey().CustomCode, Is.Zero,
            "0 means 'not a custom error', so app codes must not assume it is available.");
    }

    [Test]
    public void SystemFailure_WithNoException_StillProducesSomethingToReport()
    {
        var exception = DbError.SystemFailure(null!).ToException();

        Assert.That(exception, Is.Not.Null);
        Assert.That(exception.Message, Is.Not.Empty,
            "a null exception must not surface as an exception with no message.");
    }

    // ErrorKind.None is 0 so the network pack can read 0 as success. That makes the
    // zero-initialised DbError (default) indistinguishable from 'no error', which is
    // exactly the intent - but it also means every generated kind shifts up by one, so
    // the sentinel is pinned here rather than left implicit.
    [Test]
    public void ErrorKind_NoneIsZero_SoTheWirePackCanReadZeroAsSuccess()
    {
        Assert.That((byte)ErrorKind.None, Is.Zero);
    }

    [Test]
    public void TheDefaultDbError_IsSuccessRatherThanAnError()
    {
        var error = default(DbError);

        Assert.Multiple(() => {
            Assert.That(error.Kind, Is.EqualTo(ErrorKind.None));
            Assert.That(error.CustomCode, Is.Zero);
        });
    }

    [Test]
    public void Is_AgainstADefaultError_DoesNotMatchAnyCustomMember()
    {
        Assert.That(default(DbError).Is(Error.InsufficientFunds), Is.False);
    }
}