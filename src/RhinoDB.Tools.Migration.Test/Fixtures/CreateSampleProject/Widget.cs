using RhinoDB.Core.Tables;

namespace CreateSampleProject;

[Database]
public partial class SampleDb { }

[Table<SampleDb>(TableKind.Persistent)]
public readonly partial record struct Widget([PrimaryKey] int Id, string Name);
