using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Storage;

namespace RhinoDB.Lib.Tables;

public readonly record struct Player(int Id, string Name, string Email, string Team, int Rating);

// Hand-written proof of the Table<TRow> shape before it's generalized/codegen'd.
// Id is the primary key (immutable - Update can never change it, see PrimaryKeyImmutableException).
// Email is a unique secondary index, Team a non-unique hash secondary, Rating a non-unique ordered secondary,
// exercising all four index kinds and both the unique/non-unique failure paths through one table.
//
// Indexes are pure key->offset maps with no storage access of their own - PlayerTable is the
// only thing that owns storage, so it's the only thing that can turn an offset back into a row.
public class PlayerTable {
    private readonly DenseArray<Player> storage = new DenseArray<Player>(chunkSize: 4);
    private readonly HashIndex<int> primary = new HashIndex<int>();

    private readonly HashIndex<string> idxEmail = new HashIndex<string>();
    private readonly NonUniqueHashIndex<string> idxTeam = new NonUniqueHashIndex<string>();
    private readonly NonUniqueOrderedIndex<int> idxRating = new NonUniqueOrderedIndex<int>();

    public int Count => storage.Count;

    public Result<Player> Get(int id) {
        var located = Locate(id);
        if (located.IsError()) return located.Void();
        return located.Unwrap().Row;
    }

    public Result<Player> GetByEmail(string email) {
        var offsetResult = idxEmail.GetOffset(email);
        if (offsetResult.IsError()) return offsetResult.Void();
        return storage.Get(offsetResult.Unwrap());
    }

    public List<Player> GetByTeam(string team) {
        var offsets = idxTeam.GetOffsets(team);
        var rows = new List<Player>(offsets.Count);
        foreach (var offset in offsets) rows.Add(storage.Get(offset));
        return rows;
    }

    public List<Player> GetByRating(int from, int to) {
        var offsets = idxRating.Range(from, to);
        var rows = new List<Player>(offsets.Count);
        foreach (var offset in offsets) rows.Add(storage.Get(offset));
        return rows;
    }

    public Result Insert(Player player) {
        var duplicateIndexResult = primary.GetOffset(player.Id);
        if (duplicateIndexResult.IsOk()) { return Result.Error(RhinoError.DuplicateKey(player.Id)); }

        var duplicateEmailResult = idxEmail.GetOffset(player.Email);
        if (duplicateEmailResult.IsOk()) { return Result.Error(RhinoError.DuplicateKey(player.Email)); }

        var offset = storage.Insert(player);

        primary.Insert(player.Id, offset);
        idxEmail.Insert(player.Email, offset);
        idxTeam.Insert(player.Team, offset);
        idxRating.Insert(player.Rating, offset);
        return Result.Ok();
    }

    public Result Delete(int id) {
        var located = Locate(id);
        if (located.IsError()) return located;

        (Player record, var offset) = located.Unwrap();
        var lastOffset = storage.LastOffset;

        var deleteType = storage.Delete(offset);

        primary.Delete(record.Id, offset);
        idxRating.Delete(record.Rating, offset);
        idxTeam.Delete(record.Team, offset);
        idxEmail.Delete(record.Email, offset);

        if (deleteType != DeleteType.DeletedWithSwap) return Result.Ok();

        Player swapped = storage.Get(offset);
        primary.Delete(swapped.Id, lastOffset);
        idxRating.Delete(swapped.Rating, lastOffset);
        idxTeam.Delete(swapped.Team, lastOffset);
        idxEmail.Delete(swapped.Email, lastOffset);
        primary.Insert(swapped.Id, offset);
        idxRating.Insert(swapped.Rating, offset);
        idxTeam.Insert(swapped.Team, offset);
        idxEmail.Insert(swapped.Email, offset);

        return Result.Ok();
    }

    public Result Update(int id, Player newPlayer) {
        if (id != newPlayer.Id) return Result.Error(RhinoError.PrimaryKeyImmutable(id, newPlayer.Id));

        var located = Locate(id);
        if (located.IsError()) return located;

        (Player oldRecord, var offset) = located.Unwrap();

        var emailUpdate = false;
        var teamUpdate = false;
        var ratingUpdate = false;

        if (oldRecord.Email != newPlayer.Email) {
            var duplicateEmailResult = idxEmail.GetOffset(newPlayer.Email);
            if (duplicateEmailResult.IsOk()) {
                return Result.Error(RhinoError.DuplicateKey(newPlayer.Email));
            }

            emailUpdate = true;
        }

        if (oldRecord.Team != newPlayer.Team) {
            teamUpdate = true;
        }

        if (oldRecord.Rating != newPlayer.Rating) {
            ratingUpdate = true;
        }

        if (emailUpdate) {
            idxEmail.Delete(oldRecord.Email, offset);
            idxEmail.Insert(newPlayer.Email, offset);
        }

        if (teamUpdate) {
            idxTeam.Delete(oldRecord.Team, offset);
            idxTeam.Insert(newPlayer.Team, offset);
        }

        if (ratingUpdate) {
            idxRating.Delete(oldRecord.Rating, offset);
            idxRating.Insert(newPlayer.Rating, offset);
        }

        storage.Set(offset, newPlayer);
        return Result.Ok();
    }
    
    private Result<(Player Row, int Offset)> Locate(int id) {
        var offsetResult = primary.GetOffset(id);
        if (offsetResult.IsError()) return offsetResult.Void();

        var offset = offsetResult.Unwrap();
        return (storage.Get(offset), offset);
    }
}
