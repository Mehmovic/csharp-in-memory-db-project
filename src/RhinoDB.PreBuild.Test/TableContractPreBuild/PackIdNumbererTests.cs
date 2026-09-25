using System.Collections.Immutable;
using RhinoDB.PreBuild;

namespace RhinoDB.Test.PreBuild;

public class PackIdNumbererTests {
    static ParsedShorthandField Field(string name, int? explicitPackId = null) =>
        new(name, "int", ImmutableArray<string>.Empty, explicitPackId);

    [Test]
    public void Assign_NoExplicitPackIdAnywhere_ForwardFillsFromZero() {
        var fields = ImmutableArray.Create(Field("A"), Field("B"), Field("C"));

        var result = PackIdNumberer.Assign(fields);

        Assert.That(result, Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void Assign_OneUnlabeledFieldBeforeAnExplicitAnchor_BackwardFillsThenNormalizes() {
        // [PrimaryKey] int Id, [PackId(0)] long Value - the plan's own worked example.
        var fields = ImmutableArray.Create(Field("Id"), Field("Value", explicitPackId: 0));

        var result = PackIdNumberer.Assign(fields);

        Assert.That(result, Is.EqualTo(new[] { 0, 1 }));
    }

    [Test]
    public void Assign_TwoUnlabeledFieldsBeforeAnExplicitAnchor_BackwardFillsBothThenNormalizes() {
        // raw -2, -1, 0 -> real 0, 1, 2, per the plan's own worked example.
        var fields = ImmutableArray.Create(Field("A"), Field("B"), Field("C", explicitPackId: 0));

        var result = PackIdNumberer.Assign(fields);

        Assert.That(result, Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void Assign_UnlabeledFieldsAfterAnAnchor_ForwardFillFromIt() {
        var fields = ImmutableArray.Create(Field("A", explicitPackId: 5), Field("B"), Field("C"));

        var result = PackIdNumberer.Assign(fields);

        // raw 5, 6, 7 -> normalized to 0, 1, 2.
        Assert.That(result, Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void Assign_MultipleExplicitAnchorsInAscendingOrder_EachResetsTheForwardFillBase() {
        var fields = ImmutableArray.Create(Field("A", explicitPackId: 3), Field("B", explicitPackId: 4));

        var result = PackIdNumberer.Assign(fields);

        Assert.That(result, Is.EqualTo(new[] { 0, 1 }));
    }

    [Test]
    public void Assign_TwoFieldsWithTheSameExplicitPackId_ThrowsCollision() {
        var fields = ImmutableArray.Create(Field("A", explicitPackId: 0), Field("B", explicitPackId: 0));

        Assert.Throws<PackIdCollisionException>(() => PackIdNumberer.Assign(fields));
    }

    [Test]
    public void Assign_ForwardFilledFieldCollidesWithALaterExplicitAnchor_ThrowsCollision() {
        // A=0 (explicit, anchor), B forward-fills to 1, C explicitly claims 1 too.
        var fields = ImmutableArray.Create(Field("A", explicitPackId: 0), Field("B"), Field("C", explicitPackId: 1));

        Assert.Throws<PackIdCollisionException>(() => PackIdNumberer.Assign(fields));
    }

    [Test]
    public void Assign_EmptyFieldList_ReturnsEmpty() {
        var result = PackIdNumberer.Assign(ImmutableArray<ParsedShorthandField>.Empty);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Assign_NormalizedRangeExactlyFitsAByte_DoesNotThrow() {
        // raw 0, 255 -> normalized 0, 255 - exactly byte.MaxValue, the boundary that must still succeed.
        var fields = ImmutableArray.Create(Field("A", explicitPackId: 0), Field("B", explicitPackId: 255));

        var result = PackIdNumberer.Assign(fields);

        Assert.That(result, Is.EqualTo(new byte[] { 0, 255 }));
    }

    [Test]
    public void Assign_NormalizedRangeExceedsAByte_ThrowsPackIdRange() {
        // Two explicit anchors far apart - no single field claims an out-of-range PackId, but the gap
        // between anchors still produces a normalized slot > byte.MaxValue once resolved.
        var fields = ImmutableArray.Create(Field("A", explicitPackId: 0), Field("B", explicitPackId: 256));

        Assert.Throws<PackIdRangeException>(() => PackIdNumberer.Assign(fields));
    }
}
