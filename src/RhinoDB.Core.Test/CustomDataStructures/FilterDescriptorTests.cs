namespace RhinoDB.Core.CustomDataStructures.Test;

// FilterDescriptor is the include/exclude predicate every index scan filters through
// (GetOffsetsIter walks live offsets, GetOffsetsExcept passes Exclude(theKey)) - so an
// inverted predicate would silently drop or duplicate rows on every scan of every table.
public class FilterDescriptorTests {
    [Test]
    public void Include_SelectsOnlyTheMatchingKey() {
        var filter = FilterDescriptor.Include(7);

        Assert.Multiple(() => {
            Assert.That(filter.MustInclude(7), Is.True);
            Assert.That(filter.MustExclude(7), Is.False);
            Assert.That(filter.MustInclude(8), Is.False);
            Assert.That(filter.MustExclude(8), Is.True, "a non-matching entry must be excluded by an Include filter.");
        });
    }

    [Test]
    public void Exclude_DropsOnlyTheMatchingKey() {
        var filter = FilterDescriptor.Exclude(7);

        Assert.Multiple(() => {
            Assert.That(filter.MustExclude(7), Is.True);
            Assert.That(filter.MustInclude(7), Is.False);
            Assert.That(filter.MustExclude(8), Is.False);
            Assert.That(filter.MustInclude(8), Is.True, "an Exclude filter must keep every non-matching entry.");
        });
    }

    [Test]
    public void IncludeAndExclude_AreExactComplementsForBothInputs() {
        var include = FilterDescriptor.Include(7);
        var exclude = FilterDescriptor.Exclude(7);

        foreach (var key in new[] { 7, 8 }) {
            Assert.Multiple(() => {
                Assert.That(include.MustInclude(key), Is.Not.EqualTo(exclude.MustInclude(key)));
                Assert.That(include.MustExclude(key), Is.Not.EqualTo(exclude.MustExclude(key)));
            });
        }
    }

    [Test]
    public void TheGenericFactory_BehavesIdenticallyToTheStructFactory() {
        var viaStruct = FilterDescriptor<int>.Exclude(7);
        var viaFactory = FilterDescriptor.Exclude(7);

        Assert.Multiple(() => {
            Assert.That(viaFactory.MustExclude(7), Is.EqualTo(viaStruct.MustExclude(7)));
            Assert.That(viaFactory.MustInclude(9), Is.EqualTo(viaStruct.MustInclude(9)));
        });
    }

    [Test]
    public void StringKeys_CompareByValue() {
        var filter = FilterDescriptor.Exclude("Rome");

        Assert.Multiple(() => {
            Assert.That(filter.MustExclude("Rome"), Is.True);
            Assert.That(filter.MustExclude(new string(['R', 'o', 'm', 'e'])), Is.True,
                "a different string instance with the same content is the same key.");
            Assert.That(filter.MustInclude("Milan"), Is.True);
        });
    }

    [Test]
    public void DefaultDescriptor_ExcludesEverythingBecauseNoKeyWasChosen() {
        // default(FilterDescriptor<T>) has include=false and key=default - MustExclude compares
        // against default(T), so only a default-valued entry matches. Pinning it because a
        // default-constructed filter reaching a scan would silently drop exactly one row.
        var filter = default(FilterDescriptor<int>);

        Assert.Multiple(() => {
            Assert.That(filter.MustExclude(0), Is.True);
            Assert.That(filter.MustInclude(1), Is.True);
        });
    }

    [Test]
    public void IsIncludeAndKey_ExposeHowTheDescriptorWasBuilt() {
        // Ordered indexes read these to turn an Except/Include scan into "copy the ranges around
        // the key's run" instead of testing every entry, so they must mirror the factory used.
        var include = FilterDescriptor.Include("Rome");
        var exclude = FilterDescriptor.Exclude(42);

        Assert.Multiple(() => {
            Assert.That(include.IsInclude, Is.True);
            Assert.That(include.Key, Is.EqualTo("Rome"));
            Assert.That(exclude.IsInclude, Is.False);
            Assert.That(exclude.Key, Is.EqualTo(42));
        });
    }
}
