using RhinoDB.Core.Exceptions;

namespace RhinoDB.Core.Results.Test;

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

        Assert.Throws<InvalidOperationException>(() => result.GetError().ToException());
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

        var outcome = result.Match(() => "success", err => err.ToException().Message);

        Assert.That(outcome, Is.EqualTo("success"));
    }

    [Test]
    public void Error_ReportsFailure()
    {
        var result = Result.Error(DbError.IndexKeyNotFound());

        Assert.That(result.IsError, Is.True);
        Assert.That(result.IsOk(), Is.False);
    }

    [Test]
    public void Error_GetException_ReturnsTheGeneratedExceptionType()
    {
        var result = Result.Error(DbError.IndexKeyNotFound());

        Assert.That(result.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Error_ThrowIfError_ThrowsTheGeneratedExceptionType()
    {
        var result = Result.Error(DbError.IndexKeyNotFound());

        Assert.Throws<IndexKeyNotFoundException>(() => result.ThrowIfError());
    }

    [Test]
    public void Error_Match_InvokesTheOnFailureBranchWithTheError()
    {
        var result = Result.Error(DbError.IndexKeyNotFound());

        var outcome = result.Match(() => "success", err => err.Kind.ToString());

        Assert.That(outcome, Is.EqualTo(nameof(ErrorKind.IndexKeyNotFound)));
    }
}
