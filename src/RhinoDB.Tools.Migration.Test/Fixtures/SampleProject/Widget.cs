using RhinoDB.Core.Tables;

namespace SampleProject;

[Database]
public partial class SampleDb { }

[Table(TableKind.Instant, typeof(SampleDb))]
public readonly partial record struct Widget([PrimaryKey] int Id, string Name);
