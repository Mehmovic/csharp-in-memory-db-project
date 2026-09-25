namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class SchemaGenerationInvalidException()
    : Exception("Refusing to open this database - its current schema generation is declared invalid via "
        + "[Database(InvalidGenerations=)], meaning a past migration into it is known to be unsafe.");
