using RhinoDB.SchemaContracts;

namespace RhinoDB.SchemaContracts.Test;

public class ContractDiffTests {
    static private FieldDescriptor Field(string path, string typeFullName, RowFieldKind kind = RowFieldKind.Unmanaged) => new FieldDescriptor { Path = path, TypeFullName = typeFullName, Kind = kind };

    static private TableDescriptor Table(
        string accessor = "Player",
        string kind = "Persistent",
        string pkType = "int",
        params FieldDescriptor[] fields
    ) => new TableDescriptor {
        DatabaseFullName = "TestNs.GameDb",
        Accessor = accessor,
        RowTypeFullName = "global::TestNs.Player",
        Kind = kind,
        TableIdHash = 1,
        Revision = 0,
        PrimaryKey = Field("Id", pkType),
        Fields = [.. fields],
    };

    [Test]
    public void Diff_BothNull_IsMeaninglessButStillUnchanged_NewTableCaseCoversTheRealScenario() {
        // oldTable null represents "table didn't exist before" - nothing to migrate away from.
        var newTable = Table(fields: [Field("Id", "int"), Field("Name", "string", RowFieldKind.String)]);
        Assert.That(ContractDiff.Diff(null, newTable), Is.EqualTo(DiffClassification.Unchanged));
    }

    [Test]
    public void Diff_NewTableIsNull_IsRemoved() {
        var oldTable = Table(fields: [Field("Id", "int")]);
        Assert.That(ContractDiff.Diff(oldTable, null), Is.EqualTo(DiffClassification.Removed));
    }

    [Test]
    public void Diff_IdenticalFieldsAndKindAndPrimaryKey_IsUnchanged() {
        var oldTable = Table(fields: [Field("Id", "int"), Field("Name", "string", RowFieldKind.String)]);
        var newTable = Table(fields: [Field("Id", "int"), Field("Name", "string", RowFieldKind.String)]);
        Assert.That(ContractDiff.Diff(oldTable, newTable), Is.EqualTo(DiffClassification.Unchanged));
    }

    [Test]
    public void Diff_FieldAppendedAtTheEnd_IsAdditiveOnly() {
        var oldTable = Table(fields: [Field("Id", "int"), Field("Name", "string", RowFieldKind.String)]);
        var newTable = Table(fields: [Field("Id", "int"), Field("Name", "string", RowFieldKind.String), Field("Age", "int")]);
        Assert.That(ContractDiff.Diff(oldTable, newTable), Is.EqualTo(DiffClassification.AdditiveOnly));
    }

    [Test]
    public void Diff_FieldRemoved_IsBreaking() {
        var oldTable = Table(fields: [Field("Id", "int"), Field("Name", "string", RowFieldKind.String)]);
        var newTable = Table(fields: [Field("Id", "int")]);
        Assert.That(ContractDiff.Diff(oldTable, newTable), Is.EqualTo(DiffClassification.Breaking));
    }

    [Test]
    public void Diff_FieldsReordered_IsBreaking() {
        var oldTable = Table(fields: [Field("Id", "int"), Field("Name", "string", RowFieldKind.String), Field("Age", "int")]);
        var newTable = Table(fields: [Field("Id", "int"), Field("Age", "int"), Field("Name", "string", RowFieldKind.String)]);
        Assert.That(ContractDiff.Diff(oldTable, newTable), Is.EqualTo(DiffClassification.Breaking));
    }

    [Test]
    public void Diff_FieldRetyped_IsBreaking() {
        var oldTable = Table(fields: [Field("Id", "int"), Field("Age", "int")]);
        var newTable = Table(fields: [Field("Id", "int"), Field("Age", "long")]);
        Assert.That(ContractDiff.Diff(oldTable, newTable), Is.EqualTo(DiffClassification.Breaking));
    }

