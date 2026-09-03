using RhinoDB.Core.Results;

namespace RhinoDB.Test.Core.Results;

public class RhinoErrorConfigurationTests
{
    [TearDown]
    public void ResetVerbose()
    {
        RhinoErrorConfiguration.ResetForTests(true);
    }

    [Test]
    public void Configure_CalledASecondTime_Throws()
    {
        RhinoErrorConfiguration.ResetForTests(true);

        RhinoErrorConfiguration.Configure(false);

        Assert.Throws<InvalidOperationException>(() => RhinoErrorConfiguration.Configure(true));
    }

    [Test]
    public void Verbose_True_ReturnsADistinctInstancePerCall()
    {
        RhinoErrorConfiguration.ResetForTests(true);

        var first = RhinoError.DuplicateKey(1);
        var second = RhinoError.DuplicateKey(2);

        Assert.That(second, Is.Not.SameAs(first));
    }

    [Test]
    public void Verbose_True_PreservesTheSuppliedKeyInTheException()
    {
        RhinoErrorConfiguration.ResetForTests(true);

        var error = RhinoError.DuplicateKey(42);

        Assert.That(error.ToException().Message, Does.Contain("42"));
    }

    [Test]
    public void Verbose_False_ReturnsTheSameCachedInstanceEveryCall()
    {
        RhinoErrorConfiguration.ResetForTests(false);

        var first = RhinoError.DuplicateKey(1);
        var second = RhinoError.DuplicateKey(2);

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void Verbose_False_ReturnsTheSameCachedInstanceRegardlessOfKeyType()
    {
        RhinoErrorConfiguration.ResetForTests(false);

        var fromInt = RhinoError.DuplicateKey(1);
        var fromString = RhinoError.DuplicateKey("player@example.com");

        Assert.That(fromString, Is.SameAs(fromInt));
    }

    [Test]
    public void Verbose_False_DiscardsTheSuppliedKey()
    {
        RhinoErrorConfiguration.ResetForTests(false);

        var error = RhinoError.DuplicateKey(42);

        Assert.That(error.ToException().Message, Does.Contain("[null]"));
    }

    [Test]
    public void Verbose_False_DifferentKindsUseDifferentCachedInstances()
    {
        RhinoErrorConfiguration.ResetForTests(false);

        var duplicateKey = RhinoError.DuplicateKey(1);
        var indexKeyNotFound = RhinoError.IndexKeyNotFound(1);

        Assert.That(indexKeyNotFound, Is.Not.SameAs(duplicateKey));
    }
}
