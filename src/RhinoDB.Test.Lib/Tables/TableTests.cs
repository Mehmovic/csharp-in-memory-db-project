using RhinoDB.Core.Exceptions;
using RhinoDB.Core.Results;
using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Test.Lib.Tables;

public class TableTests {
    // Widget exercises the same four index kinds PlayerTable does (unique primary,
    // unique secondary, non-unique-hash secondary, non-unique-ordered secondary),
    // but drives Table<TPk,TRow> directly instead of through a composing class -
    // this suite is what proves the generic type itself, not a specific table.
    private readonly record struct Widget(int Id, string Sku, string Category, int Stock);

    // Bundles the generic Table with the concrete secondary indexes registered into
    // it - Table<TPk,TRow> has no way to expose bespoke per-column read accessors
    // (GetBySku/GetByCategory/GetByStock) generically, so tests read through the
    // concrete index + Table.GetByOffset, exactly like a real composing table would.
    private readonly record struct WidgetTable(
        Table<int, Widget> Table,
        HashIndex<string> IdxSku,
        NonUniqueHashIndex<string> IdxCategory,
        NonUniqueOrderedIndex<int> IdxStock);

    static private WidgetTable NewTable() {
        var table = new Table<int, Widget>(chunkSize: 4, new HashIndex<int>(), static w => w.Id);
        var idxSku = new HashIndex<string>();
        var idxCategory = new NonUniqueHashIndex<string>();
        var idxStock = new NonUniqueOrderedIndex<int>();

        table.Register(new UniqueSecondaryIndex<Widget, string>(idxSku, static w => w.Sku));
        table.Register(new NonUniqueSecondaryIndex<Widget, string>(idxCategory, static w => w.Category));
        table.Register(new NonUniqueSecondaryIndex<Widget, int>(idxStock, static w => w.Stock));

        return new WidgetTable(table, idxSku, idxCategory, idxStock);
    }

    static private Widget Gizmo(int id = 1, string category = "Tools", int stock = 80) =>
        new Widget(id, $"SKU-{id}", category, stock);

    static private Result<Widget> GetBySku(WidgetTable t, string sku) {
        var offset = t.IdxSku.GetOffset(sku);
        return offset.IsError() ? offset.Void() : t.Table.GetByOffset(offset.Unwrap());
    }

    static private List<Widget> GetByCategory(WidgetTable t, string category) {
        var offsets = t.IdxCategory.GetOffsets(category);
        var rows = new List<Widget>(offsets.Count);
        foreach (var offset in offsets) rows.Add(t.Table.GetByOffset(offset));
        return rows;
    }

    static private List<Widget> GetByStock(WidgetTable t, int from, int to) {
        var offsets = t.IdxStock.Range(from, to);
        var rows = new List<Widget>(offsets.Count);
        foreach (var offset in offsets) rows.Add(t.Table.GetByOffset(offset));
        return rows;
    }

    // ---- Insert ----

    [Test]
    public void Insert_ThenGet_ReturnsTheInsertedRow() {
        var t = NewTable();
        var widget = Gizmo();

        var result = t.Table.Insert(widget);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(t.Table.Get(1).Unwrap(), Is.EqualTo(widget));
        Assert.That(t.Table.Count, Is.EqualTo(1));
    }

    [Test]
    public void Insert_ThenAllSecondaryAccessors_FindTheRow() {
        var t = NewTable();
        var widget = Gizmo();
        t.Table.Insert(widget);

        Assert.That(GetBySku(t, widget.Sku).Unwrap(), Is.EqualTo(widget));
        Assert.That(GetByCategory(t, widget.Category), Is.EqualTo(new[] { widget }));
        Assert.That(GetByStock(t, widget.Stock, widget.Stock), Is.EqualTo(new[] { widget }));
    }