    [Test]
    public void Diff_FieldInsertedInTheMiddle_IsBreaking_NotAdditiveOnlyDespiteLookingLikeAnAdd() {
        var oldTable = Table(fields: [Field("Id", "int"), Field("Name", "string", RowFieldKind.String)]);
        var newTable = Table(fields: [Field("Id", "int"), Field("Nickname", "string", RowFieldKind.String), Field("Name", "string", RowFieldKind.String)]);
        Assert.That(ContractDiff.Diff(oldTable, newTable), Is.EqualTo(DiffClassification.Breaking));
    }

    [Test]
    public void Diff_PrimaryKeyTypeChanged_IsBreaking() {
        var oldTable = Table(pkType: "int", fields: [Field("Id", "int")]);
        var newTable = Table(pkType: "long", fields: [Field("Id", "int")]);
        Assert.That(ContractDiff.Diff(oldTable, newTable), Is.EqualTo(DiffClassification.Breaking));
    }

    [Test]
    public void Diff_KindChangedFromPersistentToInstant_IsBreaking() {
        var oldTable = Table(kind: "Persistent", fields: [Field("Id", "int")]);
        var newTable = Table(kind: "Instant", fields: [Field("Id", "int")]);
        Assert.That(ContractDiff.Diff(oldTable, newTable), Is.EqualTo(DiffClassification.Breaking));
    }

    [Test]
    public void Diff_IndexOnlyChange_NeverAffectsClassification_DerivedDataIsAlwaysRebuiltNeverMigrated() {
        var oldTable = Table(fields: [Field("Id", "int"), Field("Name", "string", RowFieldKind.String)]);
        oldTable.Indexes = [new IndexDescriptor { Accessor = "ByName", Kind = "BTree", Uniqueness = "NonUnique", FieldPaths = ["Name"] }];
        var newTable = Table(fields: [Field("Id", "int"), Field("Name", "string", RowFieldKind.String)]);
        newTable.Indexes = [];

        Assert.That(ContractDiff.Diff(oldTable, newTable), Is.EqualTo(DiffClassification.Unchanged));
    }

    [Test]
    public void ValidateRevisions_EveryTablePinnedAtOrBelowItsTypesLatestRevision_ReturnsNoViolations() {
        var descriptor = new DatabaseContractDescriptor {
            TypeRevisions = new Dictionary<string, int> { ["global::TestNs.Player"] = 3 },
            Tables = [
                new TableDescriptor { DatabaseFullName = "TestNs.GameDb", Accessor = "Player", RowTypeFullName = "global::TestNs.Player", Revision = 3 },
                new TableDescriptor { DatabaseFullName = "TestNs.AdminDb", Accessor = "AdminPlayer", RowTypeFullName = "global::TestNs.Player", Revision = 1 },
            ],
        };

        Assert.That(ContractDiff.ValidateRevisions(descriptor), Is.Empty);
    }

    [Test]
    public void ValidateRevisions_TablePinnedAboveItsTypesLatestKnownRevision_ReportsAViolation() {
        var descriptor = new DatabaseContractDescriptor {
            TypeRevisions = new Dictionary<string, int> { ["global::TestNs.Player"] = 2 },
            Tables = [
                new TableDescriptor { DatabaseFullName = "TestNs.GameDb", Accessor = "Player", RowTypeFullName = "global::TestNs.Player", Revision = 5 },
            ],
        };

        var violations = ContractDiff.ValidateRevisions(descriptor);

        Assert.That(violations, Has.Count.EqualTo(1));
        Assert.That(violations[0], Does.Contain("GameDb.Player"));
        Assert.That(violations[0], Does.Contain("revision 5"));
    }

    [Test]
    public void ValidateRevisions_RowTypeMissingFromTypeRevisions_TreatsItAsRevisionZero() {
        var descriptor = new DatabaseContractDescriptor {
            TypeRevisions = [],
            Tables = [
                new TableDescriptor { DatabaseFullName = "TestNs.GameDb", Accessor = "Player", RowTypeFullName = "global::TestNs.Player", Revision = 0 },
            ],
        };

        Assert.That(ContractDiff.ValidateRevisions(descriptor), Is.Empty);
    }
}
