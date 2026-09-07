using RhinoDB.Core;
using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Indexing;

namespace RhinoDB.Lib.Tables.Test;

// InstantTable<TKey,TRow> is pure delegation onto Table<TKey,TRow> - the instant
// counterpart to PersistentTable<TKey,TRow> (Cold/PersistentTableTests.cs), giving
// the table-source-generator a symmetric contract to switch on. Table<TKey,TRow>'s
// own logic (swap-remove repoint, self-collision, chunk boundaries, etc.) is already
// proven by TableTests.cs - this suite only proves the wiring is correct, not that
// Table itself works.
public class InstantTableTests {
    private readonly record struct Gadget(int Id, string Sku, int Stock);

    static private InstantTable<int, Gadget> NewTable() {
        var table = new InstantTable<int, Gadget>(chunkSize: 4, new HashIndex<int>(), static g => g.Id);
        table.Register(new UniqueSecondaryIndex<Gadget, string>(new HashIndex<string>(), static g => g.Sku));
        return table;
    }

    [Test]
    public void Insert_ThenGet_ReturnsTheInsertedRow() {
        var t = NewTable();
        var gadget = new Gadget(1, "SKU-1", 10);

        Result result = t.Insert(gadget);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(t.Get(1).Unwrap(), Is.EqualTo(gadget));
        Assert.That(t.Count, Is.EqualTo(1));
    }

    [Test]
    public void Insert_DuplicatePrimaryKey_ReturnsFailureWithDuplicateKeyException() {
        var t = NewTable();
        t.Insert(new Gadget(1, "SKU-1", 10));

        Result result = t.Insert(new Gadget(1, "SKU-2", 20));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void Insert_DuplicateSecondaryKey_ReturnsFailureAndDoesNotInsert() {
        var t = NewTable();
        t.Insert(new Gadget(1, "SKU-1", 10));

        Result result = t.Insert(new Gadget(2, "SKU-1", 20));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<DuplicateKeyException>());
        Assert.That(t.Count, Is.EqualTo(1));
    }

    [Test]
    public void GetByOffset_AfterInsert_ReturnsTheSameRowAsGet() {
        var t = NewTable();
        var gadget = new Gadget(1, "SKU-1", 10);
        t.Insert(gadget);

        var offset = default(int);
        // Only way to obtain a known-good offset through the public surface is via
        // a fresh insert into an empty table - offset 0 is guaranteed here.
        Assert.That(t.GetByOffset(offset), Is.EqualTo(gadget));
    }

    [Test]
    public void Update_ChangesTheRow_GetReflectsTheNewValue() {
        var t = NewTable();
        t.Insert(new Gadget(1, "SKU-1", 10));

        Result result = t.Update(1, new Gadget(1, "SKU-1", 99));

        Assert.That(result.IsOk(), Is.True);
        Assert.That(t.Get(1).Unwrap().Stock, Is.EqualTo(99));
    }

    [Test]
    public void Update_UnknownId_ReturnsFailureWithIndexKeyNotFoundException() {
        var t = NewTable();

        Result result = t.Update(999, new Gadget(999, "SKU-999", 1));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Delete_ExistingRow_RemovesItAndDecrementsCount() {
        var t = NewTable();
        t.Insert(new Gadget(1, "SKU-1", 10));

        Result result = t.Delete(1);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(t.Count, Is.EqualTo(0));
        Assert.That(t.Get(1).IsError(), Is.True);
    }

    [Test]
    public void Delete_UnknownId_ReturnsFailureWithIndexKeyNotFoundException() {
        var t = NewTable();

        Result result = t.Delete(999);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Count_TracksInsertsAndDeletes() {
        var t = NewTable();

        t.Insert(new Gadget(1, "SKU-1", 10));
        t.Insert(new Gadget(2, "SKU-2", 20));
        t.Delete(1);

        Assert.That(t.Count, Is.EqualTo(1));
    }
}
