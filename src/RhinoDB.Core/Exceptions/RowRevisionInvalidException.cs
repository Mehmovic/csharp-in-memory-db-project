namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class RowRevisionInvalidException()
    : Exception("Refusing to bridge to/from this row-type revision - it is declared invalid via "
        + "[InvalidRevisions(Revisions=)], meaning a past migration into or out of it is known to be unsafe.");
