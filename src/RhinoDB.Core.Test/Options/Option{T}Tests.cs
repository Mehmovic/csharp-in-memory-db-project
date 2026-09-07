namespace RhinoDB.Core.Options.Test;

public class OptionOfTTests
{
    [Test]
    public void Some_ReportsPresence_AndCarriesTheValue()
    {
        var option = Option<int>.Some(42);

        Assert.That(option.IsSome(), Is.True);
        Assert.That(option.IsNone(), Is.False);
        Assert.That(option.Get(), Is.EqualTo(42));
    }

    [Test]
    public void Some_WithNullValue_ThrowsArgumentNullException()
    {
        // Same rationale as Result<T>.Ok: a "present" value that's actually null
        // is a contradiction. None already means "no value" - Some(null) would
        // just reopen that ambiguity for no benefit.
        Assert.Throws<ArgumentNullException>(() => Option<string>.Some(null!));
    }

    [Test]
    public void ImplicitConversion_FromANullValue_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => { Option<string> _ = (string)null!; });
    }

    [Test]
    public void None_ReportsAbsence()
    {
        var option = Option<int>.None();

        Assert.That(option.IsNone(), Is.True);
        Assert.That(option.IsSome(), Is.False);
    }

    [Test]
    public void None_GetValue_ThrowsInvalidOperationException()
    {
        var option = Option<int>.None();

        Assert.Throws<InvalidOperationException>(() => option.Get());
    }

    [Test]
    public void Some_TryGetValue_ReturnsTrueAndTheValue()
    {
        var option = Option<int>.Some(42);

        var found = option.TryGet(out var value);

        Assert.That(found, Is.True);
        Assert.That(value, Is.EqualTo(42));
    }

    [Test]
    public void None_TryGetValue_ReturnsFalseAndTheDefault()
    {
        var option = Option<int>.None();

        var found = option.TryGet(out var value);

        Assert.That(found, Is.False);
        Assert.That(value, Is.EqualTo(0));
    }

    [Test]
    public void Some_GetValueOr_ReturnsTheActualValueNotTheFallback()
    {
        var option = Option<int>.Some(42);

        Assert.That(option.OrElse(-1), Is.EqualTo(42));
    }

    [Test]
    public void None_GetValueOr_ReturnsTheFallback()
    {
        var option = Option<int>.None();

        Assert.That(option.OrElse(-1), Is.EqualTo(-1));
    }

    [Test]
    public void Match_OnSome_InvokesTheOnSomeBranchWithTheValue()
    {
        var option = Option<int>.Some(42);

        var outcome = option.Match(value => $"value={value}", () => "none");

        Assert.That(outcome, Is.EqualTo("value=42"));
    }

    [Test]
    public void Match_OnNone_InvokesTheOnNoneBranch()
    {
        var option = Option<int>.None();

        var outcome = option.Match(value => $"value={value}", () => "none");

        Assert.That(outcome, Is.EqualTo("none"));
    }

    [Test]
    public void ImplicitConversion_FromAPlainValue_CreatesASomeOption()
    {
        Option<int> option = 42;

        Assert.That(option.IsSome(), Is.True);
        Assert.That(option.Get(), Is.EqualTo(42));
    }

    [Test]
    public void Some_WithReferenceTypeValue_RoundTrips()
    {
        var option = Option<string>.Some("hello");

        Assert.That(option.Get(), Is.EqualTo("hello"));
    }

    [Test]
    public void None_ForReferenceType_TryGetValue_ReturnsFalseAndNull()
    {
        var option = Option<string>.None();

        var found = option.TryGet(out var value);

        Assert.That(found, Is.False);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void ImplicitConversion_FromNonGenericOptionNone_CreatesANoneOfTheTargetType()
    {
        Option<int> option = Option.None();

        Assert.That(option.IsNone(), Is.True);
    }

    [Test]
    public void NonGenericOptionSome_InfersTheTypeArgumentFromTheValue()
    {
        Option<int> option = Option.Some(42);

        Assert.That(option.IsSome(), Is.True);
        Assert.That(option.Get(), Is.EqualTo(42));
    }
}
