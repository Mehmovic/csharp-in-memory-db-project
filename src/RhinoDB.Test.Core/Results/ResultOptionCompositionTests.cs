using RhinoDB.Core.Options;
using RhinoDB.Core.Results;

namespace RhinoDB.Test.Core.Results;

public class ResultOptionCompositionTests
{
    // Shaped like a future nullable-column lookup: three distinct outcomes instead
    // of the usual two (found / not found / operation failed).
    static private Result<Option<string>> Lookup(Dictionary<int, string?> data, int key, bool simulateFailure)
    {
        if (simulateFailure) return Result.Error(new InvalidOperationException("storage unavailable"));

        if (!data.TryGetValue(key, out var value)) return Option<string>.None();
        return value is null ? Option<string>.None() : Option<string>.Some(value);
    }

    [Test]
    public void Lookup_Found_ReturnsOkWithSome()
    {
        var data = new Dictionary<int, string?> { [1] = "Alice" };

        var result = Lookup(data, 1, simulateFailure: false);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap().IsSome(), Is.True);
        Assert.That(result.Unwrap().Get(), Is.EqualTo("Alice"));
    }

    [Test]
    public void Lookup_NotFound_ReturnsOkWithNone()
    {
        var data = new Dictionary<int, string?>();

        var result = Lookup(data, 999, simulateFailure: false);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap().IsNone(), Is.True);
    }

    [Test]
    public void Lookup_NullColumnValue_ReturnsOkWithNone_NotAFailure()
    {
        // A row that exists with a SQL-NULL column is a successful lookup with
        // nothing found - not an error. That's the whole point of the Result<Option<T>>
        // shape: null-column and missing-row both collapse to Ok(None), while a real
        // operational failure stays a distinct Error.
        var data = new Dictionary<int, string?> { [1] = null };

        var result = Lookup(data, 1, simulateFailure: false);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap().IsNone(), Is.True);
    }

    [Test]
    public void Lookup_OperationFailure_ReturnsError_RegardlessOfWhatDataWouldHaveMatched()
    {
        var data = new Dictionary<int, string?> { [1] = "Alice" };

        var result = Lookup(data, 1, simulateFailure: true);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void ImplicitConversion_FromNoneOption_SucceedsAsOk_BecauseOptionIsANonNullableStruct()
    {
        // Result<T>.Ok guards against a null T, but Option<T> is a struct - None()
        // is a real, non-null instance, so it flows through the T -> Result<T>
        // implicit conversion without tripping that guard.
        Result<Option<int>> result = Option<int>.None();

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap().IsNone(), Is.True);
    }

    [Test]
    public void Match_ChainedThroughBothTypes_HandlesAllThreeOutcomes()
    {
        Result<Option<int>> found = Option.Some(42);
        Result<Option<int>> notFound = Option<int>.None();
        Result<Option<int>> failed = Result.Error(new InvalidOperationException("boom"));

        string Describe(Result<Option<int>> result) =>
            result.Match(
                onSuccess: option => option.Match(
                    onSome: value => $"found:{value}",
                    onNone: () => "not-found"),
                onFailure: ex => $"error:{ex.Message}");

        Assert.That(Describe(found), Is.EqualTo("found:42"));
        Assert.That(Describe(notFound), Is.EqualTo("not-found"));
        Assert.That(Describe(failed), Is.EqualTo("error:boom"));
    }
}
