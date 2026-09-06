using RhinoDB.Lib.Indexing;

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
    private readonly Table<int, Player> table;

    public int Count => table.Count;
    private readonly HashIndex<string> idxEmail = new HashIndex<string>();
    private readonly NonUniqueHashIndex<string> idxTeam = new NonUniqueHashIndex<string>();
    private readonly NonUniqueOrderedIndex<int> idxRating = new NonUniqueOrderedIndex<int>();

    public PlayerTable(int chunkSize) {
        var primary = new HashIndex<int>();
        table = new Table<int, Player>(chunkSize, primary, static player => player.Id);

        table.Register(new UniqueSecondaryIndex<Player, string>(idxEmail, static player => player.Email));
        table.Register(new NonUniqueSecondaryIndex<Player, string>(idxTeam, static player => player.Team));
        table.Register(new NonUniqueSecondaryIndex<Player, int>(idxRating, static player => player.Rating));
    }

    public Result<Player> Get(int id) => table.Get(id);

    public Result<Player> GetByEmail(string email) {
        var offset = idxEmail.GetOffset(email);
        return offset.IsError() ? offset.Void() : table.GetByOffset(offset.Unwrap());
    }

    public List<Player> GetByTeam(string team) {
        var offsets = idxTeam.GetOffsets(team);
        var rows = new List<Player>(offsets.Count);
        foreach (var offset in offsets) rows.Add(table.GetByOffset(offset));
        return rows;
    }

    public List<Player> GetByRating(int from, int to) {
        var offsets = idxRating.Range(from, to);
        var rows = new List<Player>(offsets.Count);
        foreach (var offset in offsets) rows.Add(table.GetByOffset(offset));
        return rows;
    }

    public Result Insert(Player player) => table.Insert(player);

    public Result Delete(int id) => table.Delete(id);

    public Result Update(int id, Player newPlayer) =>  table.Update(id, newPlayer);
}
