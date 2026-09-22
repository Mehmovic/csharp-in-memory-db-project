namespace RhinoDB.SchemaContracts;

// One descriptor per compilation, not per database (point A/D) - a row type shared across several
// [Database]s can only ever be declared, with all its [Table] attributes, in one compilation (C#
// attributes are only ever attached at a type's own declaration site), so this is the natural and only
// consistent scope, matching what TableGenerator/CustomTypeGenerator already assume.
public sealed class DatabaseContractDescriptor {
    public List<DatabaseGenerationState> Databases { get; set; } = [];

    // A row type's (or CustomType's) own latest-known revision, independent of any database (point D) -
    // the generator's RHINO020 check walks this against each table's own currently-pinned Revision to
    // find holes in the required [Migration(FromRevision=N)] chain.
    public Dictionary<string, int> TypeRevisions { get; set; } = [];

    public Dictionary<string, CustomTypeDescriptor> CustomTypes { get; set; } = [];
    public List<TableDescriptor> Tables { get; set; } = [];
}
