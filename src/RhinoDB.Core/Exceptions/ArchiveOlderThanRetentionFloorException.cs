namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ArchiveOlderThanRetentionFloorException()
    : Exception("Refusing genesis replay - the oldest archived WAL segment on disk is older than this "
        + "binary's own retention floor (RetainedFromGeneration). This binary's [Migration(FromRevision=N)] "
        + "chain no longer has a reader old enough to decode it; open with an older binary, or restore from "
        + "an archive that has not been pruned past this point.");
