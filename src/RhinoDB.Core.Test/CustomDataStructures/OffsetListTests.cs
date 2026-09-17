namespace RhinoDB.Core.CustomDataStructures.Test;

// Ownership-transfer semantics of OffsetWriterBuilder/OffsetWriter: the rented pool
// array must have exactly one owner at any moment, no code path may return it to
// ArrayPool twice, and a built writer must have no way to be extended.
public class OffsetListTests
{
    [Test]
    public void Add_ThenBuild_WriterHoldsEveryOffsetInOrder()
    {
        using var builder = OffsetListBuilder.Create();
        for (var i = 0; i < 5; i++)
        {
            Assert.That(builder.Add(i * 10).IsOk(), Is.True);
        }

        using var writer = builder.Build().Unwrap();

        Assert.That(writer.Count, Is.EqualTo(5));
        Assert.That(writer.IsEmpty(), Is.False);
        Assert.That(writer.Buffer().ToArray(), Is.EqualTo(new[] { 0, 10, 20, 30, 40 }));
    }

    [Test]
    public void Add_PastInitialCapacity_GrowsWithoutLosingOffsets()
    {
        using var builder = OffsetListBuilder.Create(capacity: 4);
        for (var i = 0; i < 10_000; i++)
        {
            Assert.That(builder.Add(i).IsOk(), Is.True);
        }

        using var writer = builder.Build().Unwrap();

        Assert.That(writer.Count, Is.EqualTo(10_000));
        Assert.That(writer.Buffer().ToArray(), Is.EqualTo(Enumerable.Range(0, 10_000).ToArray()));
    }

    [Test]
    public void Add_AfterBuild_Fails()
    {
        using var builder = OffsetListBuilder.Create();
        Assert.That(builder.Add(1).IsOk(), Is.True);

        using var writer = builder.Build().Unwrap();

        Assert.That(builder.Add(2).IsError(), Is.True);
    }

    [Test]
    public void Buffer_AfterBuild_Fails()
    {
        using var builder = OffsetListBuilder.Create();
        using var writer = builder.Build().Unwrap();

        Assert.That(builder.BufferResult().IsError(), Is.True);
    }

    [Test]
    public void Build_Twice_Fails()
    {
        using var builder = OffsetListBuilder.Create();
        using var writer = builder.Build().Unwrap();

        Assert.That(builder.Build().IsError(), Is.True);
    }

    [Test]
    public void BuilderDispose_AfterBuild_IsANoOp_AndWriterStaysUsable()
    {
        var builder = OffsetListBuilder.Create();
        Assert.That(builder.Add(7).IsOk(), Is.True);

        using var writer = builder.Build().Unwrap();
        builder.Dispose(); // must not return the array the writer now owns

        Assert.That(writer.BufferResult().IsOk(), Is.True);
        Assert.That(writer.BufferResult().Unwrap().ToArray(), Is.EqualTo(new[] { 7 }));
    }

    [Test]
    public void BuilderDispose_Twice_IsSafe()
    {
        var builder = OffsetListBuilder.Create();
        builder.Dispose();
        builder.Dispose(); // guarded - the array is returned to the pool exactly once

        Assert.That(builder.Add(1).IsError(), Is.True);
        Assert.That(builder.BufferResult().IsError(), Is.True);
    }

    [Test]
    public void WriterDispose_ReleasesTheArray()
    {
        using var builder = OffsetListBuilder.Create();
        Assert.That(builder.Add(3).IsOk(), Is.True);

        var writer = builder.Build().Unwrap();
        writer.Dispose();

        Assert.That(writer.BufferResult().IsError(), Is.True);

        writer.Dispose(); // guarded - the array is returned to the pool exactly once
    }

    [Test]
    public void EmptyWriter_HoldsNoOffsets_AndIsDisposable()
    {
        using var writer = OffsetList.Empty();

        Assert.That(writer.Count, Is.EqualTo(0));
        Assert.That(writer.IsEmpty(), Is.True);
        Assert.That(writer.BufferResult().IsOk(), Is.True);
        Assert.That(writer.BufferResult().Unwrap().ToArray(), Is.Empty);
    }
}