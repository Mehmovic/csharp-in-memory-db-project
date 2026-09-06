using RhinoDB.Core;
using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Test.Lib.Tables;

public class PlayerTableTests {
    static private PlayerTable NewTable() => new PlayerTable(4);

    static private Player Alice(int id = 1, string team = "Red", int rating = 80) =>
        new Player(id, "Alice", $"alice{id}@example.com", team, rating);

    // ---- Insert ----

    [Test]
    public void Insert_ThenGet_ReturnsTheInsertedRow() {
        PlayerTable table = NewTable();
        Player player = Alice();

        Result result = table.Insert(player);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(player));
        Assert.That(table.Count, Is.EqualTo(1));
    }

    [Test]
    public void Insert_ThenAllSecondaryAccessors_FindTheRow() {
        PlayerTable table = NewTable();
        Player player = Alice();
        table.Insert(player);

        Assert.That(table.GetByEmail(player.Email).Unwrap(), Is.EqualTo(player));
        Assert.That(table.GetByTeam(player.Team), Is.EqualTo(new[] { player }));
        Assert.That(table.GetByRating(player.Rating, player.Rating), Is.EqualTo(new[] { player }));
    }

    [Test]
    public void Insert_DuplicatePrimaryKey_ReturnsFailureWithDuplicateKeyException() {
        PlayerTable table = NewTable();
        table.Insert(Alice(id: 1));

        Result result = table.Insert(Alice(id: 1, team: "Blue"));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void Insert_DuplicatePrimaryKey_DoesNotChangeCount() {
        PlayerTable table = NewTable();
        table.Insert(Alice(id: 1));

        table.Insert(Alice(id: 1, team: "Blue"));

        Assert.That(table.Count, Is.EqualTo(1));
    }

    [Test]
    public void Insert_DuplicateEmail_ReturnsFailureWithDuplicateKeyException() {
        PlayerTable table = NewTable();
        table.Insert(Alice(id: 1));

        var duplicate = new Player(2, "Bob", "alice1@example.com", "Blue", 70);
        Result result = table.Insert(duplicate);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void Insert_DuplicateEmail_RollsBackCompletely_NewRowUnreachableEverywhere() {
        PlayerTable table = NewTable();
        table.Insert(Alice(id: 1));

        var duplicate = new Player(2, "Bob", "alice1@example.com", "Blue", 70);
        table.Insert(duplicate);

        Assert.That(table.Count, Is.EqualTo(1));
        Assert.That(table.Get(2).IsError(), Is.True);
        Assert.That(table.GetByTeam("Blue"), Is.Empty);
        Assert.That(table.GetByRating(70, 70), Is.Empty);
    }

    [Test]
    public void Insert_DuplicateEmail_OriginalRowStillFullyIntact() {
        PlayerTable table = NewTable();
        Player original = Alice(id: 1);
        table.Insert(original);

        table.Insert(new Player(2, "Bob", original.Email, "Blue", 70));

        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(original));
        Assert.That(table.GetByEmail(original.Email).Unwrap(), Is.EqualTo(original));
    }

    [Test]
    public void Insert_DuplicateEmail_RollbackDeletesByOffsetNotById() {
        // Regression: Insert's rollback used to call storage.Delete(player.Id) instead
        // of storage.Delete(offset). Id and offset are both plain ints so that compiled,
        // but they are different domains - here the failing insert's Id (1) is chosen
        // to collide with bob's real storage offset (1), which used to swap-corrupt
        // bob's row instead of removing the failed insert's row. Insert now pre-checks
        // uniqueness before touching storage at all, so there is no rollback to get wrong.
        PlayerTable table = NewTable();
        Player alice = Alice(id: 10);
        var bob = new Player(11, "Bob", "bob@example.com", "Blue", 70);
        table.Insert(alice); // offset 0
        table.Insert(bob);   // offset 1

        var duplicate = new Player(1, "Dup", alice.Email, "Green", 99); // Id collides with bob's offset
        table.Insert(duplicate);

        Assert.That(table.Get(11).Unwrap(), Is.EqualTo(bob));
    }

    [Test]
    public void Insert_SameTeamDifferentPlayers_BothRetrievableViaGetByTeam() {
        PlayerTable table = NewTable();
        Player a = Alice(id: 1, team: "Red");
        var b = new Player(2, "Bob", "bob@example.com", "Red", 75);

        table.Insert(a);
        table.Insert(b);

        Assert.That(table.GetByTeam("Red"), Is.EquivalentTo(new[] { a, b }));
    }

    [Test]
    public void Insert_AcrossChunkBoundary_AllIndexesStayConsistent() {
        // chunkSize is 4, so 6 inserts force a second chunk allocation partway through.
        PlayerTable table = NewTable();
        var players = Enumerable.Range(1, 6)
            .Select(i => new Player(i, $"Player{i}", $"p{i}@example.com", "Red", 50 + i))
            .ToArray();

        foreach (Player p in players)
            table.Insert(p);

        foreach (Player p in players) {
            Assert.That(table.Get(p.Id).Unwrap(), Is.EqualTo(p));
            Assert.That(table.GetByEmail(p.Email).Unwrap(), Is.EqualTo(p));
            Assert.That(table.GetByRating(p.Rating, p.Rating), Is.EqualTo(new[] { p }));
        }
        Assert.That(table.GetByTeam("Red"), Is.EquivalentTo(players));
    }

    // ---- Delete ----

    [Test]
    public void Delete_UnknownId_ReturnsFailureWithIndexKeyNotFoundException() {
        PlayerTable table = NewTable();

        Result result = table.Delete(999);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Delete_UnknownId_DoesNotChangeCount() {
        PlayerTable table = NewTable();
        table.Insert(Alice(id: 1));

        table.Delete(999);

        Assert.That(table.Count, Is.EqualTo(1));
    }

    [Test]
    public void Delete_OfThePhysicallyLastRow_RemovesFromAllIndexes() {
        PlayerTable table = NewTable();
        table.Insert(Alice(id: 1, team: "Red"));
        var last = new Player(2, "Bob", "bob@example.com", "Blue", 70);
        table.Insert(last);

        Result result = table.Delete(2); // Bob was already physically last; no swap

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Count, Is.EqualTo(1));
        Assert.That(table.Get(2).IsError(), Is.True);
        Assert.That(table.GetByEmail(last.Email).IsError(), Is.True);
        Assert.That(table.GetByTeam("Blue"), Is.Empty);
        Assert.That(table.GetByRating(70, 70), Is.Empty);
    }

    [Test]
    public void Delete_OfNonLastRow_TheSwappedRowStaysReachableOnEveryIndex() {
        // This is the central regression: DenseArray swap-removes on delete, so the
        // physically-last row moves into the deleted slot. Every index sharing that
        // array - not just the primary - must repoint to the row's new offset.
        PlayerTable table = NewTable();
        Player alice = Alice(id: 1, team: "Red", rating: 80);
        var bob = new Player(2, "Bob", "bob@example.com", "Blue", 70);
        var carol = new Player(3, "Carol", "carol@example.com", "Green", 90); // physically last
        table.Insert(alice);
        table.Insert(bob);
        table.Insert(carol);

        Result result = table.Delete(1); // Carol swaps into Alice's old slot

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Count, Is.EqualTo(2));

        // Carol must still be reachable, correctly, through every index.
        Assert.That(table.Get(3).Unwrap(), Is.EqualTo(carol));
        Assert.That(table.GetByEmail(carol.Email).Unwrap(), Is.EqualTo(carol));
        Assert.That(table.GetByTeam("Green"), Is.EqualTo(new[] { carol }));
        Assert.That(table.GetByRating(90, 90), Is.EqualTo(new[] { carol }));

        // Untouched row unaffected.
        Assert.That(table.Get(2).Unwrap(), Is.EqualTo(bob));

        // Deleted row fully gone.
        Assert.That(table.Get(1).IsError(), Is.True);
        Assert.That(table.GetByEmail(alice.Email).IsError(), Is.True);
        Assert.That(table.GetByTeam("Red"), Is.Empty);
        Assert.That(table.GetByRating(80, 80), Is.Empty);
    }

    [Test]
    public void Delete_OfNonLastRow_SiblingsInTheSameNonUniqueBucketsAreUnaffected() {
        // Deleted row and the swapped-in row share a Team bucket with an unrelated
        // survivor - deregistering/repointing by (key, offset) must not disturb it.
        PlayerTable table = NewTable();
        var alice = new Player(1, "Alice", "alice@example.com", "Red", 80);
        var bob = new Player(2, "Bob", "bob@example.com", "Red", 70); // shares Team with alice
        var carol = new Player(3, "Carol", "carol@example.com", "Red", 90); // physically last, shares Team too
        table.Insert(alice);
        table.Insert(bob);
        table.Insert(carol);

        table.Delete(1); // Carol swaps into Alice's slot; Bob must be untouched

        Assert.That(table.GetByTeam("Red"), Is.EquivalentTo(new[] { bob, carol }));
    }

    [Test]
    public void Delete_OfNonLastRow_SiblingsInTheSameRatingBucketAreUnaffected() {
        // Mirrors the Team version above but exercises idxRating's swap-repoint path
        // instead - a SortedSet<(TKey,int)>-backed NonUniqueOrderedIndex, not the
        // Dictionary<TKey,List<int>>-backed NonUniqueHashIndex. Delete no longer
        // validates (key, offset) pairs on its own, so this is the test that would
        // actually catch a swap-repoint bug (e.g. deleting/inserting the swapped row
        // at the wrong offset) - not an internal check whose result nobody reads.
        PlayerTable table = NewTable();
        var alice = new Player(1, "Alice", "alice@example.com", "Red", 80);
        var bob = new Player(2, "Bob", "bob@example.com", "Blue", 80); // shares Rating with alice
        var carol = new Player(3, "Carol", "carol@example.com", "Green", 80); // physically last, shares Rating too
        table.Insert(alice);
        table.Insert(bob);
        table.Insert(carol);

        table.Delete(1); // Carol swaps into Alice's slot; Bob must be untouched

        Assert.That(table.GetByRating(80, 80), Is.EquivalentTo(new[] { bob, carol }));
    }

    [Test]
    public void Delete_EveryRow_LeavesTableEmptyAcrossAllIndexes() {
        PlayerTable table = NewTable();
        var players = new[] {
            Alice(id: 1, team: "Red"),
            new Player(2, "Bob", "bob@example.com", "Blue", 70),
            new Player(3, "Carol", "carol@example.com", "Green", 90),
        };
        foreach (Player p in players) table.Insert(p);

        foreach (Player p in players) table.Delete(p.Id);

        Assert.That(table.Count, Is.EqualTo(0));
        foreach (Player p in players) {
            Assert.That(table.Get(p.Id).IsError(), Is.True);
            Assert.That(table.GetByEmail(p.Email).IsError(), Is.True);
            Assert.That(table.GetByTeam(p.Team), Is.Empty);
            Assert.That(table.GetByRating(p.Rating, p.Rating), Is.Empty);
        }
    }

    [Test]
    public void Delete_ThenInsertSameId_SucceedsCleanly() {
        PlayerTable table = NewTable();
        Player original = Alice(id: 1);
        table.Insert(original);
        table.Delete(1);

        var replacement = new Player(1, "Alicia", "alicia@example.com", "Blue", 65);
        Result result = table.Insert(replacement);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(replacement));
        Assert.That(table.GetByEmail(original.Email).IsError(), Is.True);
        Assert.That(table.GetByEmail(replacement.Email).Unwrap(), Is.EqualTo(replacement));
    }

    [Test]
    public void Delete_AcrossChunkBoundary_SwappedRowFromSecondChunkStaysConsistent() {
        // chunkSize is 4; 6 inserts force a second chunk, so deleting an early row
        // swaps a row that physically lives in the second chunk into the first.
        PlayerTable table = NewTable();
        var players = Enumerable.Range(1, 6)
            .Select(i => new Player(i, $"Player{i}", $"p{i}@example.com", "Red", 50 + i))
            .ToArray();
        foreach (Player p in players) table.Insert(p);

        table.Delete(2); // physically-last (id 6) swaps into id 2's old slot

        Assert.That(table.Count, Is.EqualTo(5));
        Assert.That(table.Get(2).IsError(), Is.True);
        foreach (Player p in players.Where(p => p.Id != 2)) {
            Assert.That(table.Get(p.Id).Unwrap(), Is.EqualTo(p));
            Assert.That(table.GetByEmail(p.Email).Unwrap(), Is.EqualTo(p));
            Assert.That(table.GetByRating(p.Rating, p.Rating), Is.EqualTo(new[] { p }));
        }
    }

    // ---- Update ----

    [Test]
    public void Update_UnknownId_ReturnsFailureWithIndexKeyNotFoundException() {
        PlayerTable table = NewTable();

        Result result = table.Update(999, Alice(id: 999));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Update_AttemptingToChangePrimaryKey_ReturnsFailureWithPrimaryKeyImmutableException() {
        PlayerTable table = NewTable();
        Player original = Alice(id: 1);
        table.Insert(original);

        Result result = table.Update(1, original with { Id = 2 });

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<PrimaryKeyImmutableException>());
    }

    [Test]
    public void Update_AttemptingToChangePrimaryKey_LeavesOriginalRowUntouched() {
        PlayerTable table = NewTable();
        Player original = Alice(id: 1);
        table.Insert(original);

        table.Update(1, original with { Id = 2 });

        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(original));
        Assert.That(table.Get(2).IsError(), Is.True);
    }

    [Test]
    public void Update_NonIndexedFieldOnly_ChangesTheFieldAndLeavesIndexesIntact() {
        PlayerTable table = NewTable();
        Player original = Alice(id: 1);
        table.Insert(original);

        Player updated = original with { Name = "Alicia" };
        Result result = table.Update(1, updated);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(updated));
        Assert.That(table.GetByEmail(original.Email).Unwrap(), Is.EqualTo(updated));
        Assert.That(table.GetByTeam(original.Team), Is.EqualTo(new[] { updated }));
        Assert.That(table.GetByRating(original.Rating, original.Rating), Is.EqualTo(new[] { updated }));
    }

    [Test]
    public void Update_Email_OldEmailNoLongerResolves_NewEmailDoes() {
        PlayerTable table = NewTable();
        Player original = Alice(id: 1);
        table.Insert(original);

        Player updated = original with { Email = "newalice@example.com" };
        table.Update(1, updated);

        Assert.That(table.GetByEmail(original.Email).IsError(), Is.True);
        Assert.That(table.GetByEmail(updated.Email).Unwrap(), Is.EqualTo(updated));
        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(updated));
    }

    [Test]
    public void Update_EmailToOneAlreadyUsedByAnotherRow_ReturnsFailureWithDuplicateKeyException() {
        PlayerTable table = NewTable();
        Player alice = Alice(id: 1);
        var bob = new Player(2, "Bob", "bob@example.com", "Blue", 70);
        table.Insert(alice);
        table.Insert(bob);

        Result result = table.Update(2, bob with { Email = alice.Email });

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void Update_EmailConflict_LeavesTheUpdatedRowFullyUnchanged() {
        PlayerTable table = NewTable();
        Player alice = Alice(id: 1);
        var bob = new Player(2, "Bob", "bob@example.com", "Blue", 70);
        table.Insert(alice);
        table.Insert(bob);

        table.Update(2, bob with { Email = alice.Email });

        Assert.That(table.Get(2).Unwrap(), Is.EqualTo(bob));
        Assert.That(table.GetByEmail(bob.Email).Unwrap(), Is.EqualTo(bob));
        Assert.That(table.GetByTeam(bob.Team), Is.EqualTo(new[] { bob }));
        Assert.That(table.GetByRating(bob.Rating, bob.Rating), Is.EqualTo(new[] { bob }));
    }

    [Test]
    public void Update_EmailConflict_LeavesTheOtherRowFullyUnchanged() {
        PlayerTable table = NewTable();
        Player alice = Alice(id: 1);
        var bob = new Player(2, "Bob", "bob@example.com", "Blue", 70);
        table.Insert(alice);
        table.Insert(bob);

        table.Update(2, bob with { Email = alice.Email });

        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(alice));
        Assert.That(table.GetByEmail(alice.Email).Unwrap(), Is.EqualTo(alice));
    }

    [Test]
    public void Update_Team_MovesTheRowToTheNewTeamBucket() {
        PlayerTable table = NewTable();
        Player original = Alice(id: 1, team: "Red");
        table.Insert(original);

        Player updated = original with { Team = "Blue" };
        table.Update(1, updated);

        Assert.That(table.GetByTeam("Red"), Is.Empty);
        Assert.That(table.GetByTeam("Blue"), Is.EqualTo(new[] { updated }));
    }

    [Test]
    public void Update_Team_OtherRowsSharingTheOldTeamRemain() {
        PlayerTable table = NewTable();
        Player alice = Alice(id: 1, team: "Red");
        var bob = new Player(2, "Bob", "bob@example.com", "Red", 70);
        table.Insert(alice);
        table.Insert(bob);

        table.Update(1, alice with { Team = "Blue" });

        Assert.That(table.GetByTeam("Red"), Is.EqualTo(new[] { bob }));
    }

    [Test]
    public void Update_Rating_RangeQueriesReflectTheNewValueNotTheOld() {
        PlayerTable table = NewTable();
        Player original = Alice(id: 1, rating: 80);
        table.Insert(original);

        Player updated = original with { Rating = 95 };
        table.Update(1, updated);

        Assert.That(table.GetByRating(80, 80), Is.Empty);
        Assert.That(table.GetByRating(95, 95), Is.EqualTo(new[] { updated }));
    }

    [Test]
    public void Update_WithAllFieldsIdentical_IsANoOpThatSucceeds() {
        PlayerTable table = NewTable();
        Player original = Alice(id: 1);
        table.Insert(original);

        Result result = table.Update(1, original);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(original));
        Assert.That(table.GetByEmail(original.Email).Unwrap(), Is.EqualTo(original));
        Assert.That(table.GetByTeam(original.Team), Is.EqualTo(new[] { original }));
        Assert.That(table.GetByRating(original.Rating, original.Rating), Is.EqualTo(new[] { original }));
    }

    [Test]
    public void Update_AllSecondaryKeysAtOnce_EveryIndexReflectsTheChange() {
        PlayerTable table = NewTable();
        Player original = Alice(id: 1, team: "Red", rating: 80);
        table.Insert(original);

        var updated = new Player(1, "Alicia", "newalice@example.com", "Blue", 95);
        Result result = table.Update(1, updated);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(updated));
        Assert.That(table.GetByEmail(original.Email).IsError(), Is.True);
        Assert.That(table.GetByEmail(updated.Email).Unwrap(), Is.EqualTo(updated));
        Assert.That(table.GetByTeam("Red"), Is.Empty);
        Assert.That(table.GetByTeam("Blue"), Is.EqualTo(new[] { updated }));
        Assert.That(table.GetByRating(80, 80), Is.Empty);
        Assert.That(table.GetByRating(95, 95), Is.EqualTo(new[] { updated }));
    }

    // ---- Mixed operations ----

    [Test]
    public void FullLifecycle_InsertDeleteInsertUpdateDelete_TableEndsConsistentAndEmpty() {
        PlayerTable table = NewTable();
        var p1 = new Player(1, "Alice", "alice@x.com", "Red", 80);
        var p2 = new Player(2, "Bob", "bob@x.com", "Blue", 70);
        var p3 = new Player(3, "Carol", "carol@x.com", "Red", 90); // physically last after the first three inserts
        var p4 = new Player(4, "Dave", "dave@x.com", "Green", 60);
        var p5 = new Player(5, "Eve", "eve@x.com", "Red", 85);

        table.Insert(p1);
        table.Insert(p2);
        table.Insert(p3);
        table.Delete(1); // Carol swaps into Alice's old slot
        table.Insert(p4);
        table.Insert(p5);

        Player updatedBob = p2 with { Team = "Purple", Rating = 99 };
        table.Update(2, updatedBob);

        table.Delete(3); // Carol - physically not last anymore (Eve is); triggers another swap
        table.Delete(4); // Dave - physically last at this point; no swap

        Assert.That(table.Count, Is.EqualTo(2));
        Assert.That(table.Get(2).Unwrap(), Is.EqualTo(updatedBob));
        Assert.That(table.Get(5).Unwrap(), Is.EqualTo(p5));
        Assert.That(table.GetByTeam("Red"), Is.EqualTo(new[] { p5 }));
        Assert.That(table.GetByTeam("Purple"), Is.EqualTo(new[] { updatedBob }));
        Assert.That(table.GetByTeam("Green"), Is.Empty);
        Assert.That(table.GetByEmail(p3.Email).IsError(), Is.True);
        Assert.That(table.GetByEmail(p4.Email).IsError(), Is.True);

        table.Delete(2);
        table.Delete(5);

        Assert.That(table.Count, Is.EqualTo(0));
        Assert.That(table.GetByTeam("Red"), Is.Empty);
        Assert.That(table.GetByTeam("Purple"), Is.Empty);
    }

    [Test]
    public void ChainedDeletes_MultipleConsecutiveSwaps_AllSurvivorsRemainConsistent() {
        PlayerTable table = NewTable();
        var players = Enumerable.Range(1, 5)
            .Select(i => new Player(i, $"P{i}", $"p{i}@x.com", i % 2 == 0 ? "Even" : "Odd", 50 + i))
            .ToArray();
        foreach (Player p in players) table.Insert(p);

        // Deleting from the front repeatedly forces a fresh swap on every call.
        table.Delete(1);
        table.Delete(2);
        table.Delete(3);

        var survivors = players.Where(p => p.Id is 4 or 5).ToArray();
        Assert.That(table.Count, Is.EqualTo(2));
        foreach (Player p in survivors) {
            Assert.That(table.Get(p.Id).Unwrap(), Is.EqualTo(p));
            Assert.That(table.GetByEmail(p.Email).Unwrap(), Is.EqualTo(p));
            Assert.That(table.GetByRating(p.Rating, p.Rating), Is.EqualTo(new[] { p }));
        }
        Assert.That(table.GetByTeam("Even"), Is.EquivalentTo(new[] { players[3] }));
        Assert.That(table.GetByTeam("Odd"), Is.EquivalentTo(new[] { players[4] }));
    }

    [Test]
    public void FailedInsertInTheMiddleOfASequence_DoesNotDisturbSurroundingOperations() {
        PlayerTable table = NewTable();
        table.Insert(new Player(1, "Alice", "alice@x.com", "Red", 80));
        table.Insert(new Player(2, "Bob", "bob@x.com", "Blue", 70));

        Result dup = table.Insert(new Player(3, "Eve", "alice@x.com", "Green", 99)); // duplicate email
        Assert.That(dup.IsError(), Is.True);

        var carol = new Player(4, "Carol", "carol@x.com", "Green", 90);
        table.Insert(carol);
        table.Delete(1); // Carol (physically last) swaps into Alice's old slot

        Assert.That(table.Count, Is.EqualTo(2));
        Assert.That(table.Get(3).IsError(), Is.True);
        Assert.That(table.Get(2).Unwrap().Email, Is.EqualTo("bob@x.com"));
        Assert.That(table.Get(4).Unwrap(), Is.EqualTo(carol));
        Assert.That(table.GetByTeam("Green"), Is.EqualTo(new[] { carol }));
    }

    [Test]
    public void UpdateThenSwapDelete_UpdatedFieldsSurviveTheSwap() {
        PlayerTable table = NewTable();
        var alice = new Player(1, "Alice", "alice@x.com", "Red", 80);
        var bob = new Player(2, "Bob", "bob@x.com", "Blue", 70);
        var carol = new Player(3, "Carol", "carol@x.com", "Green", 90); // physically last

        table.Insert(alice);
        table.Insert(bob);
        table.Insert(carol);

        Player updatedCarol = carol with { Team = "Purple", Rating = 55 };
        table.Update(3, updatedCarol); // no swap yet - Carol is already physically last

        table.Delete(1); // Carol (now Purple/55) swaps into Alice's old slot

        Assert.That(table.Get(3).Unwrap(), Is.EqualTo(updatedCarol));
        Assert.That(table.GetByTeam("Purple"), Is.EqualTo(new[] { updatedCarol }));
        Assert.That(table.GetByTeam("Green"), Is.Empty);
        Assert.That(table.GetByRating(55, 55), Is.EqualTo(new[] { updatedCarol }));
        Assert.That(table.GetByRating(90, 90), Is.Empty);
    }

    [Test]
    public void RandomizedMixedSequence_StaysConsistentWithReferenceModelAtEveryStep() {
        // Model-based test: a plain Dictionary tracks what the table *should* contain,
        // and every Insert/Delete/Update the table accepts is mirrored into it. After
        // every step, every accessor is cross-checked against the model. This exercises
        // far more insert/delete/update interleavings - and far more swap-repoint
        // combinations - than any hand-written scenario would, while staying fully
        // reproducible via the fixed seed if it ever fails.
        PlayerTable table = NewTable();
        var oracle = new Dictionary<int, Player>();
        var rng = new Random(20260902);
        var nextId = 1;
        var teams = new[] { "Red", "Blue", "Green", "Purple", "Yellow" };

        for (var step = 0; step < 300; step++) {
            switch (rng.Next(3)) {
                case 0: {
                    var id = nextId++;
                    var player = new Player(id, $"P{id}", $"p{id}@x.com", teams[rng.Next(teams.Length)], rng.Next(40, 100));
                    if (table.Insert(player).IsOk()) oracle[id] = player;
                    break;
                }
                case 1: {
                    if (oracle.Count == 0) break;
                    var id = oracle.Keys.ElementAt(rng.Next(oracle.Count));
                    if (table.Delete(id).IsOk()) oracle.Remove(id);
                    break;
                }
                case 2: {
                    if (oracle.Count == 0) break;
                    var id = oracle.Keys.ElementAt(rng.Next(oracle.Count));
                    Player updated = oracle[id] with {
                        Team = teams[rng.Next(teams.Length)],
                        Rating = rng.Next(40, 100)
                    };
                    if (table.Update(id, updated).IsOk()) oracle[id] = updated;
                    break;
                }
            }

            Assert.That(table.Count, Is.EqualTo(oracle.Count), $"Count mismatch after step {step}");
            foreach ((var id, Player expected) in oracle) {
                Assert.That(table.Get(id).Unwrap(), Is.EqualTo(expected), $"Get({id}) wrong after step {step}");
                Assert.That(table.GetByEmail(expected.Email).Unwrap(), Is.EqualTo(expected), $"GetByEmail({expected.Email}) wrong after step {step}");
            }
            foreach (var team in teams) {
                var expected = oracle.Values.Where(p => p.Team == team);
                Assert.That(table.GetByTeam(team), Is.EquivalentTo(expected), $"GetByTeam({team}) wrong after step {step}");
            }
            Assert.That(table.GetByRating(0, 200), Is.EquivalentTo(oracle.Values), $"GetByRating(full range) wrong after step {step}");
        }
    }
}
