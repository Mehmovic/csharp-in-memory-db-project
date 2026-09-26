Console.WriteLine("""
    RhinoDB.Sandbox.MigrationFixture - historical fixture project for Phase 5 step 25's real
    breaking-change dry run (see Docs/06-schema-migration.md Section 10). Its generation-0 bytes were
    captured once, before Rating was inserted into Player, into
    src/RhinoDB.Generators.Test/Fixtures/SchemaMigration/Generation0/Player.bin - never regenerated.
    This project itself now sits at generation 1, with a real [Migration(FromRevision = 0)] bridging
    the two shapes - see RhinoDB.Generators.Test's own end-to-end test for the actual proof.
    """);
return 0;
