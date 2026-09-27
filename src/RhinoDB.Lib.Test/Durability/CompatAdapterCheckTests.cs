using RhinoDB.Core;
using RhinoDB.Lib.Durability;

namespace RhinoDB.Lib.Durability.Test;

// The client-facing counterpart to SchemaGenerationCheck - same "pure decision function" shape (plain
// ints/bools in, Result<CompatAdapterDecision> out, zero I/O). No real network transport exists yet, so
// nothing calls EnsureCompatible in anger - this pins the decision table for whenever Stage 6/7/8 does.
public class CompatAdapterCheckTests {
    [Test]
    public void InvalidClientAppVersion_RefusesFirst_RegardlessOfWhatOtherwiseWouldHaveBeenFine() {
        var result = CompatAdapterCheck.EnsureCompatible(
            isClientAppVersionInvalid: true, isClientRevisionInvalid: true,
            clientRevision: 0, currentRevision: 0,
            direction: CompatAdapterDirection.Incoming, canDowngradeToClientRevision: true);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.ClientAppVersionInvalid));
    }

    [Test]
    public void InvalidClientRevision_RefusesBeforeAnyDirectionLogic() {
        var result = CompatAdapterCheck.EnsureCompatible(
            isClientAppVersionInvalid: false, isClientRevisionInvalid: true,
            clientRevision: 1, currentRevision: 3,
            direction: CompatAdapterDirection.Outgoing, canDowngradeToClientRevision: true);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.RowRevisionInvalid));
    }

    [Test]
    public void ClientRevisionMatchesCurrent_IsPassthrough_RegardlessOfDirection() {
        var incoming = CompatAdapterCheck.EnsureCompatible(
            isClientAppVersionInvalid: false, isClientRevisionInvalid: false,
            clientRevision: 2, currentRevision: 2,
            direction: CompatAdapterDirection.Incoming, canDowngradeToClientRevision: false);
        var outgoing = CompatAdapterCheck.EnsureCompatible(
            isClientAppVersionInvalid: false, isClientRevisionInvalid: false,
            clientRevision: 2, currentRevision: 2,
            direction: CompatAdapterDirection.Outgoing, canDowngradeToClientRevision: false);

        Assert.That(incoming.Unwrap(), Is.EqualTo(CompatAdapterDecision.Passthrough));
        Assert.That(outgoing.Unwrap(), Is.EqualTo(CompatAdapterDecision.Passthrough));
    }

    [Test]
    public void IncomingClientBehindCurrent_RequiresUpgrade() {
        var result = CompatAdapterCheck.EnsureCompatible(
            isClientAppVersionInvalid: false, isClientRevisionInvalid: false,
            clientRevision: 0, currentRevision: 2,
            direction: CompatAdapterDirection.Incoming, canDowngradeToClientRevision: false);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(CompatAdapterDecision.Upgrade));
    }

    [Test]
    public void OutgoingClientBehindCurrent_DowngradeChainExists_RequiresDowngrade() {
        var result = CompatAdapterCheck.EnsureCompatible(
            isClientAppVersionInvalid: false, isClientRevisionInvalid: false,
            clientRevision: 0, currentRevision: 2,
            direction: CompatAdapterDirection.Outgoing, canDowngradeToClientRevision: true);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(CompatAdapterDecision.Downgrade));
    }

    [Test]
    public void OutgoingClientBehindCurrent_NoDowngradeChain_Refuses() {
        var result = CompatAdapterCheck.EnsureCompatible(
            isClientAppVersionInvalid: false, isClientRevisionInvalid: false,
            clientRevision: 0, currentRevision: 2,
            direction: CompatAdapterDirection.Outgoing, canDowngradeToClientRevision: false);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.DowngradeUnavailable));
    }
}
