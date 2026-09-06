using RhinoDB.Core.Exceptions;
using RhinoDB.Core;

namespace RhinoDB.Test.Core.Results;

public class ResultOfTTests
{
    [Test]
    public void Ok_ReportsSuccess_AndCarriesTheValue()
    {
        var result = Result<int>.Ok(42);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.IsError(), Is.False);
        Assert.That(result.Unwrap(), Is.EqualTo(42));
    }

    [Test]
    public void Ok_WithNullValue_ThrowsArgumentNullException()
    {
        // A "successful" result carrying null is a contradiction: nothing downstream
        // can tell "legitimately null" apart from "caller forgot to produce a value."
        Assert.Throws<ArgumentNullException>(() => Result<string>.Ok(null!));
    }

    [Test]
    public void ImplicitConversion_FromANullValue_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => { Result<string> _ = (string)null!; });
    }

    [Test]
    public void Ok_TryGetValue_ReturnsTrueAndTheValue()
    {
        var result = Result<int>.Ok(42);

        var found = result.TryUnwrap(out var value);

        Assert.That(found, Is.True);
        Assert.That(value, Is.EqualTo(42));
    }

    [Test]
    public void Ok_GetValueOr_ReturnsTheActualValueNotTheFallback()
    {
        var result = Result<int>.Ok(42);

        Assert.That(result.UnwrapOr(-1), Is.EqualTo(42));
    }

    [Test]
    public void Ok_GetException_ThrowsInvalidOperationException()
    {
        var result = Result<int>.Ok(42);

        Assert.Throws<InvalidOperationException>(() => result.GetError().ToException());
    }

    [Test]
    public void Error_ReportsFailure()
    {
        var result = Result<int>.Error(DbError.IndexKeyNotFound());

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.IsOk(), Is.False);
    }

    [Test]
    public void Error_GetValue_ThrowsTheGeneratedExceptionType()
    {
        var result = Result<int>.Error(DbError.IndexKeyNotFound());

        Assert.Throws<IndexKeyNotFoundException>(() => result.Unwrap());
    }

    [Test]
    public void Error_TryGetValue_ReturnsFalseAndTheDefault()
    {
        var result = Result<int>.Error(DbError.IndexKeyNotFound());

        var found = result.TryUnwrap(out var value);

        Assert.That(found, Is.False);
        Assert.That(value, Is.EqualTo(0));
    }

    [Test]
    public void Error_GetValueOr_ReturnsTheFallback()
    {
        var result = Result<int>.Error(DbError.IndexKeyNotFound());

        Assert.That(result.UnwrapOr(-1), Is.EqualTo(-1));
    }

    [Test]
    public void Error_GetException_ReturnsTheGeneratedExceptionType()
    {
        var result = Result<int>.Error(DbError.IndexKeyNotFound());

        Assert.That(result.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Error_ThrowIfError_ThrowsTheGeneratedExceptionType()
    {
        var result = Result<int>.Error(DbError.IndexKeyNotFound());

        Assert.Throws<IndexKeyNotFoundException>(() => result.ThrowIfError());
    }

    [Test]
    public void Match_OnSuccess_InvokesTheOnSuccessBranchWithTheValue()
    {
        var result = Result<int>.Ok(42);

        var outcome = result.Match(value => $"value={value}", err => err.ToException().Message);

        Assert.That(outcome, Is.EqualTo("value=42"));
    }

    [Test]
    public void Match_OnFailure_InvokesTheOnFailureBranchWithTheError()
    {
        var result = Result<int>.Error(DbError.IndexKeyNotFound());

        var outcome = result.Match(value => $"value={value}", err => err.Kind.ToString());

        Assert.That(outcome, Is.EqualTo(nameof(ErrorKind.IndexKeyNotFound)));
    }

    [Test]
    public void ImplicitConversion_FromAPlainValue_CreatesAnOkResult()
    {
        Result<int> result = 42;

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(42));
    }

    [Test]
    public void ImplicitConversion_FromAFailedNonGenericResult_PropagatesTheError()
    {
        Result failure = Result.Error(DbError.IndexKeyNotFound());

        Result<int> result = failure;

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void ImplicitConversion_FromASuccessfulNonGenericResult_ThrowsBecauseThereIsNoValueToCarry()
    {
        Result success = Result.Ok();

        Assert.Throws<InvalidOperationException>(() => { Result<int> _ = success; });
    }

    [Test]
    public void Ok_WithReferenceTypeValue_RoundTrips()
    {
        var result = Result<string>.Ok("hello");

        Assert.That(result.Unwrap(), Is.EqualTo("hello"));
    }
}
