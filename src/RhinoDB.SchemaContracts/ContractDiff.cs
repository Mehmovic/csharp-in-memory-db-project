namespace RhinoDB.SchemaContracts;

// Classifies a schema change between two versions of the SAME table (already matched by identity -
// Accessor/TableIdHash - by whatever calls this; an Accessor rename deliberately makes tableId AND the
// mdbx sub-db name both change, so it looks exactly like "new empty table + orphan" to any identity-based
// matching - per Docs/06-schema-migration.md §6, that requires an explicit rename step the caller supplies,
// not something Diff can infer from two descriptors that no longer share an identity to correlate by).
static public class ContractDiff {
    // oldTable null (newTable not null) - a brand-new table; nothing existed before it to be incompatible
    // with, so Unchanged (no migration needed), not one of the other three classifications.
    // oldTable not null, newTable null - Removed (point 6's orphan-table case).
    // Indexes are never inspected here - derived data (§6's rule: "rebuilt, never migrated"), so an
    // index-only change between two otherwise-identical tables is always Unchanged.
    static public DiffClassification Diff(TableDescriptor? oldTable, TableDescriptor? newTable) {
        if (oldTable is null) return DiffClassification.Unchanged;
        if (newTable is null) return DiffClassification.Removed;

        if (oldTable.Kind != newTable.Kind) return DiffClassification.Breaking;
        if (oldTable.PrimaryKey.TypeFullName != newTable.PrimaryKey.TypeFullName) return DiffClassification.Breaking;

        return DiffFields(oldTable.Fields, newTable.Fields);
    }

    static DiffClassification DiffFields(List<FieldDescriptor> oldFields, List<FieldDescriptor> newFields) {
        if (newFields.Count < oldFields.Count) return DiffClassification.Breaking;

        // Every old field must still be present, unchanged, at the SAME position - covers add-in-the-
        // middle, remove, reorder and retype all in one pass: any of those makes some index i disagree
        // between old and new before the tail even matters.
        for (var i = 0; i < oldFields.Count; i++) {
            if (oldFields[i].Path != newFields[i].Path || oldFields[i].TypeFullName != newFields[i].TypeFullName)
                return DiffClassification.Breaking;
        }

        return newFields.Count == oldFields.Count ? DiffClassification.Unchanged : DiffClassification.AdditiveOnly;
    }

    // Descriptor consistency check (review point 4's "duplicate/skipped generation number" concern,
    // reframed against what's actually representable in this data model): DatabaseGenerationState only
    // ever records the CURRENT generation, not a history list, so "duplicate" isn't directly checkable
    // there - the concrete, checkable symptom the SAME underlying bad-merge scenario would actually
    // produce is a table pinned to a revision NEWER than its own row type's latest known revision, which
    // can only happen if two independent branches both bumped the same type's revision number and a merge
    // silently picked the wrong pairing. A revision LOWER than the type's latest is always fine (that
    // table just hasn't been migrated that far yet - the normal, expected steady state for most tables
    // most of the time).
    static public List<string> ValidateRevisions(DatabaseContractDescriptor descriptor) {
        var violations = new List<string>();
        foreach (var table in descriptor.Tables) {
            var latestKnown = descriptor.TypeRevisions.TryGetValue(table.RowTypeFullName, out var r) ? r : 0;
            if (table.Revision > latestKnown)
                violations.Add(
                    $"Table '{table.DatabaseFullName}.{table.Accessor}' is pinned to revision {table.Revision}, but "
                    + $"its row type '{table.RowTypeFullName}' has no revision that high on record (latest known: "
                    + $"{latestKnown}) - the descriptor is internally inconsistent, likely from a bad merge.");
        }
        return violations;
    }
}
