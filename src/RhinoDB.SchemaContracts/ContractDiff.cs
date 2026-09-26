namespace RhinoDB.SchemaContracts;

static public class ContractDiff {
    static public DiffClassification Diff(TableDescriptor? oldTable, TableDescriptor? newTable) {
        if (oldTable is null) return DiffClassification.Unchanged;
        if (newTable is null) return DiffClassification.Removed;

        if (oldTable.Kind != newTable.Kind) return DiffClassification.Breaking;
        if (oldTable.PrimaryKey.TypeFullName != newTable.PrimaryKey.TypeFullName) return DiffClassification.Breaking;

        return DiffFields(oldTable.Fields, newTable.Fields);
    }

    static private DiffClassification DiffFields(List<FieldDescriptor> oldFields, List<FieldDescriptor> newFields) {
        if (newFields.Count < oldFields.Count) return DiffClassification.Breaking;

        for (var i = 0; i < oldFields.Count; i++) {
            if (oldFields[i].Path != newFields[i].Path || oldFields[i].TypeFullName != newFields[i].TypeFullName)
                return DiffClassification.Breaking;
        }

        return newFields.Count == oldFields.Count ? DiffClassification.Unchanged : DiffClassification.AdditiveOnly;
    }

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
