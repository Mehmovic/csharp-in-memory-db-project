namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class SchemaGenerationMismatchException()
    : Exception("Refusing to open this database - its schema generation does not agree with what this "
        + "binary expects (the WAL's own generation disagrees with the checkpointed one, the database is "
        + "newer than this binary supports, or no migration step exists to bridge the gap). Never "
        + "interpreting bytes written under a different contract than the one asked for.");
