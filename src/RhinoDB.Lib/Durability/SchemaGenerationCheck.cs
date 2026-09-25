namespace RhinoDB.Lib.Durability;

public enum SchemaGenerationDecision { UpToDate, MigrationRequired }

// Docs/06-schema-migration.md §3's open-time protocol branch table, as a pure decision function - no I/O,
// no transaction handling, no actual migration. Point C's invalid-generation check (Docs/06-schema-
// migration.md's later addition) runs FIRST, ahead of every other branch, since an invalidated generation
// must refuse regardless of what G_wal/G_db/G_binary otherwise say. The real migration transaction (Phase
// 4, step 20) and the "does a migration chain actually exist" check (a real {Db}-generated capability, not
// built yet) are both deliberately kept OUTSIDE this function - callers supply migrationChainExists as a
// plain bool so this stays testable per-branch without either of those existing yet.
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
