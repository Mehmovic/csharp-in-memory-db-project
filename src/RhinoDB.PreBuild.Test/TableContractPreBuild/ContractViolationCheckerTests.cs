namespace RhinoDB.PreBuild.Test;

public class ContractViolationCheckerTests {
    static private ParsedShorthandFile ParseTable(string fields) => ShorthandParser.Parse($$"""
                                                                                            namespace TestNs;

                                                                                            [InstantTable(typeof(GameDb))]
                                                                                            public readonly partial record struct Row(
                                                                                                {{fields}}
                                                                                            );
                                                                                            """);

    [Test]
    public void Check_AppendingAFieldAtTheEnd_IsSafe() {
        var oldFile = ParseTable("int A, int B");
        var newFile = ParseTable("int A, int B, int C");

        Assert.DoesNotThrow(() => ContractViolationChecker.Check(oldFile, newFile));
    }

    [Test]
    public void Check_InsertingAnUnlabeledFieldInTheMiddle_ShiftsAnAutoComputedSlotToADifferentField_Throws() {
        // old: A=0, B=1. new: A=0, X=1 (inserted), B=2 - slot 1 silently repoints from B to X.
        var oldFile = ParseTable("int A, int B");
        var newFile = ParseTable("int A, int X, int B");

        var ex = Assert.Throws<ContractViolationException>(() => ContractViolationChecker.Check(oldFile, newFile));
        Assert.That(ex!.Message, Does.Contain("slot 1"));
        Assert.That(ex.Message, Does.Contain("'B'"));
        Assert.That(ex.Message, Does.Contain("'X'"));
    }

    [Test]
    public void Check_RenamingAnExplicitlyPinnedField_IsSafe() {
        var oldFile = ParseTable("int A, [PackId(1)] long B");
        var newFile = ParseTable("int A, [PackId(1)] long C");

        Assert.DoesNotThrow(() => ContractViolationChecker.Check(oldFile, newFile));
    }

    [Test]
    public void Check_ChangingAFieldsTypeAtTheSameSlot_Throws() {
        var oldFile = ParseTable("int A");
        var newFile = ParseTable("long A");

        var ex = Assert.Throws<ContractViolationException>(() => ContractViolationChecker.Check(oldFile, newFile));
        Assert.That(ex!.Message, Does.Contain("changed type"));
    }

    [Test]
    public void Check_ChangingAnExplicitlyPinnedFieldsType_StillThrows_PinningDoesNotExemptTypeChanges() {
        var oldFile = ParseTable("[PackId(0)] int A");
        var newFile = ParseTable("[PackId(0)] long A");

        var ex = Assert.Throws<ContractViolationException>(() => ContractViolationChecker.Check(oldFile, newFile));
        Assert.That(ex!.Message, Does.Contain("changed type"));
    }

    [Test]
    public void Check_RemovingAFieldEntirely_IsNotItselfAViolation() {
        var oldFile = ParseTable("int A, int B");
        var newFile = ParseTable("int A");

        Assert.DoesNotThrow(() => ContractViolationChecker.Check(oldFile, newFile));
    }

    [Test]
    public void Check_IdenticalFiles_IsSafe() {
        var file = ParseTable("[PrimaryKey] int Id, string Name");

        Assert.DoesNotThrow(() => ContractViolationChecker.Check(file, file));
    }
}
