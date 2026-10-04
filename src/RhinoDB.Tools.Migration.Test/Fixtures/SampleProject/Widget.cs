using RhinoDB.Core.Tables;

namespace SampleProject;

[Database]
public partial class SampleDb { }

[Table<SampleDb>(TableKind.Instant)]
public readonly partial record struct Widget([PrimaryKey] int Id, string Name);
