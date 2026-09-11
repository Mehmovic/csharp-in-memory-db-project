namespace RhinoDB.Lib.Tables.Test;

// ChangeSet<TKey,TRow> is the per-table, per-operation staging area behind the
// "tables never write directly" rule (Part G plan, point 1) - Insert/Update/Delete
// on a generated table stage a Change here instead of touching real storage; the
// runner (not built yet, task #19) is the only thing that ever replays Changes
// against the real Table/PersistentTable, and only after the whole operation
// succeeds.
//
// API deliberately mirrors Table<TKey,TRow>'s own shape rather than inventing
// Stage*-prefixed names: Insert(TRow record) derives the key via the same
// constructor-supplied selector Table itself uses, Update(TKey id, TRow newRecord)
// is identical to Table's. Delete is the one place it can't fully match -
// Table.Delete(TKey id) alone works because Table has real storage to look the row
// up in; ChangeSet has none, so the caller (the future generated proxy, which
// already resolves "what does this key currently look like" for its own
// not-found check) must supply the row being deleted.
//
// v1 scope, deliberately: primary-key point lookups only (read-your-own-writes for
// a Get-style read). Secondary-index overlay (a ByX read seeing a same-operation
// staged change) is its own follow-up slice (task #20), not covered here.
public class ChangeSetTests {
    private readonly record struct Gadget(int Id, string Sku, int Stock);

    static private Gadget Widget(int id = 1, int stock = 10) => new Gadget(id, $"SKU-{id}", stock);

    static private ChangeSet<int, Gadget> NewChangeSet() => new ChangeSet<int, Gadget>(static g => g.Id);

    [Test]
    public void NewChangeSet_HasNoPendingEntryForAnyKey() {
        var changeSet = NewChangeSet();

        var lookup = changeSet.TryGetPending(1, out _);

        Assert.That(lookup, Is.EqualTo(PendingLookup.NotStaged));
    }

    [Test]
    public void Insert_TryGetPending_ReturnsPresentWithTheInsertedRow() {
        var changeSet = NewChangeSet();
        var gadget = Widget();

        changeSet.Insert(gadget);
        var lookup = changeSet.TryGetPending(1, out var row);

        Assert.That(lookup, Is.EqualTo(PendingLookup.Present));
        Assert.That(row, Is.EqualTo(gadget));
    }

    [Test]
    public void Update_TryGetPending_ReturnsPresentWithTheUpdatedRow() {
        var changeSet = NewChangeSet();
        var updated = Widget(stock: 99);

        changeSet.Update(1, updated);
        var lookup = changeSet.TryGetPending(1, out var row);

        Assert.That(lookup, Is.EqualTo(PendingLookup.Present));
        Assert.That(row, Is.EqualTo(updated));
    }

    [Test]
    public void Delete_TryGetPending_ReturnsDeleted() {
        var changeSet = NewChangeSet();

        changeSet.Delete(1, Widget());
        var lookup = changeSet.TryGetPending(1, out _);

        Assert.That(lookup, Is.EqualTo(PendingLookup.Deleted));
    }

    [Test]
    public void InsertThenUpdate_SameKey_MostRecentWins() {
        var changeSet = NewChangeSet();
        var updated = Widget(stock: 42);

        changeSet.Insert(Widget(stock: 10));
        changeSet.Update(1, updated);
        var lookup = changeSet.TryGetPending(1, out var row);

        Assert.That(lookup, Is.EqualTo(PendingLookup.Present));
        Assert.That(row, Is.EqualTo(updated));
    }

    [Test]
    public void InsertThenDelete_SameKey_ReportsDeletedNotThePriorInsert() {
        var changeSet = NewChangeSet();

        changeSet.Insert(Widget());
        changeSet.Delete(1, Widget());
        var lookup = changeSet.TryGetPending(1, out _);

        Assert.That(lookup, Is.EqualTo(PendingLookup.Deleted));
    }

    [Test]
    public void DeleteThenInsert_SameKey_UndoesTheDeleteAndReportsPresent() {
        // A key deleted then reinserted within the same operation must read as
        // present again - the most-recent staged entry always wins, never a
        // "once deleted, always deleted for this operation" latch.
        var changeSet = NewChangeSet();
        var reinserted = Widget(stock: 7);

        changeSet.Delete(1, Widget());
        changeSet.Insert(reinserted);
        var lookup = changeSet.TryGetPending(1, out var row);

        Assert.That(lookup, Is.EqualTo(PendingLookup.Present));
        Assert.That(row, Is.EqualTo(reinserted));
    }

    [Test]
    public void DifferentKeys_AreTrackedIndependently() {
        var changeSet = NewChangeSet();
        var one = Widget(id: 1);
        var two = Widget(id: 2);

        changeSet.Insert(one);
        changeSet.Delete(2, two);

        Assert.That(changeSet.TryGetPending(1, out var rowOne), Is.EqualTo(PendingLookup.Present));
        Assert.That(rowOne, Is.EqualTo(one));
        Assert.That(changeSet.TryGetPending(2, out _), Is.EqualTo(PendingLookup.Deleted));
    }

    [Test]
    public void Changes_ExposesEveryStagedEntryInCallOrder_ForTheRunnerToReplay() {
        var changeSet = NewChangeSet();
        var inserted = Widget(id: 1);
        var updated = Widget(id: 1, stock: 5);

        changeSet.Insert(inserted);
        changeSet.Update(1, updated);
        changeSet.Delete(2, Widget(id: 2));

        Assert.That(changeSet.Changes, Has.Count.EqualTo(3));
        Assert.That(changeSet.Changes[0].Kind, Is.EqualTo(ChangeKind.Insert));
        Assert.That(changeSet.Changes[1].Kind, Is.EqualTo(ChangeKind.Update));
        Assert.That(changeSet.Changes[2].Kind, Is.EqualTo(ChangeKind.Delete));
    }

    [Test]
    public void Clear_RemovesEveryStagedEntry_ForReuseAcrossOperations() {
        var changeSet = NewChangeSet();
        changeSet.Insert(Widget());

        changeSet.Clear();

        Assert.That(changeSet.Changes, Is.Empty);
        Assert.That(changeSet.TryGetPending(1, out _), Is.EqualTo(PendingLookup.NotStaged));
    }
}
