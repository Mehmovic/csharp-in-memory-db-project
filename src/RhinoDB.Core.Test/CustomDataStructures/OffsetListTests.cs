namespace RhinoDB.Core.CustomDataStructures.Test;

// Ownership-transfer semantics of OffsetWriterBuilder/OffsetWriter: the rented pool
// array must have exactly one owner at any moment, no code path may return it to
// ArrayPool twice, and a built writer must have no way to be extended.
public class OffsetListTests
{
    [Test]
    public void Add_ThenBuild_WriterHoldsEveryOffsetInOrder()
    {
        using var builder = StackArrayPoolContainerBuilder<int>.Create();
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
        using var builder = StackArrayPoolContainerBuilder<int>.Create(capacity: 4);
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
        using var builder = StackArrayPoolContainerBuilder<int>.Create();
        Assert.That(builder.Add(1).IsOk(), Is.True);

        using var writer = builder.Build().Unwrap();

        Assert.That(builder.Add(2).IsError(), Is.True);
    }

    [Test]
    public void Buffer_AfterBuild_Fails()
    {
        using var builder = StackArrayPoolContainerBuilder<int>.Create();
        using var writer = builder.Build().Unwrap();

        Assert.That(builder.BufferResult().IsError(), Is.True);
    }

    [Test]
    public void Build_Twice_Fails()
    {
        using var builder = StackArrayPoolContainerBuilder<int>.Create();
        using var writer = builder.Build().Unwrap();

        Assert.That(builder.Build().IsError(), Is.True);
    }

    [Test]
    public void BuilderDispose_AfterBuild_IsANoOp_AndWriterStaysUsable()
    {
        var builder = StackArrayPoolContainerBuilder<int>.Create();
        Assert.That(builder.Add(7).IsOk(), Is.True);

        using var writer = builder.Build().Unwrap();
        builder.Dispose(); // must not return the array the writer now owns

        Assert.That(writer.BufferResult().IsOk(), Is.True);
        Assert.That(writer.BufferResult().Unwrap().ToArray(), Is.EqualTo(new[] { 7 }));
    }

    [Test]
    public void BuilderDispose_Twice_IsSafe()
    {
        var builder = StackArrayPoolContainerBuilder<int>.Create();
        builder.Dispose();
        builder.Dispose(); // guarded - the array is returned to the pool exactly once

        Assert.That(builder.Add(1).IsError(), Is.True);
        Assert.That(builder.BufferResult().IsError(), Is.True);
    }

    [Test]
    public void WriterDispose_ReleasesTheArray()
    {
        using var builder = StackArrayPoolContainerBuilder<int>.Create();
        Assert.That(builder.Add(3).IsOk(), Is.True);

        var writer = builder.Build().Unwrap();
        writer.Dispose();

        Assert.That(writer.BufferResult().IsError(), Is.True);

        writer.Dispose(); // guarded - the array is returned to the pool exactly once
    }

    // ---- AddRange: the span-copy path ordered-index scans use for whole chunks ----

    [Test]
    public void AddRange_MixedWithAdd_KeepsEveryOffsetInOrder()
    {
        using var builder = StackArrayPoolContainerBuilder<int>.Create(capacity: 2);
        Assert.That(builder.Add(1).IsOk(), Is.True);
        Assert.That(builder.AddRange([2, 3, 4]).IsOk(), Is.True);
        Assert.That(builder.Add(5).IsOk(), Is.True);

        using var writer = builder.Build().Unwrap();

        Assert.That(writer.Buffer().ToArray(), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public void AddRange_LargerThanDoubleTheCapacity_GrowsStraightToFit()
    {
        // Doubling once would still be too small: Grow must honour the requested
        // minimum, or the CopyTo would overrun the rented buffer.
        using var builder = StackArrayPoolContainerBuilder<int>.Create(capacity: 4);
        Assert.That(builder.Add(-1).IsOk(), Is.True);
        var big = Enumerable.Range(0, 1_000).ToArray();

        Assert.That(builder.AddRange(big).IsOk(), Is.True);

        using var writer = builder.Build().Unwrap();
        Assert.That(writer.Count, Is.EqualTo(1_001));
        Assert.That(writer.Buffer().ToArray(), Is.EqualTo(new[] { -1 }.Concat(big).ToArray()));
    }

    [Test]
    public void AddRange_Empty_IsANoOp()
    {
        using var builder = StackArrayPoolContainerBuilder<int>.Create(capacity: 1);

        Assert.That(builder.AddRange([]).IsOk(), Is.True);

        using var writer = builder.Build().Unwrap();
        Assert.That(writer.Count, Is.EqualTo(0));
    }

    [Test]
    public void AddRange_AfterBuild_Fails()
    {
        using var builder = StackArrayPoolContainerBuilder<int>.Create();
        using var writer = builder.Build().Unwrap();

        Assert.That(builder.AddRange([1, 2]).IsError(), Is.True);
    }

    [Test]
    public void EmptyWriter_HoldsNoOffsets_AndIsDisposable()
    {
        using var writer = StackArrayPoolContainer<int>.Empty();

        Assert.That(writer.Count, Is.EqualTo(0));
        Assert.That(writer.IsEmpty(), Is.True);
        Assert.That(writer.BufferResult().IsOk(), Is.True);
        Assert.That(writer.BufferResult().Unwrap().ToArray(), Is.Empty);
    }
}