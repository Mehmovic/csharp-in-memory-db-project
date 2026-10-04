using System.Runtime.CompilerServices;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Tools.Migration.Test;

// `rhinodb migration status`, covering the diff half. EndToEndTests.cs only reaches the
// "no committed descriptor" branch, because SampleProject ships without a Descriptor.json -
// so every table reports as new and none of ContractDiff is exercised.
//
// These write a descriptor into the fixture and remove it again in a finally, which is why
// they share the fixture rather than building a throwaway project: a synthetic temp project
// has no restore output, so MSBuildWorkspace hands CompilationWalker a compilation whose
// framework references never resolved, and BuildDescriptor throws on the empty attribute
// arguments (CompilationWalker.cs:35) instead of describing the tables.
//
// The fixture is not mutated in any lasting way - RhinoContracts\Descriptor.json is created
// per test and deleted afterwards, and it does not exist in the repository.
public class MigrationStatusCommandTests {
    const string DbName = "global::SampleProject.SampleDb";
    const string RowName = "global::SampleProject.Widget";

    static private string FixtureProject([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", "SampleProject", "SampleProject.csproj");

    static private string DescriptorPath(string projectPath) =>
        Path.Combine(Path.GetDirectoryName(projectPath)!, "RhinoContracts", "Descriptor.json");

    static private TableDescriptor Table(string kind = "Instant", bool omitName = false) {
        // omitName models a descriptor written before Name existed: the source now has MORE
        // fields than the old descriptor, which ContractDiff classifies as AdditiveOnly.
        var fields = new List<FieldDescriptor> {
            new FieldDescriptor { Path = "Id", TypeFullName = "int", Kind = RowFieldKind.Unmanaged },
        };
        if (!omitName)
            fields.Add(new FieldDescriptor { Path = "Name", TypeFullName = "string", Kind = RowFieldKind.Unmanaged });

        return new TableDescriptor {
            DatabaseFullName = DbName,
            Accessor = "Widget",
            RowTypeFullName = RowName,
            Kind = kind,
            PrimaryKey = new FieldDescriptor { Path = "Id", TypeFullName = "int", Kind = RowFieldKind.Unmanaged },
            Fields = fields,
        };
    }

    // Writes the descriptor, runs status, removes the descriptor - in that order, always.
    static private (int Code, string Out, string Err) RunWithDescriptor(
        string projectPath, params TableDescriptor[] tables) {

        var descriptorPath = DescriptorPath(projectPath);
        var descriptorDirectory = Path.GetDirectoryName(descriptorPath)!;
        var directoryExisted = Directory.Exists(descriptorDirectory);
        Directory.CreateDirectory(descriptorDirectory);
        File.WriteAllText(descriptorPath,
            ContractDescriptorJson.Serialize(new DatabaseContractDescriptor { Tables = [.. tables] }));

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        try {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
            var exitCode = MigrationTool.Run(["status", "--project", projectPath]);
            return (exitCode, outWriter.ToString(), errorWriter.ToString());
        } finally {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            File.Delete(descriptorPath);
            if (!directoryExisted) Directory.Delete(descriptorDirectory);
        }
    }

    [Test]
    public void Status_WithACommittedDescriptorThatMatches_ReportsUnchangedAndExitsZero() {
        var (code, output, _) = RunWithDescriptor(FixtureProject(), Table());

        Assert.Multiple(() => {
            Assert.That(code, Is.EqualTo(0), "an unchanged contract is not a failure.");
            Assert.That(output, Does.Contain("SampleDb.Widget: Unchanged"));
            Assert.That(output, Does.Not.Contain("(new)"), "a committed descriptor exists, so this is not a new table.");
        });
    }

    [Test]
    public void Status_WhenOnlyTheTableKindChanged_ReportsBreakingAndExitsNonZero() {
        var (code, output, _) = RunWithDescriptor(FixtureProject(), Table(kind: "Persistent")); // source says Instant

        Assert.Multiple(() => {
            Assert.That(code, Is.EqualTo(1), "a breaking diff must fail so a pipeline can gate on it.");
            Assert.That(output, Does.Contain("SampleDb.Widget: Breaking"));
        });
    }

    [Test]
    public void Status_WhenTheSourceHasMoreFieldsThanTheOldDescriptor_ReportsAdditiveOnlyAndExitsZero() {
        var (code, output, _) = RunWithDescriptor(FixtureProject(), Table(omitName: true));

        Assert.Multiple(() => {
            Assert.That(code, Is.EqualTo(0), "a field added since the descriptor was written classifies as additive-only.");
            Assert.That(output, Does.Contain("SampleDb.Widget: AdditiveOnly"));
        });
    }

    [Test]
    public void Status_WhenTheOldDescriptorHasATableTheSourceNoLongerHas_ReportsRemovedAndExitsZero() {
        var (code, output, _) = RunWithDescriptor(FixtureProject(),
            Table(),
            new TableDescriptor {
                DatabaseFullName = DbName,
                Accessor = "Retired",
                RowTypeFullName = "global::SampleProject.Retired",
                Kind = "Instant",
                PrimaryKey = new FieldDescriptor { Path = "Id", TypeFullName = "int", Kind = RowFieldKind.Unmanaged },
                Fields = [new FieldDescriptor { Path = "Id", TypeFullName = "int", Kind = RowFieldKind.Unmanaged }],
            });

        Assert.Multiple(() => {
            Assert.That(code, Is.EqualTo(0), "a removed table is not a breaking change to the tables that remain.");
            Assert.That(output, Does.Contain("Retired: Removed"));
            Assert.That(output, Does.Contain("SampleDb.Widget: Unchanged"));
        });
    }

    [Test]
    public void Status_WithoutAProjectFlag_FailsLoudlyOnStderrRatherThanGuessing() {
        var originalError = Console.Error;
        var errorWriter = new StringWriter();
        string error;
        try {
            Console.SetError(errorWriter);
            MigrationTool.Run(["status"]);
            error = errorWriter.ToString();
        } finally {
            Console.SetError(originalError);
        }

        Assert.Multiple(() => {
            Assert.That(error, Is.Not.Empty, "an unresolvable project must say so on stderr rather than fall back silently.");
            Assert.That(error, Does.Contain("--project"));
        });
    }
}