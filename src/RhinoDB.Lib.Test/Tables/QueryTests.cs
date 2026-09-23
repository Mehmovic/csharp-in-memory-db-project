using RhinoDB.Core;
using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Storage;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Lib.Test.Tables;

public class QueryTests {
    private readonly record struct TestRow(int Id, string Name);

    private readonly struct RecordingMutator(List<(TestRow Original, TestRow NewRow)> updates, List<TestRow> deletes)
        : IRowMutator<TestRow> {
        public void Update(TestRow original, TestRow newRow) => updates.Add((original, newRow));
        public void Delete(TestRow row) => deletes.Add(row);
        public TestRow WithSamePrimaryKey(TestRow original, TestRow newRow) => newRow with { Id = original.Id };
    }

    static private DenseArray<TestRow> NewStorageWith(params TestRow[] rows) {
        var storage = new DenseArray<TestRow>(chunkSize: 16);
        foreach (var row in rows) storage.Insert(row);
        return storage;
    }

    static private StackArrayPoolContainer<int> OffsetsOf(params int[] offsets) {
        using var builder = StackArrayPoolContainerBuilder<int>.Create(offsets.Length);
        foreach (var offset in offsets) builder.Add(offset);
        return builder.Build().Unwrap();
    }

    // ---- QuerySet ----

    [Test]
    public void Get_ReturnsEveryMatchedRowInOffsetOrder() {
        var storage = NewStorageWith(new TestRow(1, "Ada"), new TestRow(2, "Bob"), new TestRow(3, "Cy"));
        var updates = new List<(TestRow, TestRow)>();
        var deletes = new List<TestRow>();
        var mutator = new RecordingMutator(updates, deletes);
        using var set = new QuerySet<TestRow, RecordingMutator>(storage, OffsetsOf(2, 0), ref mutator);

        var rows = set.Get().Unwrap();

        Assert.That(rows.Length, Is.EqualTo(2));
        Assert.That(rows[0], Is.EqualTo(new TestRow(3, "Cy")));
        Assert.That(rows[1], Is.EqualTo(new TestRow(1, "Ada")));
    }

    [Test]
    public void Get_WithNoMatches_ReturnsAnEmptySpan() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var mutator = new RecordingMutator([], []);
        using var set = new QuerySet<TestRow, RecordingMutator>(storage, OffsetsOf(), ref mutator);

