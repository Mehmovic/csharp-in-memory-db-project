using RhinoDB.SchemaContracts;

namespace RhinoDB.SchemaContracts.Test;

public class ContractDescriptorJsonTests {
    static private DatabaseContractDescriptor FullDescriptor() {
        var descriptor = new DatabaseContractDescriptor();
        descriptor.Databases.Add(new DatabaseGenerationState {
            FullName = "TestNs.GameDb",
            Generation = 3,
            InvalidGenerations = { 1 },
            RetainedFromGeneration = 1,
        });
        descriptor.TypeRevisions["TestNs.Player"] = 2;
        descriptor.CustomTypes["TestNs.Loadout"] = new CustomTypeDescriptor {
            FullName = "TestNs.Loadout",
            Fields = { new FieldDescriptor { Path = "WeaponId", TypeFullName = "System.Int32", Kind = RowFieldKind.Unmanaged } },
        };
        descriptor.Tables.Add(new TableDescriptor {
            DatabaseFullName = "TestNs.GameDb",
            Accessor = "Player",
            RowTypeFullName = "TestNs.Player",
            Kind = "Persistent",
            NameHash = 123456789u,
            Revision = 2,
            PrimaryKey = new FieldDescriptor { Path = "Id", TypeFullName = "System.Int32", Kind = RowFieldKind.Unmanaged },
            Fields = {
                new FieldDescriptor { Path = "Id", TypeFullName = "System.Int32", Kind = RowFieldKind.Unmanaged },
                new FieldDescriptor { Path = "Name", TypeFullName = "System.String", Kind = RowFieldKind.String },
                new FieldDescriptor { Path = "Loadout.WeaponId", TypeFullName = "System.Int32", Kind = RowFieldKind.Unmanaged },
            },
            Indexes = { new IndexDescriptor { Accessor = "Name", Kind = "Hash", Uniqueness = "NonUnique", FieldPaths = { "Name" } } },
            RevisionHistory = { new RevisionHistoryEntry { Generation = 1, Revision = 1 }, new RevisionHistoryEntry { Generation = 3, Revision = 2 } },
        });
        return descriptor;
    }

    [Test]
    public void Serialize_ThenParse_RoundTripsEveryField() {
        var original = FullDescriptor();

        var json = ContractDescriptorJson.Serialize(original);
        var parsed = ContractDescriptorJson.Parse(json);

        Assert.That(parsed.Databases, Has.Count.EqualTo(1));
        Assert.That(parsed.Databases[0].FullName, Is.EqualTo("TestNs.GameDb"));
        Assert.That(parsed.Databases[0].Generation, Is.EqualTo(3));
        Assert.That(parsed.Databases[0].InvalidGenerations, Is.EqualTo(new[] { 1 }));
        Assert.That(parsed.Databases[0].RetainedFromGeneration, Is.EqualTo(1));

        Assert.That(parsed.TypeRevisions["TestNs.Player"], Is.EqualTo(2));

        Assert.That(parsed.CustomTypes["TestNs.Loadout"].Fields, Has.Count.EqualTo(1));
        Assert.That(parsed.CustomTypes["TestNs.Loadout"].Fields[0].Path, Is.EqualTo("WeaponId"));

        Assert.That(parsed.Tables, Has.Count.EqualTo(1));
        var table = parsed.Tables[0];
        Assert.That(table.DatabaseFullName, Is.EqualTo("TestNs.GameDb"));
        Assert.That(table.Accessor, Is.EqualTo("Player"));
        Assert.That(table.RowTypeFullName, Is.EqualTo("TestNs.Player"));
        Assert.That(table.Kind, Is.EqualTo("Persistent"));
        Assert.That(table.NameHash, Is.EqualTo(123456789u));
        Assert.That(table.Revision, Is.EqualTo(2));
        Assert.That(table.PrimaryKey.Path, Is.EqualTo("Id"));
        Assert.That(table.Fields.Select(f => f.Path), Is.EqualTo(new[] { "Id", "Name", "Loadout.WeaponId" }));
        Assert.That(table.Fields[2].Kind, Is.EqualTo(RowFieldKind.Unmanaged));
        Assert.That(table.Indexes, Has.Count.EqualTo(1));
        Assert.That(table.Indexes[0].FieldPaths, Is.EqualTo(new[] { "Name" }));
        Assert.That(table.RemovedAtGeneration, Is.Null);
        Assert.That(table.RevisionHistory.Select(h => (h.Generation, h.Revision)), Is.EqualTo(new[] { (1, 1), (3, 2) }));
    }

    [Test]
    public void Serialize_WritesEnumsAsReadableStringsNotIntegers() {
        var json = ContractDescriptorJson.Serialize(FullDescriptor());

        Assert.That(json, Does.Contain("\"Unmanaged\""));
        Assert.That(json, Does.Contain("\"String\""));
    }

    [Test]
    public void Parse_MalformedJson_ThrowsSchemaContractDescriptorException() {
        Assert.Throws<SchemaContractDescriptorException>(() => ContractDescriptorJson.Parse("{ not valid json"));
    }

    [Test]
    public void Parse_JsonNullLiteral_ThrowsSchemaContractDescriptorException() {
        Assert.Throws<SchemaContractDescriptorException>(() => ContractDescriptorJson.Parse("null"));
    }

    [Test]
    public void Parse_EmptyObject_ReturnsAllDefaults() {
        var parsed = ContractDescriptorJson.Parse("{}");

        Assert.That(parsed.Databases, Is.Empty);
        Assert.That(parsed.TypeRevisions, Is.Empty);
        Assert.That(parsed.CustomTypes, Is.Empty);
        Assert.That(parsed.Tables, Is.Empty);
    }

    [Test]
    public void RemovedAtGeneration_RoundTripsWhenSet() {
        var descriptor = FullDescriptor();
        descriptor.Tables[0].RemovedAtGeneration = 5;

        var parsed = ContractDescriptorJson.Parse(ContractDescriptorJson.Serialize(descriptor));

        Assert.That(parsed.Tables[0].RemovedAtGeneration, Is.EqualTo(5));
    }
}
