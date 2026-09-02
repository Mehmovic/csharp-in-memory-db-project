using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Indexing;

namespace RhinoDB.Test.Lib.Indexing;

public class HashIndexTests {
    static private HashIndex<int> NewIndex() => new HashIndex<int>();

    [Test]
    public void Insert_ThenGetOffset_ReturnsTheOffset() {
        var index = NewIndex();

        var insertResult = index.Insert(1, 0);

        Assert.That(insertResult.IsOk(), Is.True);
        Assert.That(index.GetOffset(1).Unwrap(), Is.EqualTo(0));
        Assert.That(index.Count, Is.EqualTo(1));
    }

    [Test]
    public void Insert_MultipleKeys_AllRetrievableByKey() {
        var index = NewIndex();

        index.Insert(1, 0);
        index.Insert(2, 1);
        index.Insert(3, 2);

        Assert.That(index.GetOffset(1).Unwrap(), Is.EqualTo(0));
        Assert.That(index.GetOffset(2).Unwrap(), Is.EqualTo(1));
        Assert.That(index.GetOffset(3).Unwrap(), Is.EqualTo(2));
        Assert.That(index.Count, Is.EqualTo(3));
    }

    [Test]
    public void Insert_DuplicateKey_ReturnsFailureWithDuplicateKeyException() {
        var index = NewIndex();
        index.Insert(1, 0);

        var result = index.Insert(1, 99);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void Insert_DuplicateKey_DoesNotOverwriteTheExistingEntry() {
        var index = NewIndex();
        index.Insert(1, 0);

        index.Insert(1, 99);

        Assert.That(index.GetOffset(1).Unwrap(), Is.EqualTo(0));
    }

    [Test]
    public void GetOffset_UnknownKey_ReturnsFailureWithIndexKeyNotFoundException() {
        var index = NewIndex();

        var result = index.GetOffset(999);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Delete_RemovesTheKey_SubsequentGetOffsetFails() {
        var index = NewIndex();
        index.Insert(1, 0);

        var deleteResult = index.Delete(1, 0);

        Assert.That(deleteResult.IsOk(), Is.True);
        Assert.That(index.Count, Is.EqualTo(0));
        Assert.That(index.GetOffset(1).IsError(), Is.True);
    }

    [Test]
    public void Delete_UnknownKey_ReturnsFailureWithIndexKeyNotFoundException() {
        var index = NewIndex();

        var result = index.Delete(999, 0);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Delete_OffsetMismatch_ReturnsFailureWithOffsetNotRegisteredException() {
        var index = NewIndex();
        index.Insert(1, 0);

        var result = index.Delete(1, 999);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<OffsetNotRegisteredException>());
    }

    [Test]
    public void Delete_OffsetMismatch_DoesNotRemoveTheEntry() {
        var index = NewIndex();
        index.Insert(1, 0);

        index.Delete(1, 999);

        Assert.That(index.GetOffset(1).Unwrap(), Is.EqualTo(0));
    }

    [Test]
    public void Delete_ThenInsertSameKeyAgain_Succeeds() {
        var index = NewIndex();
        index.Insert(1, 0);
        index.Delete(1, 0);

        var result = index.Insert(1, 5);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(index.GetOffset(1).Unwrap(), Is.EqualTo(5));
        Assert.That(index.Count, Is.EqualTo(1));
    }

    [Test]
    public void DeleteThenInsertNewKey_SameOffset_SupportsRekeying() {
        // The Update use case: same physical row, key changes, offset stays the same.
        var index = NewIndex();
        index.Insert(1, 0);

        index.Delete(1, 0);
        var result = index.Insert(2, 0);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(index.GetOffset(1).IsError(), Is.True);
        Assert.That(index.GetOffset(2).Unwrap(), Is.EqualTo(0));
    }

    [Test]
    public void Delete_EveryKey_LeavesIndexEmpty() {
        var index = NewIndex();
        index.Insert(1, 0);
        index.Insert(2, 1);
        index.Insert(3, 2);

        index.Delete(1, 0);
        index.Delete(2, 1);
        index.Delete(3, 2);

        Assert.That(index.Count, Is.EqualTo(0));
        Assert.That(index.GetOffset(1).IsError(), Is.True);
        Assert.That(index.GetOffset(2).IsError(), Is.True);
        Assert.That(index.GetOffset(3).IsError(), Is.True);
    }
}
