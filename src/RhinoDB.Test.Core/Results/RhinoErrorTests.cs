using RhinoDB.Core.Exceptions;
using RhinoDB.Core.Results;

namespace RhinoDB.Test.Core.Results;

public class RhinoErrorTests
{
    [Test]
    public void OffsetOutOfRange_ToException_BuildsTheGeneratedExceptionType()
    {
        var error = RhinoError.OffsetOutOfRange(5);

        var exception = error.ToException();

        Assert.That(exception, Is.InstanceOf<OffsetOutOfRangeException>());
        Assert.That(((OffsetOutOfRangeException)exception).Offset, Is.EqualTo(5));
    }

    [Test]
    public void OffsetOutOfRange_ThroughResult_RoundTripsToTheGeneratedExceptionType()
    {
        Result<int> result = Result<int>.Error(RhinoError.OffsetOutOfRange(5));

        var thrown = Assert.Throws<OffsetOutOfRangeException>(() => result.Unwrap());
        Assert.That(thrown.Offset, Is.EqualTo(5));
    }
}
