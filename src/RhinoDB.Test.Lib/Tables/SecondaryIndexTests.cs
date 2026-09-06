using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Test.Lib.Tables;

public class SecondaryIndexTests {
    private readonly record struct Widget(int Id, string Sku);

    // ---- UniqueSecondaryIndex ----

    [Test]
    public void UniqueSecondaryIndex_CheckInsert_NoExistingEntry_Succeeds() {
        var index = new HashIndex<string>();
        var secondary = new UniqueSecondaryIndex<Widget, string>(index, static w => w.Sku);

        var result = secondary.CheckInsert(new Widget(1, "A"), selfOffset: -1);

        Assert.That(result.IsOk(), Is.True);
    }

    [Test]
    public void UniqueSecondaryIndex_CheckInsert_ExistingEntryAtDifferentOffset_ReportsDuplicateKey() {
        var index = new HashIndex<string>();
        index.Insert("A", offset: 5);
        var secondary = new UniqueSecondaryIndex<Widget, string>(index, static w => w.Sku);

        var result = secondary.CheckInsert(new Widget(1, "A"), selfOffset: 7);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void UniqueSecondaryIndex_CheckInsert_ExistingEntryAtSameSelfOffset_SucceedsAsNotAConflict() {
        // The self-collision fix: an entry pointing at the row's own current offset
        // is not a real conflict, it's the row's own unchanged (or re-written) key.
        // Without threading selfOffset through, this exact case is what makes a naive
        // "always re-register on Update" implementation false-positive on itself.
        var index = new HashIndex<string>();
        index.Insert("A", offset: 7);
        var secondary = new UniqueSecondaryIndex<Widget, string>(index, static w => w.Sku);

        var result = secondary.CheckInsert(new Widget(1, "A"), selfOffset: 7);

        Assert.That(result.IsOk(), Is.True);
    }

    [Test]
    public void UniqueSecondaryIndex_Insert_ThenDelete_RemovesTheEntry() {
        var index = new HashIndex<string>();
        var secondary = new UniqueSecondaryIndex<Widget, string>(index, static w => w.Sku);

        secondary.Insert(new Widget(1, "A"), offset: 3);
        Assert.That(index.GetOffset("A").Unwrap(), Is.EqualTo(3));

        secondary.Delete(new Widget(1, "A"), offset: 3);
        Assert.That(index.GetOffset("A").IsError(), Is.True);
    }

    // ---- NonUniqueSecondaryIndex ----

    [Test]
    public void NonUniqueSecondaryIndex_CheckInsert_AlwaysSucceeds_EvenWithAnExistingEntry() {
        var index = new NonUniqueHashIndex<string>();
        index.Insert("A", offset: 5);
        var secondary = new NonUniqueSecondaryIndex<Widget, string>(index, static w => w.Sku);

        var result = secondary.CheckInsert(new Widget(1, "A"), selfOffset: -1);

        Assert.That(result.IsOk(), Is.True);
    }

    [Test]
    public void NonUniqueSecondaryIndex_Insert_ThenDelete_RemovesOnlyThatOffset() {
        var index = new NonUniqueHashIndex<string>();
        var secondary = new NonUniqueSecondaryIndex<Widget, string>(index, static w => w.Sku);

        secondary.Insert(new Widget(1, "A"), offset: 3);
        secondary.Insert(new Widget(2, "A"), offset: 4);

        secondary.Delete(new Widget(1, "A"), offset: 3);

        Assert.That(index.GetOffsets("A"), Is.EqualTo(new[] { 4 }));
    }
}