        Assert.That(set.Get().Unwrap().Length, Is.EqualTo(0));
    }

    [Test]
    public void Get_AfterDispose_ReturnsError() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var mutator = new RecordingMutator([], []);
        var set = new QuerySet<TestRow, RecordingMutator>(storage, OffsetsOf(0), ref mutator);
        set.Dispose();

        Assert.That(set.Get().IsError(), Is.True);
    }

    [Test]
    public void Dispose_CalledTwice_IsSafe() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var mutator = new RecordingMutator([], []);
        var set = new QuerySet<TestRow, RecordingMutator>(storage, OffsetsOf(0), ref mutator);

        set.Dispose();
        set.Dispose();
    }

    [Test]
    public void Update_Blanket_PreservesEachRowsOwnPrimaryKeyAcrossAMultiRowMatch() {
        // The one newRow value is stamped onto every matched row, but each row's own primary
        // key must survive - otherwise every row after the first would fail Validate()'s
        // PrimaryKeyImmutable check, since a real table's Update(id, newRow) requires
        // newRow's PK to equal id.
        var storage = NewStorageWith(new TestRow(1, "Ada"), new TestRow(2, "Bob"));
        var updates = new List<(TestRow Original, TestRow NewRow)>();
        var mutator = new RecordingMutator(updates, []);
        using var set = new QuerySet<TestRow, RecordingMutator>(storage, OffsetsOf(0, 1), ref mutator);

        var affected = set.ExecuteUpdate(new TestRow(-1, "Stamped"));

        Assert.That(affected.IsOk(), Is.True);
        Assert.That(updates, Is.EquivalentTo(new[] {
            (new TestRow(1, "Ada"), new TestRow(1, "Stamped")),
            (new TestRow(2, "Bob"), new TestRow(2, "Stamped")),
        }));
    }

    [Test]
    public void Update_Mutator_TransformsEachRowIndependently() {
        var storage = NewStorageWith(new TestRow(1, "Ada"), new TestRow(2, "Bob"));
        var updates = new List<(TestRow Original, TestRow NewRow)>();
        var mutator = new RecordingMutator(updates, []);
        using var set = new QuerySet<TestRow, RecordingMutator>(storage, OffsetsOf(0, 1), ref mutator);

        var affected = set.ExecuteUpdate(row => row with { Name = row.Name + "!" });

        Assert.That(affected.IsOk(), Is.True);
        Assert.That(updates, Is.EquivalentTo(new[] {
            (new TestRow(1, "Ada"), new TestRow(1, "Ada!")),
            (new TestRow(2, "Bob"), new TestRow(2, "Bob!")),
        }));
    }

    [Test]
    public void Delete_CallsMutatorForEveryMatchedRow() {
        var storage = NewStorageWith(new TestRow(1, "Ada"), new TestRow(2, "Bob"));
        var deletes = new List<TestRow>();
        var mutator = new RecordingMutator([], deletes);
        using var set = new QuerySet<TestRow, RecordingMutator>(storage, OffsetsOf(0, 1), ref mutator);

        var affected = set.ExecuteDelete();

        Assert.That(affected.IsOk(), Is.True);
        Assert.That(deletes, Is.EquivalentTo(new[] { new TestRow(1, "Ada"), new TestRow(2, "Bob") }));
    }

    // ---- QuerySingle ----

    [Test]
    public void Get_WithAResolvedOffset_ReturnsTheRow() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var mutator = new RecordingMutator([], []);
        var single = new QuerySingle<TestRow, RecordingMutator>(storage, 0, ref mutator);

        Assert.That(single.Get().Unwrap(), Is.EqualTo(new TestRow(1, "Ada")));
    }

    [Test]
    public void Get_WithAnErrorOffset_PropagatesTheError() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var mutator = new RecordingMutator([], []);
        var single = new QuerySingle<TestRow, RecordingMutator>(storage, Result<int>.Error(DbError.IndexKeyNotFound()), ref mutator);

        Assert.That(single.Get().IsError(), Is.True);
    }

    [Test]
    public void Update_Blanket_CallsMutatorOnceWithTheGivenValue() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var updates = new List<(TestRow Original, TestRow NewRow)>();
        var mutator = new RecordingMutator(updates, []);
        var single = new QuerySingle<TestRow, RecordingMutator>(storage, 0, ref mutator);

        var affected = single.ExecuteUpdate(new TestRow(1, "Adaline"));

        Assert.That(affected.IsOk(), Is.True);
        Assert.That(updates, Is.EqualTo(new[] { (new TestRow(1, "Ada"), new TestRow(1, "Adaline")) }));
    }

    [Test]
    public void Update_OnAnErrorOffset_PropagatesTheErrorAndCallsNothing() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var updates = new List<(TestRow, TestRow)>();
        var mutator = new RecordingMutator(updates, []);
        var single = new QuerySingle<TestRow, RecordingMutator>(storage, Result<int>.Error(DbError.IndexKeyNotFound()), ref mutator);

        Assert.That(single.ExecuteUpdate(new TestRow(1, "Adaline")).IsError(), Is.True);
        Assert.That(updates, Is.Empty);
    }

    [Test]
    public void Delete_WithAResolvedOffset_CallsMutatorOnce() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var deletes = new List<TestRow>();
        var mutator = new RecordingMutator([], deletes);
        var single = new QuerySingle<TestRow, RecordingMutator>(storage, 0, ref mutator);

        var affected = single.ExecuteDelete();

        Assert.That(affected.IsOk(), Is.True);
        Assert.That(deletes, Is.EqualTo(new[] { new TestRow(1, "Ada") }));
    }
    // ---- Zero-copy reads (EnumerateRef) ----

    [Test]
    public void EnumerateRef_ReturnsEveryMatchedRowInOffsetOrder_WithoutCopying() {
        var storage = NewStorageWith(new TestRow(1, "Ada"), new TestRow(2, "Bob"), new TestRow(3, "Cy"));
        var mutator = new RecordingMutator([], []);
        using var set = new QuerySet<TestRow, RecordingMutator>(storage, OffsetsOf(2, 0), ref mutator);

        var rows = new List<TestRow>();
        foreach (ref readonly var row in set.GetRefEnumerator().Unwrap()) {
            rows.Add(row);
        }

        Assert.That(rows, Is.EqualTo(new[] { new TestRow(3, "Cy"), new TestRow(1, "Ada") }));
    }

    [Test]
    public void EnumerateRef_IsALiveView_ReflectsALaterStorageMutation() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var mutator = new RecordingMutator([], []);
        using var set = new QuerySet<TestRow, RecordingMutator>(storage, OffsetsOf(0), ref mutator);

        var enumerator = set.GetRefEnumerator().Unwrap();
        Assert.That(enumerator.MoveNext(), Is.True);
        ref readonly var view = ref enumerator.Current;
        Assert.That(view.Name, Is.EqualTo("Ada"));

        // No buffer is involved, so the view tracks storage directly.
        storage.Set(0, new TestRow(1, "Ada Lovelace"));
        Assert.That(view.Name, Is.EqualTo("Ada Lovelace"));
    }

    [Test]
    public void EnumerateRef_WithNoMatches_YieldsNothing() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var mutator = new RecordingMutator([], []);
        using var set = new QuerySet<TestRow, RecordingMutator>(storage, OffsetsOf(), ref mutator);

        var enumerator = set.GetRefEnumerator().Unwrap();

        Assert.That(enumerator.Count, Is.EqualTo(0));
        Assert.That(enumerator.MoveNext(), Is.False);
    }

    [Test]
    public void EnumerateRefResult_WhenDisposed_ReturnsQuerySetDisposedError() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var mutator = new RecordingMutator([], []);
        var set = new QuerySet<TestRow, RecordingMutator>(storage, OffsetsOf(0), ref mutator);
        set.Dispose();

        var result = set.GetRefEnumerator();

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.QuerySetDisposed));
    }

    [Test]
    public void EnumerateRefResult_WhenLive_UnwrapsToAWalkableEnumerator() {
        var storage = NewStorageWith(new TestRow(1, "Ada"), new TestRow(2, "Bob"));
        var mutator = new RecordingMutator([], []);
        using var set = new QuerySet<TestRow, RecordingMutator>(storage, OffsetsOf(1), ref mutator);

        var rows = new List<TestRow>();
        var enumerator = set.GetRefEnumerator().Unwrap();
        while (enumerator.MoveNext()) rows.Add(enumerator.Current);

        Assert.That(rows, Is.EqualTo(new[] { new TestRow(2, "Bob") }));
    }
    // ---- Zero-copy single-row reads (QuerySingle) ----

    [Test]
    public void SingleGetRef_ReturnsTheLiveRowWithoutCopying() {
        var storage = NewStorageWith(new TestRow(1, "Ada"), new TestRow(2, "Bob"));
        var mutator = new RecordingMutator([], []);
        var single = new QuerySingle<TestRow, RecordingMutator>(storage, 1, ref mutator);

        Assert.That(single.HasRow(), Is.True);
        ref readonly var view = ref single.GetRef();
        Assert.That(view, Is.EqualTo(new TestRow(2, "Bob")));

        // Live view: nothing was copied, so a later mutation on the same slot is
        // visible through the reference that was handed out earlier.
        storage.Set(1, new TestRow(2, "Bob Marley"));
        Assert.That(view.Name, Is.EqualTo("Bob Marley"));
    }

    [Test]
    public void SingleHasRow_IsFalseWhenTheOffsetLookupFailed() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var mutator = new RecordingMutator([], []);
        var missing = Result<int>.Error(DbError.IndexKeyNotFound());
        var single = new QuerySingle<TestRow, RecordingMutator>(storage, missing, ref mutator);

        Assert.That(single.HasRow(), Is.False);
    }

    [Test]
    public void SingleGetRef_WithoutARow_ThrowsInsteadOfReturningGarbage() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var mutator = new RecordingMutator([], []);
        var missing = Result<int>.Error(DbError.IndexKeyNotFound());
        var single = new QuerySingle<TestRow, RecordingMutator>(storage, missing, ref mutator);

        // GetRef() must stay guarded by HasRow() - a ref return has no way to report
        // "no row", so bypassing the guard has to fail loudly rather than hand out a
        // reference to an arbitrary slot.
        var threw = false;
        try { single.GetRef(); }
        catch (IndexKeyNotFoundException) { threw = true; }

        Assert.That(threw, Is.True);
    }
}
