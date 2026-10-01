namespace RhinoDB.Core.Results.Test;

using RhinoDB.Core.Exceptions;

// StackResult and StackResult<T> are the return type of nearly every internal API:
// index scans hand back ReadOnlySpan<T>, the query types hand back rows, and
// PooledOperation hands back operation outcomes. Three things have to hold:
//
//   1. An error can never be unwrapped into a value by accident - Unwrap, Match
//      and the implicit conversions all have to route it to the throw or the
//      fallback, never to a default.
//   2. IsOkOrReverted is the one deliberately fuzzy predicate in the codebase: a
//      reverted operation is an error the database recovered from, so the
//      storage sweep and other cleanups must still run for it.
//   3. Converting a *successful* non-generic result to StackResult<T> throws,
//      because there is no value to carry and a silent default<T> would hand
//      callers a fabricated zero.
//
// Note these are ref structs: a StackResult cannot be captured by a lambda, so
// every assertion that needs one builds it inside the assertion.
public class StackResultTests
{
    // ---- non-generic ----

    [Test]
    public void Ok_IsOkAndCarriesNoError()
    {
        var result = StackResult.Ok();
        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.IsError(), Is.False);
        Assert.That(result.IsOkOrReverted(), Is.True);
    }

    [Test]
    public void Error_IsAnErrorAndCarriesTheDbError()
    {
        Assert.That(StackResult.Error(DbError.PrimaryKeyImmutable()).GetError().Kind,
            Is.EqualTo(ErrorKind.PrimaryKeyImmutable));
    }

    [Test]
    public void GetError_OnASuccessfulResult_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => { _ = StackResult.Ok().GetError(); });
    }

    [Test]
    public void ThrowIfError_OnAnError_ThrowsTheGeneratedException()
    {
        Assert.Throws<PrimaryKeyImmutableException>(
            () => StackResult.Error(DbError.PrimaryKeyImmutable()).ThrowIfError());
    }

    [Test]
    public void ThrowIfError_OnSuccess_DoesNothing()
    {
        Assert.DoesNotThrow(() => StackResult.Ok().ThrowIfError());
    }

    [Test]
    public void Error_FromAnException_KeepsTheCauseAsTheInnerException()
    {
        var boom = new InvalidOperationException("boom");
        var result = StackResult.Error(boom);

        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.SystemFailure));
        Assert.That(result.GetError().ToException(), Is.SameAs(boom),
            "SystemFailure maps straight to the captured exception, so it must survive intact");
    }

    [Test]
    public void Match_TakesTheSuccessOrTheFailureBranch()
    {
        Assert.That(StackResult.Ok().Match(() => "ok", _ => "err"), Is.EqualTo("ok"));
        Assert.That(StackResult.Error(DbError.PrimaryKeyImmutable()).Match(() => "ok", _ => "err"), Is.EqualTo("err"));
    }

    [Test]
    public void IsOkOrReverted_IsTrueForSuccessAndForARevertedApplyOnly()
    {
        Assert.That(StackResult.Ok().IsOkOrReverted(), Is.True);
        Assert.That(StackResult.Error(DbError.ApplyFailedButRevertedSuccessfully()).IsOkOrReverted(), Is.True,
            "a reverted operation is an error the database recovered from - cleanup must still run for it");
        Assert.That(StackResult.Error(DbError.ApplyFailed()).IsOkOrReverted(), Is.False);
        Assert.That(StackResult.Error(DbError.PrimaryKeyImmutable()).IsOkOrReverted(), Is.False);
    }

    [Test]
    public void Ok_OfAValue_ProducesTheGenericForm()
    {
        Assert.That(StackResult.Ok<int>(42).Unwrap(), Is.EqualTo(42));
    }

    // ---- generic ----

    [Test]
    public void GenericOk_HoldsTheValue()
    {
        Assert.That(StackResult<int>.Ok(7).IsOk(), Is.True);
        Assert.That(StackResult<int>.Ok(7).Unwrap(), Is.EqualTo(7));
    }

    [Test]
    public void Ok_WithANullRefValue_Throws()
    {
        // The `value is null` guard: a StackResult<string> reporting success while
        // carrying null would fail much later, at an unrelated call site.
        Assert.Throws<ArgumentNullException>(() => StackResult<string>.Ok(null!));
    }

    [Test]
    public void Unwrap_OnAnError_ThrowsRatherThanReturningDefault()
    {
        Assert.Throws<PrimaryKeyImmutableException>(
            () => { _ = StackResult<int>.Error(DbError.PrimaryKeyImmutable()).Unwrap(); });
    }

    [Test]
    public void TryUnwrap_ReportsSuccessAndFailureWithoutThrowing()
    {
        Assert.That(StackResult<int>.Ok(9).TryUnwrap(out var value), Is.True);
        Assert.That(value, Is.EqualTo(9));

        Assert.That(StackResult<int>.Error(DbError.PrimaryKeyImmutable()).TryUnwrap(out var nothing), Is.False);
        Assert.That(nothing, Is.EqualTo(0), "the out value must be cleared on failure, never left holding a previous call's data");
    }

    [Test]
    public void UnwrapOr_FallsBackOnlyOnError()
    {
        Assert.That(StackResult<int>.Ok(3).UnwrapOr(99), Is.EqualTo(3));
        Assert.That(StackResult<int>.Error(DbError.PrimaryKeyImmutable()).UnwrapOr(99), Is.EqualTo(99));
    }

    [Test]
    public void GenericMatch_PassesTheValueOrTheError()
    {
        Assert.That(StackResult<int>.Ok(4).Match(v => v * 2, _ => -1), Is.EqualTo(8));
        Assert.That(StackResult<int>.Error(DbError.PrimaryKeyImmutable()).Match(v => v * 2, _ => -1), Is.EqualTo(-1));
    }

    [Test]
    public void Void_NarrowsToTheNonGenericFormAndKeepsTheError()
    {
        Assert.That(StackResult<int>.Ok(1).Void().IsOk(), Is.True);

        var narrowed = StackResult<int>.Error(DbError.ApplyFailedButRevertedSuccessfully()).Void();
        Assert.That(narrowed.IsError(), Is.True);
        Assert.That(narrowed.IsOkOrReverted(), Is.True, "Void must not lose the reverted classification");
    }

    [Test]
    public void ImplicitConversion_FromTheValue_ProducesASuccessfulResult()
    {
        StackResult<int> result = 12;
        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(12));
    }

    [Test]
    public void ImplicitConversion_FromAFailedResult_CarriesTheError()
    {
        StackResult<int> result = Result.Error(DbError.PrimaryKeyImmutable());
        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.PrimaryKeyImmutable));
    }

    [Test]
    public void ImplicitConversion_FromASuccessfulResult_ThrowsBecauseThereIsNoValueToCarry()
    {
        // The whole point of this rule: a fabricated default<T> would be
        // indistinguishable from a real zero the caller stored.
        Assert.Throws<InvalidOperationException>(() => { StackResult<int> fromResult = Result.Ok(); _ = fromResult.IsOk(); });
        Assert.Throws<InvalidOperationException>(() => { StackResult<int> fromStack = StackResult.Ok(); _ = fromStack.IsOk(); });
    }

    [Test]
    public void ImplicitConversion_BackToTheNonGenericForm_DropsTheValueAndKeepsTheError()
    {
        StackResult narrowed = StackResult<int>.Error(DbError.PrimaryKeyImmutable());
        Assert.That(narrowed.GetError().Kind, Is.EqualTo(ErrorKind.PrimaryKeyImmutable));
    }

    [Test]
    public void GetError_OnASuccessfulGenericResult_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => { _ = StackResult<int>.Ok(1).GetError(); });
    }

    [Test]
    public void ThrowIfError_OnAGenericError_Throws()
    {
        Assert.Throws<PrimaryKeyImmutableException>(
            () => StackResult<int>.Error(DbError.PrimaryKeyImmutable()).ThrowIfError());
    }

    [Test]
    public void GenericError_FromAnException_KeepsTheCause()
    {
        var boom = new InvalidOperationException("boom");
        var result = StackResult<int>.Error(boom);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.SystemFailure));
        Assert.That(result.GetError().ToException(), Is.SameAs(boom));
    }

    [Test]
    public void ARefStructPayload_SurvivesTheRoundTrip()
    {
        // This is the reason the type exists at all: a span cannot be boxed into
        // Result<T>, so index scans return StackResult<ReadOnlySpan<int>>.
        Span<int> source = [1, 2, 3];
        var ok = StackResult<ReadOnlySpan<int>>.Ok((ReadOnlySpan<int>)source);
        Assert.That(ok.IsOk(), Is.True);
        var payload = ok.Unwrap();
        Assert.That(payload.Length, Is.EqualTo(3));
        Assert.That(payload.ToArray(), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void Error_FromAnException_ThroughTheGenericForm_IsSystemFailure()
    {
        var boom = new InvalidOperationException("boom");
        Assert.That(StackResult<int>.Error(boom).GetError().Kind, Is.EqualTo(ErrorKind.SystemFailure));
    }
}
