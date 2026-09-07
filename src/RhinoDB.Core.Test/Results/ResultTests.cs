using RhinoDB.Core.Exceptions;

namespace RhinoDB.Core.Results.Test;

public class ResultTests
{
    [Test]
    public void Ok_ReportsSuccess()
    {
        Result result = Result.Ok();

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.IsError, Is.False);
    }

    [Test]
    public void Ok_GetException_ThrowsInvalidOperationException()
    {
        Result result = Result.Ok();

        Assert.Throws<InvalidOperationException>(() => result.GetError().ToException());
    }

    [Test]
    public void Ok_ThrowIfError_DoesNotThrow()
    {
        Result result = Result.Ok();

        Assert.DoesNotThrow(() => result.ThrowIfError());
    }

    [Test]
    public void Ok_Match_InvokesTheOnSuccessBranch()
    {
        Result result = Result.Ok();

        var outcome = result.Match(() => "success", err => err.ToException().Message);

        Assert.That(outcome, Is.EqualTo("success"));
    }

    [Test]
    public void Error_ReportsFailure()
    {
        Result result = Result.Error(DbError.IndexKeyNotFound());

        Assert.That(result.IsError, Is.True);
        Assert.That(result.IsOk(), Is.False);
    }

    [Test]
    public void Error_GetException_ReturnsTheGeneratedExceptionType()
    {
        Result result = Result.Error(DbError.IndexKeyNotFound());

        Assert.That(result.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Error_ThrowIfError_ThrowsTheGeneratedExceptionType()
    {
        Result result = Result.Error(DbError.IndexKeyNotFound());

        Assert.Throws<IndexKeyNotFoundException>(() => result.ThrowIfError());
    }

    [Test]
    public void Error_Match_InvokesTheOnFailureBranchWithTheError()
    {
        Result result = Result.Error(DbError.IndexKeyNotFound());

        var outcome = result.Match(() => "success", err => err.Kind.ToString());

        Assert.That(outcome, Is.EqualTo(nameof(ErrorKind.IndexKeyNotFound)));
    }
}
