namespace RhinoDB.Lib.Indexing.Test;

// Composite keys whose second column is a *reference* type. (int, string) falls out
// of TKey being generic exactly the way (int, int) does, but it exercises two extra
// things: string equality (case-sensitive, ordinal) on the hash/equality side of
// every index, and string *ordering* on the ordered indexes.
//
// Ordering note: the ordered indexes compare through Comparer<TKey>.Default, so the
// string column goes through string.CompareTo (culture-aware). Every name used for
// ordering assertions here is plain lowercase ASCII, so the expected order holds
// under both culture-aware and ordinal comparison; the case-sensitivity split is
// asserted separately rather than baked into an ordering expectation.
//
// Chunk-based indexes use chunkSize: 16 in the multi-chunk tests so a few hundred
// entries already span tens of chunks - the binary-search start path and the
// cross-chunk duplicate walk get real work to do without a slow test.
public class HashIndex_CompositeStringKeyTests {
    static private HashIndex<(int ClubId, string Name)> NewIndex() => new();

    [Test]
    public void Insert_DifferentNameSameClub_BothRetrievable() {
        var index = NewIndex();
        index.Insert((5, "alice"), 0);
        index.Insert((5, "bob"), 1);

        Assert.That(index.GetOffset((5, "alice")).Unwrap(), Is.EqualTo(0));
        Assert.That(index.GetOffset((5, "bob")).Unwrap(), Is.EqualTo(1));
    }

    [Test]
    public void Insert_DifferentClubSameName_BothRetrievable() {
        var index = NewIndex();
        index.Insert((5, "alice"), 0);
        index.Insert((6, "alice"), 1);

        Assert.That(index.GetOffset((5, "alice")).Unwrap(), Is.EqualTo(0));
        Assert.That(index.GetOffset((6, "alice")).Unwrap(), Is.EqualTo(1));
    }

    [Test]
    public void GetOffset_PartialMatchOnlyClub_ReturnsFailure() {
        var index = NewIndex();
        index.Insert((5, "alice"), 0);

        Assert.That(index.GetOffset((5, "bob")).IsError(), Is.True);
    }

    [Test]
    public void GetOffset_SameClubDifferentNameCase_IsADistinctKey() {
        // ValueTuple equality delegates to EqualityComparer<string>.Default, which is
        // ordinal - "alice" and "Alice" are two different composite keys.
        var index = NewIndex();
        index.Insert((5, "alice"), 0);
        index.Insert((5, "Alice"), 1);

        Assert.That(index.GetOffset((5, "alice")).Unwrap(), Is.EqualTo(0));
        Assert.That(index.GetOffset((5, "Alice")).Unwrap(), Is.EqualTo(1));
        Assert.That(index.Count, Is.EqualTo(2));
    }

    [Test]
    public void Delete_RemovesOnlyTheExactPair() {
        var index = NewIndex();
        index.Insert((5, "alice"), 0);
        index.Insert((5, "bob"), 1);

        index.Delete((5, "alice"));

        Assert.That(index.GetOffset((5, "alice")).IsError(), Is.True);
        Assert.That(index.GetOffset((5, "bob")).Unwrap(), Is.EqualTo(1));
    }
}

public class NonUniqueHashIndex_CompositeStringKeyTests {
    static private NonUniqueHashIndex<(int ClubId, string Name)> NewIndex() => new();

    static private int[] OffsetsOf(NonUniqueHashIndex<(int ClubId, string Name)> index, (int ClubId, string Name) key) {
        using var list = index.GetOffsets(key);
        return list.BufferResult().Unwrap().ToArray();
    }

