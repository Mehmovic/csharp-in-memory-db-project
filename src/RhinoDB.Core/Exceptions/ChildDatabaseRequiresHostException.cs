namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ChildDatabaseRequiresHostException(Exception? inner = null)
    : Exception("Child database access needs a RhinoHost-backed RhinoCtx (e.g. a [Procedure]) - not available from a "
        + "lifecycle hook, which fires before/independent of the host.", inner);
