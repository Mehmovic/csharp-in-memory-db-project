using RhinoDB.Core;
using RhinoDB.Lib.Durability;

namespace RhinoDB.Lib.Durability.Test;

// Docs/06-schema-migration.md §3's open-time protocol branch table, one test per branch - pure logic, no
// I/O, migration itself stubbed via a plain bool (Phase 4 step 19's explicit scope).
public class SchemaGenerationCheckTests {
    [Test]
    public void InvalidGeneration_RefusesFirst_RegardlessOfWhatOtherwiseWouldHaveBeenFine() {
        var result = SchemaGenerationCheck.EnsureCurrentGeneration(
            currentGeneration: 3, walGeneration: 3, binaryGeneration: 3, migrationChainExists: true, isCurrentGenerationInvalid: true);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.SchemaGenerationInvalid));
    }

    [Test]
    public void WalGenerationDisagreesWithDbGeneration_Refuses() {
        var result = SchemaGenerationCheck.EnsureCurrentGeneration(
            currentGeneration: 2, walGeneration: 3, binaryGeneration: 2, migrationChainExists: true, isCurrentGenerationInvalid: false);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.SchemaGenerationMismatch));
    }

    [Test]
    public void DbGenerationMatchesBinary_IsUpToDate() {
        var result = SchemaGenerationCheck.EnsureCurrentGeneration(
            currentGeneration: 5, walGeneration: 5, binaryGeneration: 5, migrationChainExists: false, isCurrentGenerationInvalid: false);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(SchemaGenerationDecision.UpToDate));
    }

    [Test]
    public void DbBehindBinary_MigrationChainExists_RequiresMigration() {
        var result = SchemaGenerationCheck.EnsureCurrentGeneration(
            currentGeneration: 2, walGeneration: 2, binaryGeneration: 5, migrationChainExists: true, isCurrentGenerationInvalid: false);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(SchemaGenerationDecision.MigrationRequired));
    }

    [Test]
    public void DbBehindBinary_NoMigrationChain_Refuses() {
        var result = SchemaGenerationCheck.EnsureCurrentGeneration(
            currentGeneration: 2, walGeneration: 2, binaryGeneration: 5, migrationChainExists: false, isCurrentGenerationInvalid: false);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.SchemaGenerationMismatch));
    }

    [Test]
    public void DbAheadOfBinary_RefusesAsADowngrade() {
        var result = SchemaGenerationCheck.EnsureCurrentGeneration(
            currentGeneration: 6, walGeneration: 6, binaryGeneration: 5, migrationChainExists: true, isCurrentGenerationInvalid: false);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.SchemaGenerationMismatch));
    }
}
