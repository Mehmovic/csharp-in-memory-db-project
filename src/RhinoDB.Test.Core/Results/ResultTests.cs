using RhinoDB.Core.Results;

namespace RhinoDB.Test.Core.Results;

public class ResultTests
{
    [Test]
    public void Ok_ReportsSuccess()
    {
        var result = Result.Ok();

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.IsError, Is.False);
    }

    [Test]
    public void Ok_GetException_ThrowsInvalidOperationException()
    {
        var result = Result.Ok();

        Assert.Throws<InvalidOperationException>(() => result.GetException());
    }

    [Test]
    public void Ok_ThrowIfError_DoesNotThrow()
    {
        var result = Result.Ok();

        Assert.DoesNotThrow(() => result.ThrowIfError());
    }

    [Test]
    public void Ok_Match_InvokesTheOnSuccessBranch()
    {
        var result = Result.Ok();

        var outcome = result.Match(() => "success", ex => ex.Message);

        Assert.That(outcome, Is.EqualTo("success"));
    }

    [Test]
    public void Error_ReportsFailure()
    {
        var result = Result.Error(new InvalidOperationException("boom"));

        Assert.That(result.IsError, Is.True);
        Assert.That(result.IsOk(), Is.False);
    }

    [Test]
    public void Error_GetException_ReturnsTheStoredException()
    {
        var exception = new InvalidOperationException("boom");
        var result = Result.Error(exception);

        Assert.That(result.GetException(), Is.SameAs(exception));
    }

    [Test]
    public void Error_ThrowIfError_ThrowsTheStoredException()
    {
        var exception = new InvalidOperationException("boom");
        var result = Result.Error(exception);

        var thrown = Assert.Throws<InvalidOperationException>(() => result.ThrowIfError());
        Assert.That(thrown, Is.SameAs(exception));
    }

    [Test]
    public void Error_Match_InvokesTheOnFailureBranchWithTheException()
    {
        var result = Result.Error(new InvalidOperationException("boom"));

        var outcome = result.Match(() => "success", ex => ex.Message);

        Assert.That(outcome, Is.EqualTo("boom"));
    }

    [Test]
    public void Error_WithNullException_SubstitutesADefaultException()
    {
        var result = Result.Error(null!);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.GetException(), Is.Not.Null);
    }
}
