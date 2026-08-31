using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Storage;

namespace RhinoDB.Test.Lib.Indexing;

public class OrderedIndexTests {
    private readonly record struct TestRow(int Id, string Name);

    static private OrderedIndex<int, TestRow> NewIndex() => new OrderedIndex<int, TestRow>(
        new DenseArray<TestRow>(chunkSize: 4),
        row => row.Id
    );

    static private (DenseArray<TestRow> Storage, OrderedIndex<int, TestRow> Index) NewIndexWithStorage() {
        var storage = new DenseArray<TestRow>(chunkSize: 4);
        return (storage, new OrderedIndex<int, TestRow>(storage, row => row.Id));
    }

    [Test]
    public void Insert_ThenGet_ReturnsTheInsertedRow() {
        var index = NewIndex();

        var insertResult = index.Insert(new TestRow(1, "Alice"));

        Assert.That(insertResult.IsOk(), Is.True);
        Assert.That(index.Get(1).Unwrap(), Is.EqualTo(new TestRow(1, "Alice")));
        Assert.That(index.Count, Is.EqualTo(1));
    }

    [Test]
    public void Insert_MultipleRows_AllRetrievableByKey() {
        var index = NewIndex();

        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));
        index.Insert(new TestRow(3, "Carol"));

        Assert.That(index.Get(1).Unwrap(), Is.EqualTo(new TestRow(1, "Alice")));
        Assert.That(index.Get(2).Unwrap(), Is.EqualTo(new TestRow(2, "Bob")));
        Assert.That(index.Get(3).Unwrap(), Is.EqualTo(new TestRow(3, "Carol")));
        Assert.That(index.Count, Is.EqualTo(3));
    }

    [Test]
    public void Insert_DuplicateKey_ReturnsFailureWithArgumentException() {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));

        var result = index.Insert(new TestRow(1, "Impostor"));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<ArgumentException>());
    }

    [Test]
    public void Insert_DuplicateKey_RollsBackTheStorageInsert() {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));

        index.Insert(new TestRow(1, "Impostor"));

        Assert.That(index.Count, Is.EqualTo(1));
    }

    [Test]
    public void Get_UnknownKey_ReturnsFailureWithKeyNotFoundException() {
        var index = NewIndex();

        var result = index.Get(999);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<KeyNotFoundException>());
    }

    [Test]
    public void Delete_RemovesTheKey_SubsequentGetReturnsFailure() {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));

        var deleteResult = index.Delete(1);

        Assert.That(deleteResult.IsOk(), Is.True);
        Assert.That(index.Count, Is.EqualTo(0));
        Assert.That(index.Get(1).IsError(), Is.True);
    }

    [Test]
    public void Delete_UnknownKey_ReturnsFailureWithKeyNotFoundException() {
        var index = NewIndex();

        var result = index.Delete(999);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<KeyNotFoundException>());
    }

    [Test]
    public void Delete_OfNonLastRow_RepointsTheMovedRowsIndex() {
        // Same underlying DenseArray swap-remove concern as HashIndex: the physically
        // last row moves into the deleted row's slot and must stay reachable by key.
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));
        index.Insert(new TestRow(3, "Carol"));

        index.Delete(1); // Carol (physically last) moves into Alice's old slot

        Assert.That(index.Count, Is.EqualTo(2));
        Assert.That(index.Get(3).Unwrap(), Is.EqualTo(new TestRow(3, "Carol")), "moved row must still be reachable by its key");
        Assert.That(index.Get(2).Unwrap(), Is.EqualTo(new TestRow(2, "Bob")), "untouched row must be unaffected");
        Assert.That(index.Get(1).IsError(), Is.True);
    }

    [Test]
    public void Delete_OfThePhysicallyLastRow_NeedsNoRepointing() {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));
        index.Insert(new TestRow(3, "Carol"));

        index.Delete(3);

        Assert.That(index.Count, Is.EqualTo(2));
        Assert.That(index.Get(1).Unwrap(), Is.EqualTo(new TestRow(1, "Alice")));
        Assert.That(index.Get(2).Unwrap(), Is.EqualTo(new TestRow(2, "Bob")));
        Assert.That(index.Get(3).IsError(), Is.True);
    }

    [Test]
    public void InsertAfterDelete_ReusesTheFreedSlot_AndIndexStaysConsistent() {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));

        index.Delete(1); // Bob moves into slot 0
        index.Insert(new TestRow(3, "Carol")); // appended into the now-freed slot 1

        Assert.That(index.Count, Is.EqualTo(2));
        Assert.That(index.Get(2).Unwrap(), Is.EqualTo(new TestRow(2, "Bob")));
        Assert.That(index.Get(3).Unwrap(), Is.EqualTo(new TestRow(3, "Carol")));
    }

    [Test]
    public void Range_ReturnsRowsWithinBoundsInclusive_InAscendingKeyOrder() {
        var index = NewIndex();
        // Inserted out of key order on purpose - ordering must come from the index
        // itself, not from insertion order or physical array position.
        index.Insert(new TestRow(5, "Eve"));
        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(3, "Carol"));
        index.Insert(new TestRow(4, "Dave"));
        index.Insert(new TestRow(2, "Bob"));

        var rows = index.Range(2, 4);

        Assert.That(
            rows,
            Is.EqualTo(
                new[] {
                    new TestRow(2, "Bob"),
                    new TestRow(3, "Carol"),
                    new TestRow(4, "Dave"),
                }
            )
        );
    }

    [Test]
    public void Range_BoundsAreInclusive() {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));
        index.Insert(new TestRow(3, "Carol"));

        var rows = index.Range(1, 3);

        Assert.That(rows.Select(r => r.Id), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void Range_WithNoMatches_ReturnsEmpty() {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));

        var rows = index.Range(100, 200);

        Assert.That(rows, Is.Empty);
    }

    [Test]
    public void Range_WhenFromIsGreaterThanTo_ReturnsEmpty() {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));

        var rows = index.Range(2, 1);

        Assert.That(rows, Is.Empty);
    }

    [Test]
    public void Range_ReflectsStateAfterADelete() {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));
        index.Insert(new TestRow(3, "Carol"));

        index.Delete(2);
        var rows = index.Range(1, 3);

        Assert.That(rows.Select(r => r.Id), Is.EqualTo(new[] { 1, 3 }));
    }

    [Test]
    public void Delete_ThenInsertSameKeyAgain_Succeeds() {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));
        index.Delete(1);

        var result = index.Insert(new TestRow(1, "Alicia"));

        Assert.That(result.IsOk(), Is.True);
        Assert.That(index.Get(1).Unwrap(), Is.EqualTo(new TestRow(1, "Alicia")));
        Assert.That(index.Count, Is.EqualTo(1));
    }

    [Test]
    public void Range_FromEqualsTo_OnExistingKey_ReturnsThatSingleRow() {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));

        var rows = index.Range(2, 2);

        Assert.That(rows, Is.EqualTo(new[] { new TestRow(2, "Bob") }));
    }

    [Test]
    public void InsertAndRange_AcrossChunkBoundary_MaintainsAscendingOrder() {
        // chunkSize is 4, so 6 inserts force a second chunk allocation partway through.
        var index = NewIndex();
        foreach (var id in new[] { 5, 1, 6, 3, 2, 4 }) // out of order on purpose
            index.Insert(new TestRow(id, $"Row{id}"));

        var rows = index.Range(1, 6);

        Assert.That(rows.Select(r => r.Id), Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6 }));
    }

    [Test]
    public void Register_ThenGet_ReturnsTheRow() {
        var (storage, index) = NewIndexWithStorage();
        var offset = storage.Insert(new TestRow(1, "Alice"));

        var result = index.Register(1, offset);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(index.Get(1).Unwrap(), Is.EqualTo(new TestRow(1, "Alice")));
    }

    [Test]
    public void Register_DuplicateKey_ReturnsFailureWithArgumentException() {
        var (storage, index) = NewIndexWithStorage();
        var a = storage.Insert(new TestRow(1, "Alice"));
        var b = storage.Insert(new TestRow(1, "Impostor"));
        index.Register(1, a);

        var result = index.Register(1, b);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<ArgumentException>());
    }

    [Test]
    public void Register_MultipleKeysOutOfOrder_RangeStillReturnsAscendingOrder() {
        var (storage, index) = NewIndexWithStorage();
        var c = storage.Insert(new TestRow(3, "Carol"));
        var a = storage.Insert(new TestRow(1, "Alice"));
        var b = storage.Insert(new TestRow(2, "Bob"));
        index.Register(3, c);
        index.Register(1, a);
        index.Register(2, b);

        var rows = index.Range(1, 3);

        Assert.That(rows.Select(r => r.Id), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void Deregister_RemovesTheKey_SubsequentGetFails() {
        var (storage, index) = NewIndexWithStorage();
        var offset = storage.Insert(new TestRow(1, "Alice"));
        index.Register(1, offset);

        index.Deregister(1, offset);

        Assert.That(index.Get(1).IsError(), Is.True);
    }

    [Test]
    public void Deregister_UnknownKey_Throws() {
        var (_, index) = NewIndexWithStorage();

        var res = index.Deregister(1, 0);

        Assert.That(res.IsError(), Is.True);
        Assert.That(res.GetException(), Is.InstanceOf<KeyNotFoundException>());
    }

    [Test]
    public void Deregister_OffsetMismatch_Throws() {
        var (storage, index) = NewIndexWithStorage();
        var offset = storage.Insert(new TestRow(1, "Alice"));
        index.Register(1, offset);

        var res = index.Deregister(1, offset + 999);

        Assert.That(res.IsError(), Is.True);
        Assert.That(res.GetException(), Is.InstanceOf<ArgumentException>());
    }

    [Test]
    public void Deregister_ThenRegisterNewKey_SupportsRekeying() {
        // The Update use case: same physical row, key changes, offset stays the same.
        var (storage, index) = NewIndexWithStorage();
        var offset = storage.Insert(new TestRow(1, "Alice"));
        index.Register(1, offset);

        index.Deregister(1, offset);
        var result = index.Register(2, offset);
        storage.Set(offset, new TestRow(2, "Alice"));

        Assert.That(result.IsOk(), Is.True);
        Assert.That(index.Get(1).IsError(), Is.True);
        Assert.That(index.Get(2).Unwrap(), Is.EqualTo(new TestRow(2, "Alice")));
    }
}
