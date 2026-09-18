using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Lib.Hosting;

public sealed class DatabaseOptions<TDb, TTx>
    where TDb : DbContext<TTx>
    where TTx : ITransaction {
    public Func<ColdStore, TDb>? CreateDb { get; set; }
    public Func<TDb, Task>? LoadAsync { get; set; }
    public Func<TDb, ColdStore, long?, Result>? LoadFromGenesis { get; set; }
}
