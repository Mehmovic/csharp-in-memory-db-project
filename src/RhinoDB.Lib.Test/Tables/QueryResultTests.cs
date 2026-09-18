using RhinoDB.Core;
using RhinoDB.Lib.Storage;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Lib.Test.Tables;

public class QueryResultTests {
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

    static private ArrayPoolContainer<int> OffsetsOf(params int[] offsets) {
        using var builder = ArrayPoolContainerBuilder<int>.Create(offsets.Length);
        foreach (var offset in offsets) builder.Add(offset);
        return builder.Build().Unwrap();
    }

    // ---- QueryResultSet ----

    [Test]
    public void Get_ReturnsEveryMatchedRowInOffsetOrder() {
        var storage = NewStorageWith(new TestRow(1, "Ada"), new TestRow(2, "Bob"), new TestRow(3, "Cy"));
        var updates = new List<(TestRow, TestRow)>();
        var deletes = new List<TestRow>();
        var mutator = new RecordingMutator(updates, deletes);
        using var set = new QueryResultSet<TestRow, RecordingMutator>(storage, OffsetsOf(2, 0), ref mutator);

        var rows = set.Get().Unwrap();

        Assert.That(rows.Length, Is.EqualTo(2));
        Assert.That(rows[0], Is.EqualTo(new TestRow(3, "Cy")));
        Assert.That(rows[1], Is.EqualTo(new TestRow(1, "Ada")));
    }

    [Test]
    public void Get_WithNoMatches_ReturnsAnEmptySpan() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var mutator = new RecordingMutator([], []);
        using var set = new QueryResultSet<TestRow, RecordingMutator>(storage, OffsetsOf(), ref mutator);

        Assert.That(set.Get().Unwrap().Length, Is.EqualTo(0));
    }

    [Test]
    public void Get_AfterDispose_ReturnsError() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var mutator = new RecordingMutator([], []);
        var set = new QueryResultSet<TestRow, RecordingMutator>(storage, OffsetsOf(0), ref mutator);
        set.Dispose();

        Assert.That(set.Get().IsError(), Is.True);
    }

    [Test]
    public void Dispose_CalledTwice_IsSafe() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var mutator = new RecordingMutator([], []);
        var set = new QueryResultSet<TestRow, RecordingMutator>(storage, OffsetsOf(0), ref mutator);

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
        using var set = new QueryResultSet<TestRow, RecordingMutator>(storage, OffsetsOf(0, 1), ref mutator);

        var affected = set.Update(new TestRow(-1, "Stamped")).Unwrap();

        Assert.That(affected, Is.EqualTo(2));
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
        using var set = new QueryResultSet<TestRow, RecordingMutator>(storage, OffsetsOf(0, 1), ref mutator);

        var affected = set.Update(row => row with { Name = row.Name + "!" }).Unwrap();

        Assert.That(affected, Is.EqualTo(2));
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
        using var set = new QueryResultSet<TestRow, RecordingMutator>(storage, OffsetsOf(0, 1), ref mutator);

        var affected = set.Delete().Unwrap();

        Assert.That(affected, Is.EqualTo(2));
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

        var affected = single.Update(new TestRow(1, "Adaline")).Unwrap();

        Assert.That(affected, Is.EqualTo(1));
        Assert.That(updates, Is.EqualTo(new[] { (new TestRow(1, "Ada"), new TestRow(1, "Adaline")) }));
    }

    [Test]
    public void Update_OnAnErrorOffset_PropagatesTheErrorAndCallsNothing() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var updates = new List<(TestRow, TestRow)>();
        var mutator = new RecordingMutator(updates, []);
        var single = new QuerySingle<TestRow, RecordingMutator>(storage, Result<int>.Error(DbError.IndexKeyNotFound()), ref mutator);

        Assert.That(single.Update(new TestRow(1, "Adaline")).IsError(), Is.True);
        Assert.That(updates, Is.Empty);
    }

    [Test]
    public void Delete_WithAResolvedOffset_CallsMutatorOnce() {
        var storage = NewStorageWith(new TestRow(1, "Ada"));
        var deletes = new List<TestRow>();
        var mutator = new RecordingMutator([], deletes);
        var single = new QuerySingle<TestRow, RecordingMutator>(storage, 0, ref mutator);

        var affected = single.Delete().Unwrap();

        Assert.That(affected, Is.EqualTo(1));
        Assert.That(deletes, Is.EqualTo(new[] { new TestRow(1, "Ada") }));
    }
}
