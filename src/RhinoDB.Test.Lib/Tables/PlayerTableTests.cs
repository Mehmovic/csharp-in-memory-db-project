using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Test.Lib.Tables;

public class PlayerTableTests {
    static private PlayerTable NewTable() => new PlayerTable();

    static private Player Alice(int id = 1, string team = "Red", int rating = 80) =>
        new Player(id, "Alice", $"alice{id}@example.com", team, rating);

    // ---- Insert ----

    [Test]
    public void Insert_ThenGet_ReturnsTheInsertedRow() {
        var table = NewTable();
        var player = Alice();

        var result = table.Insert(player);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(player));
        Assert.That(table.Count, Is.EqualTo(1));
    }

    [Test]
    public void Insert_ThenAllSecondaryAccessors_FindTheRow() {
        var table = NewTable();
        var player = Alice();
        table.Insert(player);

        Assert.That(table.Email.Get(player.Email).Unwrap(), Is.EqualTo(player));
        Assert.That(table.Team.Get(player.Team), Is.EqualTo(new[] { player }));
        Assert.That(table.Rating.Range(player.Rating, player.Rating), Is.EqualTo(new[] { player }));
    }

    [Test]
    public void Insert_DuplicatePrimaryKey_ReturnsFailureWithDuplicateKeyException() {
        var table = NewTable();
        table.Insert(Alice(id: 1));

        var result = table.Insert(Alice(id: 1, team: "Blue"));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void Insert_DuplicatePrimaryKey_DoesNotChangeCount() {
        var table = NewTable();
        table.Insert(Alice(id: 1));

        table.Insert(Alice(id: 1, team: "Blue"));

        Assert.That(table.Count, Is.EqualTo(1));
    }

    [Test]
    public void Insert_DuplicateEmail_ReturnsFailureWithDuplicateKeyException() {
        var table = NewTable();
        table.Insert(Alice(id: 1));

        var duplicate = new Player(2, "Bob", "alice1@example.com", "Blue", 70);
        var result = table.Insert(duplicate);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void Insert_DuplicateEmail_RollsBackCompletely_NewRowUnreachableEverywhere() {
        var table = NewTable();
        table.Insert(Alice(id: 1));

        var duplicate = new Player(2, "Bob", "alice1@example.com", "Blue", 70);
        table.Insert(duplicate);

        Assert.That(table.Count, Is.EqualTo(1));
        Assert.That(table.Get(2).IsError(), Is.True);
        Assert.That(table.Team.Get("Blue"), Is.Empty);
        Assert.That(table.Rating.Range(70, 70), Is.Empty);
    }

    [Test]
    public void Insert_DuplicateEmail_OriginalRowStillFullyIntact() {
        var table = NewTable();
        var original = Alice(id: 1);
        table.Insert(original);

        table.Insert(new Player(2, "Bob", original.Email, "Blue", 70));

        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(original));
        Assert.That(table.Email.Get(original.Email).Unwrap(), Is.EqualTo(original));
    }

    [Test]
    public void Insert_SameTeamDifferentPlayers_BothRetrievableViaByTeam() {
        var table = NewTable();
        var a = Alice(id: 1, team: "Red");
        var b = new Player(2, "Bob", "bob@example.com", "Red", 75);

        table.Insert(a);
        table.Insert(b);

        Assert.That(table.Team.Get("Red"), Is.EquivalentTo(new[] { a, b }));
    }

    [Test]
    public void Insert_DuplicateEmail_RollbackDeletesByOffsetNotById() {
        // Regression: Insert's rollback calls storage.Delete(player.Id) instead of
        // storage.Delete(offset). Id and offset are both plain ints so this compiles,
        // but they are different domains - here the failing insert's Id (1) is chosen
        // to collide with bob's real storage offset (1), so a buggy offset-by-id
        // delete swap-corrupts bob's row instead of removing the failed insert's row.
        var table = NewTable();
        var alice = Alice(id: 10);
        var bob = new Player(11, "Bob", "bob@example.com", "Blue", 70);
        table.Insert(alice); // offset 0
        table.Insert(bob);   // offset 1

        var duplicate = new Player(1, "Dup", alice.Email, "Green", 99); // Id collides with bob's offset
        table.Insert(duplicate);

        Assert.That(table.Get(11).Unwrap(), Is.EqualTo(bob));
    }

    [Test]
    public void Insert_AcrossChunkBoundary_AllIndexesStayConsistent() {
        // chunkSize is 4, so 6 inserts force a second chunk allocation partway through.
        var table = NewTable();
        var players = Enumerable.Range(1, 6)
            .Select(i => new Player(i, $"Player{i}", $"p{i}@example.com", "Red", 50 + i))
            .ToArray();

        foreach (var p in players)
            table.Insert(p);

        foreach (var p in players) {
            Assert.That(table.Get(p.Id).Unwrap(), Is.EqualTo(p));
            Assert.That(table.Email.Get(p.Email).Unwrap(), Is.EqualTo(p));
            Assert.That(table.Rating.Range(p.Rating, p.Rating), Is.EqualTo(new[] { p }));
        }
        Assert.That(table.Team.Get("Red"), Is.EquivalentTo(players));
    }

    // ---- Delete ----

    [Test]
    public void Delete_UnknownId_ReturnsFailureWithIndexKeyNotFoundException() {
        var table = NewTable();

        var result = table.Delete(999);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Delete_UnknownId_DoesNotChangeCount() {
        var table = NewTable();
        table.Insert(Alice(id: 1));

        table.Delete(999);

        Assert.That(table.Count, Is.EqualTo(1));
    }

    [Test]
    public void Delete_OfThePhysicallyLastRow_RemovesFromAllIndexes() {
        var table = NewTable();
        table.Insert(Alice(id: 1, team: "Red"));
        var last = new Player(2, "Bob", "bob@example.com", "Blue", 70);
        table.Insert(last);

        var result = table.Delete(2); // Bob was already physically last; no swap

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Count, Is.EqualTo(1));
        Assert.That(table.Get(2).IsError(), Is.True);
        Assert.That(table.Email.Get(last.Email).IsError(), Is.True);
        Assert.That(table.Team.Get("Blue"), Is.Empty);
        Assert.That(table.Rating.Range(70, 70), Is.Empty);
    }

    [Test]
    public void Delete_OfNonLastRow_TheSwappedRowStaysReachableOnEveryIndex() {
        // This is the central regression: DenseArray swap-removes on delete, so the
        // physically-last row moves into the deleted slot. Every index sharing that
        // array - not just the primary - must repoint to the row's new offset.
        var table = NewTable();
        var alice = Alice(id: 1, team: "Red", rating: 80);
        var bob = new Player(2, "Bob", "bob@example.com", "Blue", 70);
        var carol = new Player(3, "Carol", "carol@example.com", "Green", 90); // physically last
        table.Insert(alice);
        table.Insert(bob);
        table.Insert(carol);

        var result = table.Delete(1); // Carol swaps into Alice's old slot

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Count, Is.EqualTo(2));

        // Carol must still be reachable, correctly, through every index.
        Assert.That(table.Get(3).Unwrap(), Is.EqualTo(carol));
        Assert.That(table.Email.Get(carol.Email).Unwrap(), Is.EqualTo(carol));
        Assert.That(table.Team.Get("Green"), Is.EqualTo(new[] { carol }));
        Assert.That(table.Rating.Range(90, 90), Is.EqualTo(new[] { carol }));

        // Untouched row unaffected.
        Assert.That(table.Get(2).Unwrap(), Is.EqualTo(bob));

        // Deleted row fully gone.
        Assert.That(table.Get(1).IsError(), Is.True);
        Assert.That(table.Email.Get(alice.Email).IsError(), Is.True);
        Assert.That(table.Team.Get("Red"), Is.Empty);
        Assert.That(table.Rating.Range(80, 80), Is.Empty);
    }

    [Test]
    public void Delete_OfNonLastRow_SiblingsInTheSameNonUniqueBucketsAreUnaffected() {
        // Deleted row and the swapped-in row share a Team bucket with an unrelated
        // survivor - deregistering/repointing by (key, offset) must not disturb it.
        var table = NewTable();
        var alice = new Player(1, "Alice", "alice@example.com", "Red", 80);
        var bob = new Player(2, "Bob", "bob@example.com", "Red", 70); // shares Team with alice
        var carol = new Player(3, "Carol", "carol@example.com", "Red", 90); // physically last, shares Team too
        table.Insert(alice);
        table.Insert(bob);
        table.Insert(carol);

        table.Delete(1); // Carol swaps into Alice's slot; Bob must be untouched

        Assert.That(table.Team.Get("Red"), Is.EquivalentTo(new[] { bob, carol }));
    }

    [Test]
    public void Delete_EveryRow_LeavesTableEmptyAcrossAllIndexes() {
        var table = NewTable();
        var players = new[] {
            Alice(id: 1, team: "Red"),
            new Player(2, "Bob", "bob@example.com", "Blue", 70),
            new Player(3, "Carol", "carol@example.com", "Green", 90),
        };
        foreach (var p in players) table.Insert(p);

        foreach (var p in players) table.Delete(p.Id);

        Assert.That(table.Count, Is.EqualTo(0));
        foreach (var p in players) {
            Assert.That(table.Get(p.Id).IsError(), Is.True);
            Assert.That(table.Email.Get(p.Email).IsError(), Is.True);
            Assert.That(table.Team.Get(p.Team), Is.Empty);
            Assert.That(table.Rating.Range(p.Rating, p.Rating), Is.Empty);
        }
    }

    [Test]
    public void Delete_ThenInsertSameId_SucceedsCleanly() {
        var table = NewTable();
        var original = Alice(id: 1);
        table.Insert(original);
        table.Delete(1);

        var replacement = new Player(1, "Alicia", "alicia@example.com", "Blue", 65);
        var result = table.Insert(replacement);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(replacement));
        Assert.That(table.Email.Get(original.Email).IsError(), Is.True);
        Assert.That(table.Email.Get(replacement.Email).Unwrap(), Is.EqualTo(replacement));
    }

    [Test]
    public void Delete_AcrossChunkBoundary_SwappedRowFromSecondChunkStaysConsistent() {
        // chunkSize is 4; 6 inserts force a second chunk, so deleting an early row
        // swaps a row that physically lives in the second chunk into the first.
        var table = NewTable();
        var players = Enumerable.Range(1, 6)
            .Select(i => new Player(i, $"Player{i}", $"p{i}@example.com", "Red", 50 + i))
            .ToArray();
        foreach (var p in players) table.Insert(p);

        table.Delete(2); // physically-last (id 6) swaps into id 2's old slot

        Assert.That(table.Count, Is.EqualTo(5));
        Assert.That(table.Get(2).IsError(), Is.True);
        foreach (var p in players.Where(p => p.Id != 2)) {
            Assert.That(table.Get(p.Id).Unwrap(), Is.EqualTo(p));
            Assert.That(table.Email.Get(p.Email).Unwrap(), Is.EqualTo(p));
            Assert.That(table.Rating.Range(p.Rating, p.Rating), Is.EqualTo(new[] { p }));
        }
    }

    // ---- Update ----

    [Test]
    public void Update_UnknownId_ReturnsFailureWithIndexKeyNotFoundException() {
        var table = NewTable();

        var result = table.Update(999, Alice(id: 999));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Update_AttemptingToChangePrimaryKey_ReturnsFailureWithPrimaryKeyImmutableException() {
        var table = NewTable();
        var original = Alice(id: 1);
        table.Insert(original);

        var result = table.Update(1, original with { Id = 2 });

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<PrimaryKeyImmutableException>());
    }

    [Test]
    public void Update_AttemptingToChangePrimaryKey_LeavesOriginalRowUntouched() {
        var table = NewTable();
        var original = Alice(id: 1);
        table.Insert(original);

        table.Update(1, original with { Id = 2 });

        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(original));
        Assert.That(table.Get(2).IsError(), Is.True);
    }

    [Test]
    public void Update_NonIndexedFieldOnly_ChangesTheFieldAndLeavesIndexesIntact() {
        var table = NewTable();
        var original = Alice(id: 1);
        table.Insert(original);

        var updated = original with { Name = "Alicia" };
        var result = table.Update(1, updated);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(updated));
        Assert.That(table.Email.Get(original.Email).Unwrap(), Is.EqualTo(updated));
        Assert.That(table.Team.Get(original.Team), Is.EqualTo(new[] { updated }));
        Assert.That(table.Rating.Range(original.Rating, original.Rating), Is.EqualTo(new[] { updated }));
    }

    [Test]
    public void Update_Email_OldEmailNoLongerResolves_NewEmailDoes() {
        var table = NewTable();
        var original = Alice(id: 1);
        table.Insert(original);

        var updated = original with { Email = "newalice@example.com" };
        table.Update(1, updated);

        Assert.That(table.Email.Get(original.Email).IsError(), Is.True);
        Assert.That(table.Email.Get(updated.Email).Unwrap(), Is.EqualTo(updated));
        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(updated));
    }

    [Test]
    public void Update_EmailToOneAlreadyUsedByAnotherRow_ReturnsFailureWithDuplicateKeyException() {
        var table = NewTable();
        var alice = Alice(id: 1);
        var bob = new Player(2, "Bob", "bob@example.com", "Blue", 70);
        table.Insert(alice);
        table.Insert(bob);

        var result = table.Update(2, bob with { Email = alice.Email });

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void Update_EmailConflict_LeavesTheUpdatedRowFullyUnchanged() {
        var table = NewTable();
        var alice = Alice(id: 1);
        var bob = new Player(2, "Bob", "bob@example.com", "Blue", 70);
        table.Insert(alice);
        table.Insert(bob);

        table.Update(2, bob with { Email = alice.Email });

        Assert.That(table.Get(2).Unwrap(), Is.EqualTo(bob));
        Assert.That(table.Email.Get(bob.Email).Unwrap(), Is.EqualTo(bob));
        Assert.That(table.Team.Get(bob.Team), Is.EqualTo(new[] { bob }));
        Assert.That(table.Rating.Range(bob.Rating, bob.Rating), Is.EqualTo(new[] { bob }));
    }

    [Test]
    public void Update_EmailConflict_LeavesTheOtherRowFullyUnchanged() {
        var table = NewTable();
        var alice = Alice(id: 1);
        var bob = new Player(2, "Bob", "bob@example.com", "Blue", 70);
        table.Insert(alice);
        table.Insert(bob);

        table.Update(2, bob with { Email = alice.Email });

        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(alice));
        Assert.That(table.Email.Get(alice.Email).Unwrap(), Is.EqualTo(alice));
    }

    [Test]
    public void Update_Team_MovesTheRowToTheNewTeamBucket() {
        var table = NewTable();
        var original = Alice(id: 1, team: "Red");
        table.Insert(original);

        var updated = original with { Team = "Blue" };
        table.Update(1, updated);

        Assert.That(table.Team.Get("Red"), Is.Empty);
        Assert.That(table.Team.Get("Blue"), Is.EqualTo(new[] { updated }));
    }

    [Test]
    public void Update_Team_OtherRowsSharingTheOldTeamRemain() {
        var table = NewTable();
        var alice = Alice(id: 1, team: "Red");
        var bob = new Player(2, "Bob", "bob@example.com", "Red", 70);
        table.Insert(alice);
        table.Insert(bob);

        table.Update(1, alice with { Team = "Blue" });

        Assert.That(table.Team.Get("Red"), Is.EqualTo(new[] { bob }));
    }

    [Test]
    public void Update_Rating_RangeQueriesReflectTheNewValueNotTheOld() {
        var table = NewTable();
        var original = Alice(id: 1, rating: 80);
        table.Insert(original);

        var updated = original with { Rating = 95 };
        table.Update(1, updated);

        Assert.That(table.Rating.Range(80, 80), Is.Empty);
        Assert.That(table.Rating.Range(95, 95), Is.EqualTo(new[] { updated }));
    }

    [Test]
    public void Update_WithAllFieldsIdentical_IsANoOpThatSucceeds() {
        var table = NewTable();
        var original = Alice(id: 1);
        table.Insert(original);

        var result = table.Update(1, original);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(original));
        Assert.That(table.Email.Get(original.Email).Unwrap(), Is.EqualTo(original));
        Assert.That(table.Team.Get(original.Team), Is.EqualTo(new[] { original }));
        Assert.That(table.Rating.Range(original.Rating, original.Rating), Is.EqualTo(new[] { original }));
    }

    [Test]
    public void Update_AllSecondaryKeysAtOnce_EveryIndexReflectsTheChange() {
        var table = NewTable();
        var original = Alice(id: 1, team: "Red", rating: 80);
        table.Insert(original);

        var updated = new Player(1, "Alicia", "newalice@example.com", "Blue", 95);
        var result = table.Update(1, updated);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(table.Get(1).Unwrap(), Is.EqualTo(updated));
        Assert.That(table.Email.Get(original.Email).IsError(), Is.True);
        Assert.That(table.Email.Get(updated.Email).Unwrap(), Is.EqualTo(updated));
        Assert.That(table.Team.Get("Red"), Is.Empty);
        Assert.That(table.Team.Get("Blue"), Is.EqualTo(new[] { updated }));
        Assert.That(table.Rating.Range(80, 80), Is.Empty);
        Assert.That(table.Rating.Range(95, 95), Is.EqualTo(new[] { updated }));
    }
}
