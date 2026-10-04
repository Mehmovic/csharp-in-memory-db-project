using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Lib.Hosting;

public sealed class DatabaseOptions<TDb, TTx>
    where TDb : DbContext<TTx>
    where TTx : ITransaction {
    public Func<ColdStore, TDb>? CreateDb { get; set; }
    public Func<TDb, ColdStore, ulong?, Result>? LoadFromGenesis { get; set; }

    // Absent for an Instant-only database
    public Func<TDb, Result>? RunMigration { get; set; }
    public int? GBinary { get; set; }
    public Func<int, bool>? IsGenerationInvalid { get; set; }
    public Func<TDb, Result>? MigrateWalArchive { get; set; }
    public ArchiveRetentionPolicy? ArchiveRetention { get; set; }
}
