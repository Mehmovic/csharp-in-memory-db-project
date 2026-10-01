# `src/RhinoDB.Tools.Migration/MigrationStatusCommand.cs` — dev notes

## File header

`rhinodb migration status` - read-only. Loads the project, builds a fresh descriptor from its live
compilation, diffs every table against the last committed Descriptor.json, and prints a per-table
classification. Exits 1 if any table is Breaking, so it doubles as a CI gate (`rhinodb migration status ||
exit 1`) without needing a separate `--check` flag.
