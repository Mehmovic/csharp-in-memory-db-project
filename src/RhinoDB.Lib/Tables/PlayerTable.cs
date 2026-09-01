using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Storage;

namespace RhinoDB.Lib.Tables;

public readonly record struct Player(int Id, string Name, string Email, string Team, int Rating);

// Hand-written proof of the Table<TRow> shape before it's generalized/codegen'd.
// Id is the primary key (immutable - Update can never change it, see PrimaryKeyImmutableException).
// Email is a unique secondary index, Team a non-unique hash secondary, Rating a non-unique ordered secondary,
// exercising all four index kinds and both the unique/non-unique failure paths through one table.
public class PlayerTable {
    private readonly DenseArray<Player> storage = new DenseArray<Player>(chunkSize: 4);
    private readonly HashIndex<int, Player> primary;

    public readonly HashIndex<string, Player> Email;
    public readonly NonUniqueHashIndex<string, Player> Team;
    public readonly NonUniqueOrderedIndex<int, Player> Rating;

    public PlayerTable() {
        primary = new HashIndex<int, Player>(storage, p => p.Id);
        Email = new HashIndex<string, Player>(storage, p => p.Email);
        Team = new NonUniqueHashIndex<string, Player>(storage);
        Rating = new NonUniqueOrderedIndex<int, Player>(storage);
    }

    public int Count => storage.Count;

    public Result<Player> Get(int id) => primary.Get(id);

    public Result Insert(Player player) {
        var duplicateIndexResult = primary.Get(player.Id);
        if (duplicateIndexResult.IsOk()) { return Result.Error(new DuplicateKeyException(player.Id)); }

        var duplicateEmailResult = Email.Get(player.Email);
        if (duplicateEmailResult.IsOk()) { return Result.Error(new DuplicateKeyException(player.Email)); }

        var offset = storage.Insert(player);

        primary.Register(player.Id, offset);
        Email.Register(player.Email, offset);
        Team.Register(player.Team, offset);
        Rating.Register(player.Rating, offset);
        return Result.Ok();
    }

    public Result Delete(int id) {
        var recordResult = primary.Fetch(id);
        if (recordResult.IsError()) return recordResult;

        (Player record, var offset) = recordResult.Unwrap();
        var lastOffset = storage.LastOffset;

        var deleteType = storage.Delete(offset);

        primary.Deregister(record.Id, offset);
        Rating.Deregister(record.Rating, offset);
        Team.Deregister(record.Team, offset);
        Email.Deregister(record.Email, offset);

        if (deleteType != DeleteType.DeletedWithSwap) return Result.Ok();

        Player swapped = storage.Get(offset);
        primary.Deregister(swapped.Id, lastOffset);
        Rating.Deregister(swapped.Rating, lastOffset);
        Team.Deregister(swapped.Team, lastOffset);
        Email.Deregister(swapped.Email, lastOffset);
        primary.Register(swapped.Id, offset);
        Rating.Register(swapped.Rating, offset);
        Team.Register(swapped.Team, offset);
        Email.Register(swapped.Email, offset);

        return Result.Ok();
    }

    public Result Update(int id, Player newPlayer) {
        if (id != newPlayer.Id) return Result.Error(new PrimaryKeyImmutableException(id, newPlayer.Id));

        var recordResult = primary.Fetch(id);
        if (recordResult.IsError()) return recordResult;

        (Player oldRecord, var offset) = recordResult.Unwrap();

        var emailUpdate = false;
        var teamUpdate = false;
        var ratingUpdate = false;

        if (oldRecord.Email != newPlayer.Email) {
            var duplicateEmailResult = Email.Get(newPlayer.Email);
            if (duplicateEmailResult.IsOk()) {
                return Result.Error(new DuplicateKeyException(newPlayer.Email));
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
            Email.Deregister(oldRecord.Email, offset);
            Email.Register(newPlayer.Email, offset);
        }

        if (teamUpdate) {
            Team.Deregister(oldRecord.Team, offset);
            Team.Register(newPlayer.Team, offset);
        }

        if (ratingUpdate) {
            Rating.Deregister(oldRecord.Rating, offset);
            Rating.Register(newPlayer.Rating, offset);
        }

        storage.Set(offset, newPlayer);
        return Result.Ok();
    }
}