    [Test]
    public void Insert_DuplicatePrimaryKey_ReturnsFailureWithDuplicateKeyException() {
        var t = NewTable();
        t.Table.Insert(Gizmo(id: 1));

        var result = t.Table.Insert(Gizmo(id: 1, category: "Blue"));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void Insert_DuplicatePrimaryKey_DoesNotChangeCount() {
        var t = NewTable();
        t.Table.Insert(Gizmo(id: 1));

        t.Table.Insert(Gizmo(id: 1, category: "Blue"));

        Assert.That(t.Table.Count, Is.EqualTo(1));
    }

    [Test]
    public void Insert_DuplicateSku_ReturnsFailureWithDuplicateKeyException() {
        var t = NewTable();
        t.Table.Insert(Gizmo(id: 1));

        var duplicate = new Widget(2, "SKU-1", "Blue", 70);
        var result = t.Table.Insert(duplicate);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void Insert_DuplicateSku_RollsBackCompletely_NewRowUnreachableEverywhere() {
        var t = NewTable();
        t.Table.Insert(Gizmo(id: 1));

        var duplicate = new Widget(2, "SKU-1", "Blue", 70);
        t.Table.Insert(duplicate);

        Assert.That(t.Table.Count, Is.EqualTo(1));
        Assert.That(t.Table.Get(2).IsError(), Is.True);
        Assert.That(GetByCategory(t, "Blue"), Is.Empty);
        Assert.That(GetByStock(t, 70, 70), Is.Empty);
    }

    [Test]
    public void Insert_DuplicateSku_OriginalRowStillFullyIntact() {
        var t = NewTable();
        var original = Gizmo(id: 1);
        t.Table.Insert(original);

        t.Table.Insert(new Widget(2, original.Sku, "Blue", 70));

        Assert.That(t.Table.Get(1).Unwrap(), Is.EqualTo(original));
        Assert.That(GetBySku(t, original.Sku).Unwrap(), Is.EqualTo(original));
    }

    [Test]
    public void Insert_SameCategoryDifferentWidgets_BothRetrievableViaGetByCategory() {
        var t = NewTable();
        var a = Gizmo(id: 1, category: "Red");
        var b = new Widget(2, "SKU-2", "Red", 75);

        t.Table.Insert(a);
        t.Table.Insert(b);

        Assert.That(GetByCategory(t, "Red"), Is.EquivalentTo(new[] { a, b }));
    }

    [Test]
    public void Insert_AcrossChunkBoundary_AllIndexesStayConsistent() {
        // chunkSize is 4, so 6 inserts force a second chunk allocation partway through.
        var t = NewTable();
        var widgets = Enumerable.Range(1, 6)
            .Select(i => new Widget(i, $"SKU-{i}", "Red", 50 + i))
            .ToArray();

        foreach (var w in widgets) t.Table.Insert(w);

        foreach (var w in widgets) {
            Assert.That(t.Table.Get(w.Id).Unwrap(), Is.EqualTo(w));
            Assert.That(GetBySku(t, w.Sku).Unwrap(), Is.EqualTo(w));
            Assert.That(GetByStock(t, w.Stock, w.Stock), Is.EqualTo(new[] { w }));
        }
        Assert.That(GetByCategory(t, "Red"), Is.EquivalentTo(widgets));
    }

    // ---- Delete ----

    [Test]
    public void Delete_UnknownId_ReturnsFailureWithIndexKeyNotFoundException() {
        var t = NewTable();

        var result = t.Table.Delete(999);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Delete_UnknownId_DoesNotChangeCount() {
        var t = NewTable();
        t.Table.Insert(Gizmo(id: 1));

        t.Table.Delete(999);

        Assert.That(t.Table.Count, Is.EqualTo(1));
    }

    [Test]
    public void Delete_OfThePhysicallyLastRow_RemovesFromAllIndexes() {
        var t = NewTable();
        t.Table.Insert(Gizmo(id: 1, category: "Red"));
        var last = new Widget(2, "SKU-2", "Blue", 70);
        t.Table.Insert(last);

        var result = t.Table.Delete(2); // was already physically last; no swap

        Assert.That(result.IsOk(), Is.True);
        Assert.That(t.Table.Count, Is.EqualTo(1));
        Assert.That(t.Table.Get(2).IsError(), Is.True);
        Assert.That(GetBySku(t, last.Sku).IsError(), Is.True);
        Assert.That(GetByCategory(t, "Blue"), Is.Empty);
        Assert.That(GetByStock(t, 70, 70), Is.Empty);
    }

    [Test]
    public void Delete_OfNonLastRow_TheSwappedRowStaysReachableOnEveryIndex() {
        // Central regression: DenseArray swap-removes on delete, so the physically-
        // last row moves into the deleted slot. Every registered index - not just
        // the primary - must repoint to the row's new offset.
        var t = NewTable();
        var a = Gizmo(id: 1, category: "Red", stock: 80);
        var b = new Widget(2, "SKU-2", "Blue", 70);
        var c = new Widget(3, "SKU-3", "Green", 90); // physically last
        t.Table.Insert(a);
        t.Table.Insert(b);
        t.Table.Insert(c);

        var result = t.Table.Delete(1); // c swaps into a's old slot

        Assert.That(result.IsOk(), Is.True);
        Assert.That(t.Table.Count, Is.EqualTo(2));

        // c must still be reachable, correctly, through every index.
        Assert.That(t.Table.Get(3).Unwrap(), Is.EqualTo(c));
        Assert.That(GetBySku(t, c.Sku).Unwrap(), Is.EqualTo(c));
        Assert.That(GetByCategory(t, "Green"), Is.EqualTo(new[] { c }));
        Assert.That(GetByStock(t, 90, 90), Is.EqualTo(new[] { c }));

        // Untouched row unaffected.
        Assert.That(t.Table.Get(2).Unwrap(), Is.EqualTo(b));

        // Deleted row fully gone.
        Assert.That(t.Table.Get(1).IsError(), Is.True);
        Assert.That(GetBySku(t, a.Sku).IsError(), Is.True);
        Assert.That(GetByCategory(t, "Red"), Is.Empty);
        Assert.That(GetByStock(t, 80, 80), Is.Empty);
    }

    [Test]
    public void Delete_OfNonLastRow_SiblingsInTheSameNonUniqueHashBucketAreUnaffected() {
        var t = NewTable();
        var a = new Widget(1, "SKU-1", "Red", 80);
        var b = new Widget(2, "SKU-2", "Red", 70); // shares Category with a
        var c = new Widget(3, "SKU-3", "Red", 90); // physically last, shares Category too
        t.Table.Insert(a);
        t.Table.Insert(b);
        t.Table.Insert(c);

        t.Table.Delete(1); // c swaps into a's slot; b must be untouched

        Assert.That(GetByCategory(t, "Red"), Is.EquivalentTo(new[] { b, c }));
    }

    [Test]
    public void Delete_OfNonLastRow_SiblingsInTheSameNonUniqueOrderedBucketAreUnaffected() {
        // Mirrors the hash-bucket version above but exercises the ordered
        // (SortedSet<(TKey,int)>-backed) non-unique index's swap-repoint path -
        // a different concrete backing than idxCategory, so this is the test that
        // would actually catch a swap-repoint bug specific to that implementation.
        var t = NewTable();
        var a = new Widget(1, "SKU-1", "Red", 80);
        var b = new Widget(2, "SKU-2", "Blue", 80); // shares Stock with a
        var c = new Widget(3, "SKU-3", "Green", 80); // physically last, shares Stock too
        t.Table.Insert(a);
        t.Table.Insert(b);
        t.Table.Insert(c);

        t.Table.Delete(1); // c swaps into a's slot; b must be untouched

        Assert.That(GetByStock(t, 80, 80), Is.EquivalentTo(new[] { b, c }));
    }

    [Test]
    public void Delete_EveryRow_LeavesTableEmptyAcrossAllIndexes() {
        var t = NewTable();
        var widgets = new[] {
            new Widget(1, "SKU-1", "Red", 80),
            new Widget(2, "SKU-2", "Blue", 70),
            new Widget(3, "SKU-3", "Green", 90),
        };
        foreach (var w in widgets) t.Table.Insert(w);

        foreach (var w in widgets) t.Table.Delete(w.Id);

        Assert.That(t.Table.Count, Is.EqualTo(0));
        foreach (var w in widgets) {
            Assert.That(t.Table.Get(w.Id).IsError(), Is.True);
            Assert.That(GetBySku(t, w.Sku).IsError(), Is.True);
            Assert.That(GetByCategory(t, w.Category), Is.Empty);
            Assert.That(GetByStock(t, w.Stock, w.Stock), Is.Empty);
        }
    }

    [Test]
    public void Delete_ThenInsertSameId_SucceedsCleanly() {
        var t = NewTable();
        var original = Gizmo(id: 1);
        t.Table.Insert(original);
        t.Table.Delete(1);

        var replacement = new Widget(1, "SKU-1B", "Blue", 65);
        var result = t.Table.Insert(replacement);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(t.Table.Get(1).Unwrap(), Is.EqualTo(replacement));
        Assert.That(GetBySku(t, original.Sku).IsError(), Is.True);
        Assert.That(GetBySku(t, replacement.Sku).Unwrap(), Is.EqualTo(replacement));
    }

    [Test]
    public void Delete_AcrossChunkBoundary_SwappedRowFromSecondChunkStaysConsistent() {
        var t = NewTable();
        var widgets = Enumerable.Range(1, 6)
            .Select(i => new Widget(i, $"SKU-{i}", "Red", 50 + i))
            .ToArray();
        foreach (var w in widgets) t.Table.Insert(w);

        t.Table.Delete(2); // physically-last (id 6) swaps into id 2's old slot

        Assert.That(t.Table.Count, Is.EqualTo(5));
        Assert.That(t.Table.Get(2).IsError(), Is.True);
        foreach (var w in widgets.Where(w => w.Id != 2)) {
            Assert.That(t.Table.Get(w.Id).Unwrap(), Is.EqualTo(w));
            Assert.That(GetBySku(t, w.Sku).Unwrap(), Is.EqualTo(w));
            Assert.That(GetByStock(t, w.Stock, w.Stock), Is.EqualTo(new[] { w }));
        }
    }

    // ---- Update ----

    [Test]
    public void Update_UnknownId_ReturnsFailureWithIndexKeyNotFoundException() {
        var t = NewTable();

        var result = t.Table.Update(999, Gizmo(id: 999));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Update_AttemptingToChangePrimaryKey_ReturnsFailureWithPrimaryKeyImmutableException() {
        var t = NewTable();
        var original = Gizmo(id: 1);
        t.Table.Insert(original);

        var result = t.Table.Update(1, original with { Id = 2 });

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<PrimaryKeyImmutableException>());
    }

    [Test]
    public void Update_AttemptingToChangePrimaryKey_LeavesOriginalRowUntouched() {
        var t = NewTable();
        var original = Gizmo(id: 1);
        t.Table.Insert(original);

        t.Table.Update(1, original with { Id = 2 });

        Assert.That(t.Table.Get(1).Unwrap(), Is.EqualTo(original));
        Assert.That(t.Table.Get(2).IsError(), Is.True);
    }

    [Test]
    public void Update_Sku_OldSkuNoLongerResolves_NewSkuDoes() {
        var t = NewTable();
        var original = Gizmo(id: 1);
        t.Table.Insert(original);

        var updated = original with { Sku = "SKU-1-NEW" };
        t.Table.Update(1, updated);

        Assert.That(GetBySku(t, original.Sku).IsError(), Is.True);
        Assert.That(GetBySku(t, updated.Sku).Unwrap(), Is.EqualTo(updated));
        Assert.That(t.Table.Get(1).Unwrap(), Is.EqualTo(updated));
    }

    [Test]
    public void Update_SkuToOneAlreadyUsedByAnotherRow_ReturnsFailureWithDuplicateKeyException() {
        var t = NewTable();
        var a = Gizmo(id: 1);
        var b = new Widget(2, "SKU-2", "Blue", 70);
        t.Table.Insert(a);
        t.Table.Insert(b);

        var result = t.Table.Update(2, b with { Sku = a.Sku });

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void Update_SkuConflict_LeavesTheUpdatedRowFullyUnchanged() {
        var t = NewTable();
        var a = Gizmo(id: 1);
        var b = new Widget(2, "SKU-2", "Blue", 70);
        t.Table.Insert(a);
        t.Table.Insert(b);

        t.Table.Update(2, b with { Sku = a.Sku });

        Assert.That(t.Table.Get(2).Unwrap(), Is.EqualTo(b));
        Assert.That(GetBySku(t, b.Sku).Unwrap(), Is.EqualTo(b));
        Assert.That(GetByCategory(t, b.Category), Is.EqualTo(new[] { b }));
        Assert.That(GetByStock(t, b.Stock, b.Stock), Is.EqualTo(new[] { b }));
    }

    [Test]
    public void Update_SkuConflict_LeavesTheOtherRowFullyUnchanged() {
        var t = NewTable();
        var a = Gizmo(id: 1);
        var b = new Widget(2, "SKU-2", "Blue", 70);
        t.Table.Insert(a);
        t.Table.Insert(b);

        t.Table.Update(2, b with { Sku = a.Sku });

        Assert.That(t.Table.Get(1).Unwrap(), Is.EqualTo(a));
        Assert.That(GetBySku(t, a.Sku).Unwrap(), Is.EqualTo(a));
    }

    [Test]
    public void Update_Category_MovesTheRowToTheNewCategoryBucket() {
        var t = NewTable();
        var original = Gizmo(id: 1, category: "Red");
        t.Table.Insert(original);

        var updated = original with { Category = "Blue" };
        t.Table.Update(1, updated);

        Assert.That(GetByCategory(t, "Red"), Is.Empty);
        Assert.That(GetByCategory(t, "Blue"), Is.EqualTo(new[] { updated }));
    }

    [Test]
    public void Update_Category_OtherRowsSharingTheOldCategoryRemain() {
        var t = NewTable();
        var a = Gizmo(id: 1, category: "Red");
        var b = new Widget(2, "SKU-2", "Red", 70);
        t.Table.Insert(a);
        t.Table.Insert(b);

        t.Table.Update(1, a with { Category = "Blue" });

        Assert.That(GetByCategory(t, "Red"), Is.EqualTo(new[] { b }));
    }

    [Test]
    public void Update_Stock_RangeQueriesReflectTheNewValueNotTheOld() {
        var t = NewTable();
        var original = Gizmo(id: 1, stock: 80);
        t.Table.Insert(original);

        var updated = original with { Stock = 95 };
        t.Table.Update(1, updated);

        Assert.That(GetByStock(t, 80, 80), Is.Empty);
        Assert.That(GetByStock(t, 95, 95), Is.EqualTo(new[] { updated }));
    }

    [Test]
    public void Update_WithAllFieldsIdentical_IsANoOpThatSucceeds() {
        var t = NewTable();
        var original = Gizmo(id: 1);
        t.Table.Insert(original);

        var result = t.Table.Update(1, original);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(t.Table.Get(1).Unwrap(), Is.EqualTo(original));
        Assert.That(GetBySku(t, original.Sku).Unwrap(), Is.EqualTo(original));
        Assert.That(GetByCategory(t, original.Category), Is.EqualTo(new[] { original }));
        Assert.That(GetByStock(t, original.Stock, original.Stock), Is.EqualTo(new[] { original }));
    }

    [Test]
    public void Update_UniqueSecondaryFieldToItsOwnCurrentValue_SucceedsWithoutFalseConflict() {
        // Direct proof of the CheckInsert(row, selfOffset) self-collision fix: setting
        // Sku to the value it already holds (while another field changes) must not
        // report a conflict against the row's own existing index entry. A naive
        // check-before-delete implementation - checking the new value against an
        // index that still holds this row's own old entry - would fail this by
        // reporting a false DuplicateKeyException.
        var t = NewTable();
        var original = Gizmo(id: 1);
        t.Table.Insert(original);

        var updated = original with { Stock = 81, Sku = original.Sku };
        var result = t.Table.Update(1, updated);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(t.Table.Get(1).Unwrap().Sku, Is.EqualTo(original.Sku));
        Assert.That(GetBySku(t, original.Sku).Unwrap().Stock, Is.EqualTo(81));
    }

    [Test]
    public void Update_AllSecondaryKeysAtOnce_EveryIndexReflectsTheChange() {
        var t = NewTable();
        var original = Gizmo(id: 1, category: "Red", stock: 80);
        t.Table.Insert(original);

        var updated = new Widget(1, "SKU-1-NEW", "Blue", 95);
        var result = t.Table.Update(1, updated);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(t.Table.Get(1).Unwrap(), Is.EqualTo(updated));
        Assert.That(GetBySku(t, original.Sku).IsError(), Is.True);
        Assert.That(GetBySku(t, updated.Sku).Unwrap(), Is.EqualTo(updated));
        Assert.That(GetByCategory(t, "Red"), Is.Empty);
        Assert.That(GetByCategory(t, "Blue"), Is.EqualTo(new[] { updated }));
        Assert.That(GetByStock(t, 80, 80), Is.Empty);
        Assert.That(GetByStock(t, 95, 95), Is.EqualTo(new[] { updated }));
    }

    // ---- Mixed operations ----

    [Test]
    public void FullLifecycle_InsertDeleteInsertUpdateDelete_TableEndsConsistentAndEmpty() {
        var t = NewTable();
        var w1 = new Widget(1, "SKU-1", "Red", 80);
        var w2 = new Widget(2, "SKU-2", "Blue", 70);
        var w3 = new Widget(3, "SKU-3", "Red", 90); // physically last after the first three inserts
        var w4 = new Widget(4, "SKU-4", "Green", 60);
        var w5 = new Widget(5, "SKU-5", "Red", 85);

        t.Table.Insert(w1);
        t.Table.Insert(w2);
        t.Table.Insert(w3);
        t.Table.Delete(1); // w3 swaps into w1's old slot
        t.Table.Insert(w4);
        t.Table.Insert(w5);

        var updatedW2 = w2 with { Category = "Purple", Stock = 99 };
        t.Table.Update(2, updatedW2);

        t.Table.Delete(3); // w3 - no longer physically last (w5 is); triggers another swap
        t.Table.Delete(4); // w4 - physically last at this point; no swap

        Assert.That(t.Table.Count, Is.EqualTo(2));
        Assert.That(t.Table.Get(2).Unwrap(), Is.EqualTo(updatedW2));
        Assert.That(t.Table.Get(5).Unwrap(), Is.EqualTo(w5));
        Assert.That(GetByCategory(t, "Red"), Is.EqualTo(new[] { w5 }));
        Assert.That(GetByCategory(t, "Purple"), Is.EqualTo(new[] { updatedW2 }));
        Assert.That(GetByCategory(t, "Green"), Is.Empty);
        Assert.That(GetBySku(t, w3.Sku).IsError(), Is.True);
        Assert.That(GetBySku(t, w4.Sku).IsError(), Is.True);

        t.Table.Delete(2);
        t.Table.Delete(5);

        Assert.That(t.Table.Count, Is.EqualTo(0));
        Assert.That(GetByCategory(t, "Red"), Is.Empty);
        Assert.That(GetByCategory(t, "Purple"), Is.Empty);
    }

    [Test]
    public void ChainedDeletes_MultipleConsecutiveSwaps_AllSurvivorsRemainConsistent() {
        var t = NewTable();
        var widgets = Enumerable.Range(1, 5)
            .Select(i => new Widget(i, $"SKU-{i}", i % 2 == 0 ? "Even" : "Odd", 50 + i))
            .ToArray();
        foreach (var w in widgets) t.Table.Insert(w);

        // Deleting from the front repeatedly forces a fresh swap on every call.
        t.Table.Delete(1);
        t.Table.Delete(2);
        t.Table.Delete(3);

        var survivors = widgets.Where(w => w.Id is 4 or 5).ToArray();
        Assert.That(t.Table.Count, Is.EqualTo(2));
        foreach (var w in survivors) {
            Assert.That(t.Table.Get(w.Id).Unwrap(), Is.EqualTo(w));
            Assert.That(GetBySku(t, w.Sku).Unwrap(), Is.EqualTo(w));
            Assert.That(GetByStock(t, w.Stock, w.Stock), Is.EqualTo(new[] { w }));
        }
        Assert.That(GetByCategory(t, "Even"), Is.EquivalentTo(new[] { widgets[3] }));
        Assert.That(GetByCategory(t, "Odd"), Is.EquivalentTo(new[] { widgets[4] }));
    }

    [Test]
    public void FailedInsertInTheMiddleOfASequence_DoesNotDisturbSurroundingOperations() {
        var t = NewTable();
        t.Table.Insert(new Widget(1, "SKU-1", "Red", 80));
        t.Table.Insert(new Widget(2, "SKU-2", "Blue", 70));

        var dup = t.Table.Insert(new Widget(3, "SKU-1", "Green", 99)); // duplicate sku
        Assert.That(dup.IsError(), Is.True);

        var last = new Widget(4, "SKU-4", "Green", 90);
        t.Table.Insert(last);
        t.Table.Delete(1); // last (physically last) swaps into widget 1's old slot

        Assert.That(t.Table.Count, Is.EqualTo(2));
        Assert.That(t.Table.Get(3).IsError(), Is.True);
        Assert.That(t.Table.Get(2).Unwrap().Sku, Is.EqualTo("SKU-2"));
        Assert.That(t.Table.Get(4).Unwrap(), Is.EqualTo(last));
        Assert.That(GetByCategory(t, "Green"), Is.EqualTo(new[] { last }));
    }

    [Test]
    public void UpdateThenSwapDelete_UpdatedFieldsSurviveTheSwap() {
        var t = NewTable();
        var a = new Widget(1, "SKU-1", "Red", 80);
        var b = new Widget(2, "SKU-2", "Blue", 70);
        var c = new Widget(3, "SKU-3", "Green", 90); // physically last

        t.Table.Insert(a);
        t.Table.Insert(b);
        t.Table.Insert(c);

        var updatedC = c with { Category = "Purple", Stock = 55 };
        t.Table.Update(3, updatedC); // no swap yet - c is already physically last

        t.Table.Delete(1); // c (now Purple/55) swaps into a's old slot

        Assert.That(t.Table.Get(3).Unwrap(), Is.EqualTo(updatedC));
        Assert.That(GetByCategory(t, "Purple"), Is.EqualTo(new[] { updatedC }));
        Assert.That(GetByCategory(t, "Green"), Is.Empty);
        Assert.That(GetByStock(t, 55, 55), Is.EqualTo(new[] { updatedC }));
        Assert.That(GetByStock(t, 90, 90), Is.Empty);
    }

    [Test]
    public void RandomizedMixedSequence_StaysConsistentWithReferenceModelAtEveryStep() {
        // Model-based test: a plain Dictionary tracks what the table *should* contain,
        // and every Insert/Delete/Update the table accepts is mirrored into it. After
        // every step, every accessor is cross-checked against the model. Distinct
        // fixed seed from PlayerTableTests's equivalent so the two suites are
        // independently reproducible.
        var t = NewTable();
        var oracle = new Dictionary<int, Widget>();
        var rng = new Random(20260905);
        var nextId = 1;
        var categories = new[] { "Red", "Blue", "Green", "Purple", "Yellow" };

        for (var step = 0; step < 300; step++) {
            switch (rng.Next(3)) {
                case 0: {
                    var id = nextId++;
                    var widget = new Widget(id, $"SKU-{id}", categories[rng.Next(categories.Length)], rng.Next(40, 100));
                    if (t.Table.Insert(widget).IsOk()) oracle[id] = widget;
                    break;
                }
                case 1: {
                    if (oracle.Count == 0) break;
                    var id = oracle.Keys.ElementAt(rng.Next(oracle.Count));
                    if (t.Table.Delete(id).IsOk()) oracle.Remove(id);
                    break;
                }
                case 2: {
                    if (oracle.Count == 0) break;
                    var id = oracle.Keys.ElementAt(rng.Next(oracle.Count));
                    var updated = oracle[id] with {
                        Category = categories[rng.Next(categories.Length)],
                        Stock = rng.Next(40, 100)
                    };
                    if (t.Table.Update(id, updated).IsOk()) oracle[id] = updated;
                    break;
                }
            }

            Assert.That(t.Table.Count, Is.EqualTo(oracle.Count), $"Count mismatch after step {step}");
            foreach (var (id, expected) in oracle) {
                Assert.That(t.Table.Get(id).Unwrap(), Is.EqualTo(expected), $"Get({id}) wrong after step {step}");
                Assert.That(GetBySku(t, expected.Sku).Unwrap(), Is.EqualTo(expected), $"GetBySku({expected.Sku}) wrong after step {step}");
            }
            foreach (var category in categories) {
                var expected = oracle.Values.Where(w => w.Category == category);
                Assert.That(GetByCategory(t, category), Is.EquivalentTo(expected), $"GetByCategory({category}) wrong after step {step}");
            }
            Assert.That(GetByStock(t, 0, 200), Is.EquivalentTo(oracle.Values), $"GetByStock(full range) wrong after step {step}");
        }
    }
}
