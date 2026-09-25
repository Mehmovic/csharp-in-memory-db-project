using RhinoDB.Core.Tables;

namespace CreateSampleProject;

[Database]
public partial class SampleDb { }

[Table(TableKind.Persistent, typeof(SampleDb))]
public readonly partial record struct Widget([PrimaryKey] int Id, string Name);
