using RhinoDB.Core;

namespace RhinoDB.Lib.Procedures.Test;

// Clients learn a kind and a custom code, and engine-health kinds all read as ServerUnavailable.
public class ClientErrorsTests {
    static private readonly ErrorKind[] CollapsedToServerUnavailable = [
        ErrorKind.WalDurabilityFailed,
        ErrorKind.WalDirectorySyncFailed,
        ErrorKind.ColdStorageDirectorySyncFailed,
        ErrorKind.MultiTxOutcomeUnknown,
        ErrorKind.ApplyFailed,
        ErrorKind.DatabaseClosing,
    ];

    [Test]
    public void EveryErrorKind_MapsToItself_ExceptTheEngineKinds_WhichBecomeServerUnavailable() {
        Assert.Multiple(() => {
            foreach (var kind in Enum.GetValues<ErrorKind>()) {
                var expected = CollapsedToServerUnavailable.Contains(kind) ? ErrorKind.ServerUnavailable : kind;
                Assert.That(ClientErrors.ToClientKind(kind), Is.EqualTo(expected), kind.ToString());
            }
        });
    }

    [Test]
    public void ACustomError_KeepsItsCode() {
        Assert.That(ClientErrors.ForClient(DbError.Custom(4242)), Is.EqualTo((ErrorKind.Custom, (ushort)4242)));
    }

    [Test]
    public void EveryOtherKind_SendsCodeZero() {
        Assert.That(ClientErrors.ForClient(DbError.ProcedureFailed(new Exception("secret detail"))), Is.EqualTo((ErrorKind.ProcedureFailed, (ushort)0)));
        Assert.That(ClientErrors.ForClient(DbError.WalDurabilityFailed()), Is.EqualTo((ErrorKind.ServerUnavailable, (ushort)0)));
    }
}
