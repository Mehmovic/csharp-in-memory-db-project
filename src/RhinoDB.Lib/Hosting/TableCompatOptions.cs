namespace RhinoDB.Lib.Hosting;

public sealed class TableCompatOptions<TRow> {
    public Func<int, byte[], TRow>? MigrateClientBytesToCurrentRevision { get; set; }
    public Func<int, bool>? CanDowngradeTo { get; set; }
    public Func<int, TRow, byte[]>? DowngradeToRevisionBytes { get; set; }
    public Func<int, bool>? IsRevisionInvalid { get; set; }
}
