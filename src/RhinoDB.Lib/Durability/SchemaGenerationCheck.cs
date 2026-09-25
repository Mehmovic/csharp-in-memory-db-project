namespace RhinoDB.Lib.Durability;

public enum SchemaGenerationDecision { UpToDate, MigrationRequired }

static public class SchemaGenerationCheck {
    static public Result<SchemaGenerationDecision> EnsureCurrentGeneration(
        int currentGeneration, int walGeneration, int binaryGeneration, bool migrationChainExists, bool isCurrentGenerationInvalid
    ) {
        if (isCurrentGenerationInvalid)
            return Result<SchemaGenerationDecision>.Error(DbError.SchemaGenerationInvalid());

        if (walGeneration != currentGeneration)
            return Result<SchemaGenerationDecision>.Error(DbError.SchemaGenerationMismatch());

        if (currentGeneration == binaryGeneration)
            return Result<SchemaGenerationDecision>.Ok(SchemaGenerationDecision.UpToDate);

        if (currentGeneration > binaryGeneration)
            return Result<SchemaGenerationDecision>.Error(DbError.SchemaGenerationMismatch());

        return migrationChainExists
            ? Result<SchemaGenerationDecision>.Ok(SchemaGenerationDecision.MigrationRequired)
            : Result<SchemaGenerationDecision>.Error(DbError.SchemaGenerationMismatch());
    }
}
