using RhinoDB.Core.Results;

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

        Assert.Throws<InvalidOperationException>(() => result.GetException());
    }

    [Test]
    public void Error_ReportsFailure()
    {
        var result = Result<int>.Error(new KeyNotFoundException("missing"));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.IsOk(), Is.False);
    }

    [Test]
    public void Error_GetValue_ThrowsTheStoredException()
    {
        var exception = new KeyNotFoundException("missing");
        var result = Result<int>.Error(exception);

        var thrown = Assert.Throws<KeyNotFoundException>(() => result.Unwrap());
        Assert.That(thrown, Is.SameAs(exception));
    }

    [Test]
    public void Error_TryGetValue_ReturnsFalseAndTheDefault()
    {
        var result = Result<int>.Error(new KeyNotFoundException("missing"));

        var found = result.TryUnwrap(out var value);

        Assert.That(found, Is.False);
        Assert.That(value, Is.EqualTo(0));
    }

    [Test]
    public void Error_GetValueOr_ReturnsTheFallback()
    {
        var result = Result<int>.Error(new KeyNotFoundException("missing"));

        Assert.That(result.UnwrapOr(-1), Is.EqualTo(-1));
    }

    [Test]
    public void Error_GetException_ReturnsTheStoredException()
    {
        var exception = new KeyNotFoundException("missing");
        var result = Result<int>.Error(exception);

        Assert.That(result.GetException(), Is.SameAs(exception));
    }

    [Test]
    public void Error_ThrowIfError_ThrowsTheStoredException()
    {
        var exception = new KeyNotFoundException("missing");
        var result = Result<int>.Error(exception);

        var thrown = Assert.Throws<KeyNotFoundException>(() => result.ThrowIfError());
        Assert.That(thrown, Is.SameAs(exception));
    }

    [Test]
    public void Match_OnSuccess_InvokesTheOnSuccessBranchWithTheValue()
    {
        var result = Result<int>.Ok(42);

        var outcome = result.Match(value => $"value={value}", ex => ex.Message);

        Assert.That(outcome, Is.EqualTo("value=42"));
    }

    [Test]
    public void Match_OnFailure_InvokesTheOnFailureBranchWithTheException()
    {
        var result = Result<int>.Error(new KeyNotFoundException("missing"));

        var outcome = result.Match(value => $"value={value}", ex => ex.Message);

        Assert.That(outcome, Is.EqualTo("missing"));
    }

    [Test]
    public void ImplicitConversion_FromAPlainValue_CreatesAnOkResult()
    {
        Result<int> result = 42;

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(42));
    }

    [Test]
    public void ImplicitConversion_FromAFailedNonGenericResult_PropagatesTheException()
    {
        var exception = new KeyNotFoundException("missing");
        Result failure = Result.Error(exception);

        Result<int> result = failure;

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.SameAs(exception));
    }

    [Test]
    public void ImplicitConversion_FromASuccessfulNonGenericResult_ThrowsBecauseThereIsNoValueToCarry()
    {
        Result success = Result.Ok();

        Assert.Throws<InvalidOperationException>(() => { Result<int> _ = success; });
    }

    [Test]
    public void Error_WithNullException_SubstitutesADefaultException()
    {
        var result = Result<int>.Error(null!);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.Not.Null);
    }

    [Test]
    public void Ok_WithReferenceTypeValue_RoundTrips()
    {
        var result = Result<string>.Ok("hello");

        Assert.That(result.Unwrap(), Is.EqualTo("hello"));
    }
}
