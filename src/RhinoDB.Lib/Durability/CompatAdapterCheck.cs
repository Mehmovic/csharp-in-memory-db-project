namespace RhinoDB.Lib.Durability;

public enum CompatAdapterDecision { Upgrade, Passthrough, Downgrade }

public enum CompatAdapterDirection { Incoming, Outgoing }

static public class CompatAdapterCheck {
    static public Result<CompatAdapterDecision> EnsureCompatible(
        bool isClientAppVersionInvalid,
        bool isClientRevisionInvalid,
        int clientRevision,
        int currentRevision,
        CompatAdapterDirection direction,
        bool canDowngradeToClientRevision
    ) {
        if (isClientAppVersionInvalid)
            return Result<CompatAdapterDecision>.Error(DbError.ClientAppVersionInvalid());

        if (isClientRevisionInvalid)
            return Result<CompatAdapterDecision>.Error(DbError.RowRevisionInvalid());

        if (clientRevision == currentRevision)
            return Result<CompatAdapterDecision>.Ok(CompatAdapterDecision.Passthrough);

        if (direction == CompatAdapterDirection.Incoming)
            return Result<CompatAdapterDecision>.Ok(CompatAdapterDecision.Upgrade);

        return canDowngradeToClientRevision
            ? Result<CompatAdapterDecision>.Ok(CompatAdapterDecision.Downgrade)
            : Result<CompatAdapterDecision>.Error(DbError.DowngradeUnavailable());
    }
}
