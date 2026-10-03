namespace RhinoDB.Lib.Indexing.Test;

// The struct comparers are the TCmp type argument of every BTree index, so their ordering IS
// the index's ordering. These pin the semantics the generator relies on when it picks one.
public class IndexComparersTests {
    static private int Sign(int value) => Math.Sign(value);

    [Test]
    public void OrdinalString_OrdersByCodeUnit_NotByCulture() {
        var cmp = default(OrdinalStringComparer);

        // 'Z' (0x5A) sorts before 'a' (0x61) ordinally; a culture comparer puts "a" first.
        Assert.That(Sign(cmp.Compare("Z", "a")), Is.EqualTo(-1));
        Assert.That(Sign(cmp.Compare("a", "Z")), Is.EqualTo(1));
        Assert.That(cmp.Compare("same", "same"), Is.EqualTo(0));
        Assert.That(Sign(cmp.Compare("ab", "abc")), Is.EqualTo(-1), "a prefix sorts first");
    }

    [Test]
    public void OrdinalString_OrdersNullBeforeEverything() {
        var cmp = default(OrdinalStringComparer);

        Assert.That(cmp.Compare(null, null), Is.EqualTo(0));
        Assert.That(Sign(cmp.Compare(null, "")), Is.EqualTo(-1));
        Assert.That(Sign(cmp.Compare("", null)), Is.EqualTo(1));
    }

    [Test]
    public void Default_MatchesComparerDefault_ForValueAndReferenceTypes() {
        Assert.That(Sign(default(DefaultComparer<int>).Compare(1, 2)), Is.EqualTo(-1));
        Assert.That(Sign(default(DefaultComparer<long>).Compare(long.MaxValue, long.MinValue)), Is.EqualTo(1));
        Assert.That(default(DefaultComparer<Guid>).Compare(Guid.Empty, Guid.Empty), Is.EqualTo(0));
        Assert.That(Sign(default(DefaultComparer<string>).Compare(null, "x")), Is.EqualTo(-1), "null-safe like Comparer<T>.Default");
    }

    [Test]
    public void Default_WorksForEnums_WhichHaveNoGenericIComparable() {
        // Enums only implement the non-generic IComparable, so a "where T : IComparable<T>"
        // comparer could not be used for an enum field inside a composite index key.
        var cmp = default(DefaultComparer<DayOfWeek>);

        Assert.That(Sign(cmp.Compare(DayOfWeek.Monday, DayOfWeek.Friday)), Is.EqualTo(-1));
        Assert.That(cmp.Compare(DayOfWeek.Sunday, DayOfWeek.Sunday), Is.EqualTo(0));
    }

    [Test]
    public void Tuple2_ComparesTheFirstElementFirst_ThenTheSecondOrdinally() {
        var cmp = default(TupleComparer<int, string, DefaultComparer<int>, OrdinalStringComparer>);

        Assert.That(Sign(cmp.Compare((1, "z"), (2, "a"))), Is.EqualTo(-1), "the first element decides");
        Assert.That(Sign(cmp.Compare((1, "Z"), (1, "a"))), Is.EqualTo(-1), "the second element is ordinal");
        Assert.That(cmp.Compare((1, "a"), (1, "a")), Is.EqualTo(0));
    }

    [Test]
    public void Tuple3_FallsThroughEachElementInOrder() {
        var cmp = default(TupleComparer<int, string, DayOfWeek, DefaultComparer<int>, OrdinalStringComparer, DefaultComparer<DayOfWeek>>);

        Assert.That(Sign(cmp.Compare((1, "a", DayOfWeek.Friday), (1, "b", DayOfWeek.Monday))), Is.EqualTo(-1));
        Assert.That(Sign(cmp.Compare((1, "a", DayOfWeek.Friday), (1, "a", DayOfWeek.Monday))), Is.EqualTo(1));
        Assert.That(cmp.Compare((1, "a", DayOfWeek.Monday), (1, "a", DayOfWeek.Monday)), Is.EqualTo(0));
    }

    [Test]
    public void TupleComparer_AcceptsNamedTupleKeys() {
        // Generated index keys are named tuples like (int ClubId, string Name); names are erased,
        // so the comparer must slot in as the TCmp of an index over the named shape.
        var index = new BTreeIndex<(int ClubId, string Name), TupleComparer<int, string, DefaultComparer<int>, OrdinalStringComparer>>();
        index.Insert((1, "b"), 2);
        index.Insert((1, "B"), 1);
        index.Insert((0, "z"), 0);

        using var scan = index.GetOffsetsIter();
        Assert.That(scan.Buffer().ToArray(), Is.EqualTo(new[] { 0, 1, 2 }));
    }
}
