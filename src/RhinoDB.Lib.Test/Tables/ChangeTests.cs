namespace RhinoDB.Lib.Tables.Test;

// Change<TKey,TRow> is the shared primitive both Part G's write-staging path and
// Stage 6's propagation build on - a plain, non-boxing data carrier with zero logic
// of its own, so this suite only proves the shape, not any behavior.
public class ChangeTests {
    private readonly record struct Gadget(int Id, string Sku, int Stock);

    static private readonly Gadget Widget = new Gadget(1, "SKU-1", 10);

    [Test]
    public void Insert_CarriesKindKeyAndRow() {
        var change = new Change<int, Gadget>(ChangeKind.Insert, 1, Widget);

        Assert.That(change.Kind, Is.EqualTo(ChangeKind.Insert));
        Assert.That(change.Key, Is.EqualTo(1));
        Assert.That(change.Row, Is.EqualTo(Widget));
    }

    [Test]
    public void Update_CarriesKindKeyAndRow() {
        var change = new Change<int, Gadget>(ChangeKind.Update, 1, Widget);

        Assert.That(change.Kind, Is.EqualTo(ChangeKind.Update));
        Assert.That(change.Row, Is.EqualTo(Widget));
    }

    [Test]
    public void Delete_StillCarriesTheRowsLastKnownContent() {
        // A Delete must not be given default(TRow) - the staging caller is expected
        // to capture the row's value before it disappears, since nothing downstream
        // (propagation, read-your-own-writes overlay matching) can recover it otherwise.
        var change = new Change<int, Gadget>(ChangeKind.Delete, 1, Widget);

        Assert.That(change.Row, Is.EqualTo(Widget));
    }

    [Test]
    public void IsAReadonlyValueType_NoAllocationPerChange() {
        Assert.That(typeof(Change<int, Gadget>).IsValueType, Is.True);
    }
}
