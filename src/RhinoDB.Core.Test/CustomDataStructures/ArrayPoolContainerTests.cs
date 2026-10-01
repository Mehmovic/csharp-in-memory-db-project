namespace RhinoDB.Core.CustomDataStructures.Test;

using RhinoDB.Core.Exceptions;

// ArrayPoolContainer<T> and its builder are the pooled buffer underneath every
// offset list, every index scan result and the undo journal, so their two
// contracts matter more than their line count:
//
//   1. Exactly one owner of the rented array at any moment. Build() transfers
//      ownership and disarms the builder, so a builder disposed after Build is a
//      no-op rather than a double Return to the pool.
//   2. Nothing observable survives Dispose(). Reading a disposed buffer throws
//      rather than handing out a span into memory the pool has recycled - the
//      exact bug class OffsetList.Buffer() had.
public class ArrayPoolContainerTests
{
    // ---- builder: growth and contents ----

    [Test]
    public void Add_BeyondTheInitialCapacity_KeepsEveryValueInOrder()
    {
        // capacity 1 forces the Grow path repeatedly: this is the pool-growth
        // branch, and a copy or length slip here corrupts scan results silently.
        using var builder = ArrayPoolContainerBuilder<int>.Create(capacity: 1);
        const int count = 300;

        for (var i = 0; i < count; i++) Assert.That(builder.Add(i).IsOk(), Is.True);

        var span = builder.BufferResult().Unwrap();
        Assert.That(span.Length, Is.EqualTo(count), "the rented buffer is longer than the count - BufferResult must hand back exactly what was added");
        for (var i = 0; i < count; i++) Assert.That(span[i], Is.EqualTo(i));
    }

    [Test]
    public void Add_WithAReferenceElementType_GrowsAndKeepsTheReferences()
    {
        // Grow returns the old array with clearArray driven by
        // IsReferenceOrContainsReferences<T>; a value-type run would never catch
        // a reference-type mistake.
        using var builder = ArrayPoolContainerBuilder<string>.Create(capacity: 1);
        for (var i = 0; i < 64; i++) Assert.That(builder.Add("v" + i).IsOk(), Is.True);

        var span = builder.BufferResult().Unwrap();
        Assert.That(span.Length, Is.EqualTo(64));
        Assert.That(span[63], Is.EqualTo("v63"));
    }

    [Test]
    public void Add_WithZeroCapacity_StartsFromAnEmptyBufferAndStillGrows()
    {
        // Create(0) takes the [] branch rather than renting, so Grow is entered
        // with array.Length == 0 and has to skip the copy.
        using var builder = ArrayPoolContainerBuilder<int>.Create(capacity: 0);
        Assert.That(builder.Add(7).IsOk(), Is.True);
        Assert.That(builder.Add(8).IsOk(), Is.True);

        var span = builder.BufferResult().Unwrap();
        Assert.That(span.Length, Is.EqualTo(2));
        Assert.That(span[0], Is.EqualTo(7));
        Assert.That(span[1], Is.EqualTo(8));
    }

    [Test]
    public void BufferResult_OnAFreshBuilder_IsEmptyRatherThanAnError()
    {
        using var builder = ArrayPoolContainerBuilder<int>.Create();
        Assert.That(builder.BufferResult().Unwrap().Length, Is.EqualTo(0));
    }

    // ---- builder: ownership transfer ----

    [Test]
    public void Build_HandsTheRentedArrayToTheContainer()
    {
        using var builder = ArrayPoolContainerBuilder<int>.Create();
        Assert.That(builder.Add(11).IsOk(), Is.True);
        Assert.That(builder.Add(22).IsOk(), Is.True);

        var container = builder.Build().Unwrap();
        using (container)
        {
            Assert.That(container.Count, Is.EqualTo(2));
            Assert.That(container.Buffer().ToArray(), Is.EqualTo(new[] { 11, 22 }));
        }
    }

    [Test]
    public void Dispose_AfterBuild_IsANoOpSoTheArrayIsNotReturnedTwice()
    {
        var builder = ArrayPoolContainerBuilder<int>.Create();
        Assert.That(builder.Add(5).IsOk(), Is.True);
        var container = builder.Build().Unwrap();

        // The builder no longer owns the array. Disposing it must be harmless -
        // otherwise Build followed by `using` on the builder returns a live array
        // to the pool while the container is still reading it.
        builder.Dispose();

        using (container) Assert.That(container.Buffer().Length, Is.EqualTo(1));
    }

    [Test]
    public void Build_ThenAnyFurtherBuilderUse_Fails()
    {
        using var builder = ArrayPoolContainerBuilder<int>.Create();
        Assert.That(builder.Add(1).IsOk(), Is.True);
        using var container = builder.Build().Unwrap();

        Assert.That(builder.Add(2).GetError().Kind, Is.EqualTo(ErrorKind.ArrayPoolDisposed));
        Assert.That(builder.BufferResult().GetError().Kind, Is.EqualTo(ErrorKind.ArrayPoolDisposed));
        Assert.That(builder.Build().GetError().Kind, Is.EqualTo(ErrorKind.ArrayPoolDisposed));
    }

