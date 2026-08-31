using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Storage;

namespace RhinoDB.Test.Lib.Indexing;

public class HashIndexTests {
    private readonly record struct TestRow(int Id, string Name);

    static private HashIndex<int, TestRow> NewIndex() => new HashIndex<int, TestRow>(
        new DenseArray<TestRow>(chunkSize: 4),
        row => row.Id
    );

    static private (DenseArray<TestRow> Storage, HashIndex<int, TestRow> Index) NewIndexWithStorage() {
        var storage = new DenseArray<TestRow>(chunkSize: 4);
        return (storage, new HashIndex<int, TestRow>(storage, row => row.Id));
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

        Assert.That(result.IsError, Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<ArgumentException>());
    }

    [Test]
    public void Insert_DuplicateKey_RollsBackTheStorageInsert() {
        // Insert always appends, so a rejected duplicate must not leave an orphaned,
        // unreachable row sitting in storage inflating Count.
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));

        var result = index.Insert(new TestRow(1, "Impostor"));

        Assert.That(result.IsError(), Is.True);
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

        Assert.That(result.IsError, Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<KeyNotFoundException>());
    }

    [Test]
    public void Delete_OfNonLastRow_RepointsTheMovedRowsIndex() {
        // The underlying DenseArray swap-removes: the last row physically moves into
        // the deleted row's slot. The index must repoint that row's dictionary entry
        // to its new slot, or its key becomes unreachable / points at stale data.
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

        index.Delete(3); // Carol was already physically last; nothing moves

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
    public void InsertAndDelete_AcrossChunkBoundary_AllRemainingRowsStayConsistent() {
        // chunkSize is 4, so 6 inserts force a second chunk allocation partway through.
        var index = NewIndex();
        for (var i = 1; i <= 6; i++)
            index.Insert(new TestRow(i, $"Row{i}"));

        index.Delete(2); // triggers a swap from the second chunk into the first

        Assert.That(index.Count, Is.EqualTo(5));
        Assert.That(index.Get(2).IsError(), Is.True);
        foreach (var id in new[] { 1, 3, 4, 5, 6 })
            Assert.That(index.Get(id).Unwrap(), Is.EqualTo(new TestRow(id, $"Row{id}")));
    }

    [Test]
    public void Delete_EveryRow_LeavesIndexEmptyAndAllKeysUnreachable() {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));
        index.Insert(new TestRow(3, "Carol"));

        index.Delete(1);
        index.Delete(2);
        index.Delete(3);

        Assert.That(index.Count, Is.EqualTo(0));
        Assert.That(index.Get(1).IsError(), Is.True);
        Assert.That(index.Get(2).IsError(), Is.True);
        Assert.That(index.Get(3).IsError(), Is.True);
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
    public void Register_DuplicateKey_DoesNotOverwriteTheExistingEntry() {
        var (storage, index) = NewIndexWithStorage();
        var a = storage.Insert(new TestRow(1, "Alice"));
        var b = storage.Insert(new TestRow(1, "Impostor"));
        index.Register(1, a);

        index.Register(1, b);

        Assert.That(index.Get(1).Unwrap(), Is.EqualTo(new TestRow(1, "Alice")));
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

        Assert.Throws<KeyNotFoundException>(() => index.Deregister(1, 0));
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