    [Test]
    public void GetOffsets_SamePairTwice_ReturnsBothOffsets() {
        var index = NewIndex();
        index.Insert((5, "alice"), 1);
        index.Insert((5, "alice"), 2);

        Assert.That(OffsetsOf(index, (5, "alice")), Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void GetOffsets_SameClubDifferentName_AreSeparateBuckets() {
        var index = NewIndex();
        index.Insert((5, "alice"), 1);
        index.Insert((5, "bob"), 2);

        Assert.That(OffsetsOf(index, (5, "alice")), Is.EqualTo(new[] { 1 }));
        Assert.That(OffsetsOf(index, (5, "bob")), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void GetOffsets_NameCaseDifference_IsADifferentBucket() {
        var index = NewIndex();
        index.Insert((5, "alice"), 1);
        index.Insert((5, "Alice"), 2);

        Assert.That(OffsetsOf(index, (5, "alice")), Is.EqualTo(new[] { 1 }));
        Assert.That(OffsetsOf(index, (5, "Alice")), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void Delete_RemovesOnlyTheGivenOffsetFromThePair() {
        var index = NewIndex();
        index.Insert((5, "alice"), 1);
        index.Insert((5, "alice"), 2);

        index.Delete((5, "alice"), 1);

        Assert.That(OffsetsOf(index, (5, "alice")), Is.EqualTo(new[] { 2 }));
    }
}

public class BTreeIndex_CompositeStringKeyTests {
    static private BTreeIndex<(int ClubId, string Name)> NewIndex(int chunkSize = 256) => new(chunkSize);

    static private int[] RangeOf(BTreeIndex<(int ClubId, string Name)> index, (int, string) from, (int, string) to) {
        using var list = index.Range(from, to);
        return list.BufferResult().Unwrap().ToArray();
    }

    [Test]
    public void Insert_ThenGetOffset_ResolvesEveryPair() {
        var index = NewIndex();
        index.Insert((2, "beta"), 20);
        index.Insert((1, "alpha"), 10);
        index.Insert((1, "beta"), 11);

        Assert.That(index.GetOffset((1, "alpha")).Unwrap(), Is.EqualTo(10));
        Assert.That(index.GetOffset((1, "beta")).Unwrap(), Is.EqualTo(11));
        Assert.That(index.GetOffset((2, "beta")).Unwrap(), Is.EqualTo(20));
        Assert.That(index.Count, Is.EqualTo(3));
    }

    [Test]
    public void GetOffset_WhenTheClubMatchesButTheNameIsDifferent_ReturnsFailure() {
        var index = NewIndex();
        index.Insert((1, "alpha"), 10);

        Assert.That(index.GetOffset((1, "beta")).IsError(), Is.True);
        Assert.That(index.GetOffset((2, "alpha")).IsError(), Is.True);
    }

    [Test]
    public void Range_OrdersByClubFirstThenName() {
        var index = NewIndex();
        index.Insert((2, "alpha"), 20);
        index.Insert((1, "beta"), 11);
        index.Insert((1, "alpha"), 10);

        Assert.That(RangeOf(index, (1, "alpha"), (2, "alpha")), Is.EqualTo(new[] { 10, 11, 20 }));
    }

    [Test]
    public void Range_PointBound_ReturnsJustThatPair() {
        var index = NewIndex();
        index.Insert((1, "alpha"), 10);
        index.Insert((1, "beta"), 11);

        Assert.That(RangeOf(index, (1, "beta"), (1, "beta")), Is.EqualTo(new[] { 11 }));
    }

    [Test]
    public void Range_LeftmostPrefixQuery_ReturnsOnlyThatClub() {
        // The composite idiom: fix the club, range over the name.
        var index = NewIndex();
        index.Insert((1, "alpha"), 10);
        index.Insert((1, "beta"), 11);
        index.Insert((2, "alpha"), 20);

        Assert.That(RangeOf(index, (1, "a"), (1, "z")), Is.EqualTo(new[] { 10, 11 }));
    }

    [Test]
    public void Range_AcrossManyChunks_ResolvesEveryPair() {
        // chunkSize 16 with 400 keys => ~25 chunks, so the start-chunk binary search
        // really has to find a chunk that is not the first one.
        var index = NewIndex(chunkSize: 16);
        for (var i = 0; i < 400; i++)
            index.Insert((i / 4, $"n{i % 4:000}"), i);

        Assert.That(index.GetOffset((99, "n003")).Unwrap(), Is.EqualTo(399));
        Assert.That(index.GetOffset((50, "n000")).Unwrap(), Is.EqualTo(200));
        Assert.That(RangeOf(index, (10, "n000"), (10, "n003")), Is.EqualTo(new[] { 40, 41, 42, 43 }));
        Assert.That(RangeOf(index, (0, "n000"), (99, "n003")), Has.Length.EqualTo(400));
    }

    [Test]
    public void Gt_Gte_Lt_Lte_RespectTheCompositeBoundary() {
        var index = NewIndex();
        index.Insert((1, "alpha"), 10);
        index.Insert((1, "beta"), 11);
        index.Insert((2, "alpha"), 20);

        using var greater = index.Gt((1, "alpha"));
        using var atOrGreater = index.Gte((1, "alpha"));
        using var less = index.Lt((2, "alpha"));
        using var atOrLess = index.Lte((2, "alpha"));

        Assert.That(greater.BufferResult().Unwrap().ToArray(), Is.EqualTo(new[] { 11, 20 }));
        Assert.That(atOrGreater.BufferResult().Unwrap().ToArray(), Is.EqualTo(new[] { 10, 11, 20 }));
        Assert.That(less.BufferResult().Unwrap().ToArray(), Is.EqualTo(new[] { 10, 11 }));
        Assert.That(atOrLess.BufferResult().Unwrap().ToArray(), Is.EqualTo(new[] { 10, 11, 20 }));
    }

    [Test]
    public void Iter_ReturnsEveryPairInClubThenNameOrder() {
        var index = NewIndex();
        index.Insert((2, "alpha"), 20);
        index.Insert((1, "beta"), 11);
        index.Insert((1, "alpha"), 10);

        using var all = index.Iter();
        Assert.That(all.BufferResult().Unwrap().ToArray(), Is.EqualTo(new[] { 10, 11, 20 }));
    }

    [Test]
    public void Delete_ThenReinsertTheSamePair_Succeeds() {
        var index = NewIndex();
        index.Insert((1, "alpha"), 10);

        index.Delete((1, "alpha"));
        index.Insert((1, "alpha"), 42);

        Assert.That(index.GetOffset((1, "alpha")).Unwrap(), Is.EqualTo(42));
        Assert.That(index.Count, Is.EqualTo(1));
    }
}

public class NonUniqueBTreeIndex_CompositeStringKeyTests {
    static private NonUniqueBTreeIndex<(int ClubId, string Name)> NewIndex(int chunkSize = 256) => new(chunkSize);

    static private int[] OffsetsOf(NonUniqueBTreeIndex<(int ClubId, string Name)> index, (int ClubId, string Name) key) {
        using var list = index.GetOffsets(key);
        return list.BufferResult().Unwrap().ToArray();
    }

    [Test]
    public void GetOffsets_ReturnsOnlyTheExactPair() {
        var index = NewIndex();
        index.Insert((5, "alice"), 1);
        index.Insert((5, "bob"), 2);
        index.Insert((6, "alice"), 3);

        Assert.That(OffsetsOf(index, (5, "alice")), Is.EqualTo(new[] { 1 }));
        Assert.That(OffsetsOf(index, (5, "bob")), Is.EqualTo(new[] { 2 }));
        Assert.That(OffsetsOf(index, (6, "alice")), Is.EqualTo(new[] { 3 }));
    }

    [Test]
    public void GetOffsets_DuplicateRunSpanningManyChunks_ReturnsEveryOffset() {
        // 600 duplicates of one composite key with chunkSize 16 => the run spans
        // dozens of chunks, so the cross-chunk duplicate walk must cover them all.
        var index = NewIndex(chunkSize: 16);
        for (var i = 0; i < 600; i++)
            index.Insert((7, "same"), i);
        index.Insert((8, "other"), 600);

        Assert.That(OffsetsOf(index, (7, "same")), Is.EquivalentTo(Enumerable.Range(0, 600).ToArray()));
        Assert.That(OffsetsOf(index, (8, "other")), Is.EqualTo(new[] { 600 }));
    }

    [Test]
    public void Gt_SkipsEveryDuplicateOfTheBoundPair() {
        var index = NewIndex(chunkSize: 16);
        for (var i = 0; i < 40; i++)
            index.Insert((7, "same"), i);
        index.Insert((9, "later"), 999);

        using var above = index.Gt((7, "same"));
        Assert.That(above.BufferResult().Unwrap().ToArray(), Is.EqualTo(new[] { 999 }));
    }

    [Test]
    public void Lt_ExcludesEveryDuplicateOfTheBoundPair() {
        var index = NewIndex(chunkSize: 16);
        index.Insert((1, "first"), 111);
        for (var i = 0; i < 40; i++)
            index.Insert((7, "same"), i);

        using var below = index.Lt((7, "same"));
        Assert.That(below.BufferResult().Unwrap().ToArray(), Is.EqualTo(new[] { 111 }));
    }

    [Test]
    public void Delete_OffsetInALaterChunkOfADuplicateRun_RemovesOnlyThatOffset() {
        var index = NewIndex(chunkSize: 16);
        for (var i = 0; i < 100; i++)
            index.Insert((7, "same"), i);

        index.Delete((7, "same"), 99);

        var remaining = OffsetsOf(index, (7, "same"));
        Assert.That(remaining, Has.Length.EqualTo(99));
        Assert.That(remaining, Does.Not.Contain(99));
    }
}

public class RedBlackTreeIndex_CompositeStringKeyTests {
    static private RedBlackTreeIndex<(int ClubId, string Name)> NewIndex() => new();

    static private int[] RangeOf(RedBlackTreeIndex<(int ClubId, string Name)> index, (int, string) from, (int, string) to) {
        using var list = index.Range(from, to);
        return list.BufferResult().Unwrap().ToArray();
    }

    [Test]
    public void Range_OrdersByClubFirstThenName() {
        var index = NewIndex();
        index.Insert((2, "alpha"), 20);
        index.Insert((1, "beta"), 11);
        index.Insert((1, "alpha"), 10);

        Assert.That(RangeOf(index, (1, "alpha"), (2, "alpha")), Is.EqualTo(new[] { 10, 11, 20 }));
    }

    [Test]
    public void Range_LeftmostPrefixQuery_ReturnsOnlyThatClub() {
        var index = NewIndex();
        index.Insert((1, "alpha"), 10);
        index.Insert((1, "beta"), 11);
        index.Insert((2, "alpha"), 20);

        Assert.That(RangeOf(index, (1, "a"), (1, "z")), Is.EqualTo(new[] { 10, 11 }));
    }

    [Test]
    public void Gte_FromAnEmptyName_IncludesTheWholeClub() {
        // (club, "") sorts before every non-empty name of that club, so it is the
        // natural left edge for a leftmost-prefix query with an open name bound.
        var index = NewIndex();
        index.Insert((1, "alpha"), 10);
        index.Insert((2, "alpha"), 20);

        using var clubOne = index.Gte((1, ""));
        Assert.That(clubOne.BufferResult().Unwrap().ToArray(), Is.EqualTo(new[] { 10, 20 }));

        using var clubTwo = index.Gte((2, ""));
        Assert.That(clubTwo.BufferResult().Unwrap().ToArray(), Is.EqualTo(new[] { 20 }));
    }

    [Test]
    public void Iter_ReturnsEveryPairInSortedOrder() {
        var index = NewIndex();
        index.Insert((3, "alpha"), 30);
        index.Insert((1, "beta"), 11);
        index.Insert((1, "alpha"), 10);
        index.Insert((2, "alpha"), 20);

        using var all = index.Iter();
        Assert.That(all.BufferResult().Unwrap().ToArray(), Is.EqualTo(new[] { 10, 11, 20, 30 }));
    }

    [Test]
    public void Range_WithAnInvertedCompositeSpan_ReturnsEmpty() {
        var index = NewIndex();
        index.Insert((1, "alpha"), 10);

        Assert.That(RangeOf(index, (2, "alpha"), (1, "alpha")), Is.Empty);
    }
}

public class NonUniqueRedBlackTreeIndex_CompositeStringKeyTests {
    static private NonUniqueRedBlackTreeIndex<(int ClubId, string Name)> NewIndex() => new();

    static private int[] OffsetsOf(NonUniqueRedBlackTreeIndex<(int ClubId, string Name)> index, (int ClubId, string Name) key) {
        using var list = index.GetOffsets(key);
        return list.BufferResult().Unwrap().ToArray();
    }

    [Test]
    public void GetOffsets_SamePairTwice_ReturnsBothOffsets() {
        var index = NewIndex();
        index.Insert((5, "alice"), 1);
        index.Insert((5, "alice"), 2);

        Assert.That(OffsetsOf(index, (5, "alice")), Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void Gte_Lte_IncludeEveryDuplicateOfTheBoundPair() {
        var index = NewIndex();
        index.Insert((5, "alice"), 1);
        index.Insert((5, "alice"), 2);
        index.Insert((5, "bob"), 3);

        using var atOrAbove = index.Gte((5, "alice"));
        Assert.That(atOrAbove.BufferResult().Unwrap().ToArray(), Is.EquivalentTo(new[] { 1, 2, 3 }));

        using var atOrBelow = index.Lte((5, "alice"));
        Assert.That(atOrBelow.BufferResult().Unwrap().ToArray(), Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void Range_LeftmostPrefixQuery_ReturnsBothDuplicatesOfAClub() {
        var index = NewIndex();
        index.Insert((1, "alpha"), 10);
        index.Insert((1, "alpha"), 11);
        index.Insert((1, "beta"), 12);
        index.Insert((2, "alpha"), 20);

        using var clubOne = index.Range((1, "a"), (1, "z"));
        Assert.That(clubOne.BufferResult().Unwrap().ToArray(), Is.EquivalentTo(new[] { 10, 11, 12 }));
    }
}
