namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class RetentionFloorCannotMoveBackwardException(Exception? inner = null)
    : Exception("Refusing to prune/consolidate to a generation older than this database's already-recorded "
        + "RetainedFromGeneration watermark - the floor only ever moves forward.", inner);