    // ---- builder: disposed ----

    [Test]
    public void Dispose_ThenEveryOperation_FailsLoudly()
    {
        var builder = ArrayPoolContainerBuilder<int>.Create();
        builder.Dispose();

        Assert.That(builder.Add(1).GetError().Kind, Is.EqualTo(ErrorKind.ArrayPoolDisposed));
        Assert.That(builder.BufferResult().GetError().Kind, Is.EqualTo(ErrorKind.ArrayPoolDisposed));
        Assert.That(builder.Build().GetError().Kind, Is.EqualTo(ErrorKind.ArrayPoolDisposed));
    }

    [Test]
    public void Dispose_IsIdempotent()
    {
        var builder = ArrayPoolContainerBuilder<int>.Create();
        Assert.That(builder.Add(1).IsOk(), Is.True);
        builder.Dispose();
        Assert.DoesNotThrow(builder.Dispose);
    }

    [Test]
    public void Dispose_OnAnEmptyBuilderThatNeverRented_IsSafe()
    {
        // capacity 0 never rents, so Dispose has an array of length 0 to guard on.
        var builder = ArrayPoolContainerBuilder<int>.Create(capacity: 0);
        Assert.DoesNotThrow(builder.Dispose);
    }

    // ---- container: reading ----

    [Test]
    public void Empty_HoldsNothingAndIsStillDisposable()
    {
        var empty = ArrayPoolContainer<int>.Empty();
        Assert.That(empty.IsEmpty(), Is.True);
        Assert.That(empty.Count, Is.EqualTo(0));
        Assert.That(empty.BufferResult().Unwrap().Length, Is.EqualTo(0));
        empty.Dispose();
    }

    [Test]
    public void Buffer_LengthMatchesCountEvenThoughTheRentWasLonger()
    {
        using var builder = ArrayPoolContainerBuilder<int>.Create(capacity: 64);
        Assert.That(builder.Add(1).IsOk(), Is.True);
        Assert.That(builder.Add(2).IsOk(), Is.True);

        using var container = builder.Build().Unwrap();
        Assert.That(container.Buffer().Length, Is.EqualTo(2),
            "Buffer must not expose the unused tail of the rented array");
    }

    // ---- container: enumerators ----

    [Test]
    public void GetEnumerator_WalksEveryValueOnce()
    {
        using var container = Build(4);

        var seen = new List<int>();
        foreach (var value in container) seen.Add(value);

        Assert.That(seen, Is.EqualTo(new[] { 0, 1, 2, 3 }));
    }

    [Test]
    public void GetEnumerator_OnAnEmptyContainer_YieldsNothing()
    {
        var empty = ArrayPoolContainer<int>.Empty();
        var count = 0;
        foreach (var unused in empty) count++;
        empty.Dispose();
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void GetRefEnumerator_HandsOutReferencesToTheLiveBuffer()
    {
        using var container = Build(3);

        Assert.That(Sum(container.GetRefEnumerator()), Is.EqualTo(0 + 1 + 2));
    }

    [Test]
    public void GetRefEnumerator_CanBeReadTwiceByRewinding()
    {
        using var container = Build(3);

        var first = Sum(container.GetRefEnumerator());
        var second = Sum(container.GetRefEnumerator());

        Assert.That(first, Is.EqualTo(second));
        Assert.That(first, Is.EqualTo(0 + 1 + 2));
    }

    static private int Sum(ArrayPoolContainer<int>.RefEnumerator e)
    {
        var total = 0;
        while (e.MoveNext()) { ref readonly var value = ref e.Current; total += value; }
        return total;
    }

    // ---- container: disposed ----

    [Test]
    public void Dispose_ThenBuffer_ThrowsRatherThanHandingOutRecycledMemory()
    {
        var container = Build(3);
        container.Dispose();

        // A span into an array that has gone back to the pool reads recycled
        // memory - memory-safe but silently wrong. Throwing is the only safe
        // answer, and this is the assertion that keeps it that way.
        Assert.Throws<ArrayPoolDisposedException>(() => { _ = container.Buffer(); });
    }

    [Test]
    public void Dispose_ThenBufferResult_FailsLoudly()
    {
        var container = Build(3);
        container.Dispose();
        Assert.That(container.BufferResult().GetError().Kind, Is.EqualTo(ErrorKind.ArrayPoolDisposed));
    }

    [Test]
    public void ContainerDispose_IsIdempotent()
    {
        var container = Build(3);
        container.Dispose();
        Assert.DoesNotThrow(container.Dispose);
    }

    static private ArrayPoolContainer<int> Build(int count)
    {
        var builder = ArrayPoolContainerBuilder<int>.Create();
        for (var i = 0; i < count; i++) Assert.That(builder.Add(i).IsOk(), Is.True);
        return builder.Build().Unwrap();
    }
}
