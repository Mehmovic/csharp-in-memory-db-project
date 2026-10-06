namespace RhinoDB.Lib.Procedures;

static public class ClientErrors {
    static public ErrorKind ToClientKind(ErrorKind kind) => kind switch {
        ErrorKind.WalDurabilityFailed
            or ErrorKind.WalDirectorySyncFailed
            or ErrorKind.ColdStorageDirectorySyncFailed
            or ErrorKind.MultiTxOutcomeUnknown
            or ErrorKind.ApplyFailed
            or ErrorKind.DatabaseClosing
            => ErrorKind.ServerUnavailable,
        _ => kind,
    };

    static public (ErrorKind Kind, ushort CustomCode) ForClient(DbError error) {
        var kind = ToClientKind(error.Kind);
        return (kind, kind == ErrorKind.Custom ? error.CustomCode : (ushort)0);
    }
}
