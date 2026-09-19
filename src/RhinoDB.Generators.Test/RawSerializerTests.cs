namespace RhinoDB.Test.Generators;

// TableGenerator's hand-rolled Raw/positional serializer (SerializeRow/DeserializeRow/SerializeKey/
// DeserializeKey) - field-by-field via MemoryPack's own low-level writer/reader primitives, no
// [MemoryPackable] dependency. Exercised directly here, before any real call site (cold.Stage,
// ReplayApply, eviction, the ring buffer) is wired to use it.
public class RawSerializerTests {
    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class GameDb : DbContext<GameDbTransaction> { }

        [Table(TableKind.Instant, typeof(GameDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Metric(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] uint Count,
            [property: MemoryPackOrder(2)] [property: Key(2)] long Timestamp,
            [property: MemoryPackOrder(3)] [property: Key(3)] decimal Amount,
            [property: MemoryPackOrder(4)] [property: Key(4)] string Label
        );

        [Table(TableKind.Persistent, typeof(GameDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Ledger(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] uint Count,
            [property: MemoryPackOrder(2)] [property: Key(2)] long Timestamp,
            [property: MemoryPackOrder(3)] [property: Key(3)] decimal Amount,
            [property: MemoryPackOrder(4)] [property: Key(4)] string Label
        );
        """;

    [Test]
    public void InstantTable_SerializeRowThenDeserializeRow_RoundTripsAllFieldTypes() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var rowType = asm.GetType("TestNs.Metric")!;
        var row = Activator.CreateInstance(rowType, 7, 42u, 123456789L, 19.99m, "widget")!;

        var bytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbMetricOps", "SerializeRow", row)!;
        var roundTripped = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbMetricOps", "DeserializeRow", bytes);

        Assert.That(roundTripped, Is.EqualTo(row));
    }

    [Test]
    public void InstantTable_SerializeKeyThenDeserializeKey_RoundTrips() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);

        var bytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbMetricOps", "SerializeKey", 99)!;
        var roundTripped = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbMetricOps", "DeserializeKey", bytes);

        Assert.That(roundTripped, Is.EqualTo(99));
    }

    [Test]
    public void PersistentTable_SerializeRowThenDeserializeRow_RoundTripsAllFieldTypes() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var rowType = asm.GetType("TestNs.Ledger")!;
        var row = Activator.CreateInstance(rowType, 3, 7u, 987654321L, 1000.50m, "entry")!;

        var bytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbLedgerOps", "SerializeRow", row)!;
        var roundTripped = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbLedgerOps", "DeserializeRow", bytes);

        Assert.That(roundTripped, Is.EqualTo(row));
    }

    // A field type that isn't unmanaged and isn't string (e.g. an array) falls into
    // RowFieldKind.Other and routes through MemoryPack's generic WriteValue<T>/ReadValue<T>
    // formatter-dispatch instead of a hand-rolled primitive write. Confirmed empirically:
    // WriteValue<T>/ReadValue<T> have NO reflection fallback (unlike the top-level
    // MemoryPackSerializer.Serialize<T>) - they throw MemoryPackSerializationException if no real
    // formatter is registered for T. int[] works here because MemoryPack ships a built-in formatter
    // for arrays of unmanaged element types, registered globally, no per-type generation needed.
    // A developer-authored nested record struct (not covered by a built-in formatter) instead
    // needs its own [MemoryPackable]/[MessagePackObject] attributes, with MemoryPack's/MessagePack's
    // real generators running against it - GeneratorTestHost only runs TableGenerator, so proving
    // that end-to-end needs the harness extended to also run those generators (tracked separately,
    // see the plan's CustomType section).
    private const string SourceWithArrayField = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class GameDb : DbContext<GameDbTransaction> { }

        [Table(TableKind.Instant, typeof(GameDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Player(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] int[] Ranks
        );
        """;

    [Test]
    public void OtherKindField_BuiltInArrayFormatter_SerializeRowThenDeserializeRow_RoundTrips() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(SourceWithArrayField);
        var rowType = asm.GetType("TestNs.Player")!;
        var row = Activator.CreateInstance(rowType, 1, new[] { 1, 2, 3 })!;

        var bytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbPlayerOps", "SerializeRow", row)!;
        var roundTripped = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbPlayerOps", "DeserializeRow", bytes);

        var ranksProp = rowType.GetProperty("Ranks")!;
        Assert.That(ranksProp.GetValue(roundTripped), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    // List/Dictionary/HashSet/Queue/Stack (BuiltInFormattedGenericCollections) are exempted from
    // RHINO016's mandatory [CustomType] check the same way arrays are - both MemoryPack and MessagePack
    // ship global formatters for these with no attribute needed, so they route through the same
    // WriteValue<T>/ReadValue<T> generic dispatch as the array case above.
    private const string SourceWithCollectionFields = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;
        using System.Collections.Generic;

        namespace TestNs;

        [Database]
        public partial class GameDb : DbContext<GameDbTransaction> { }

        [Table(TableKind.Instant, typeof(GameDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Inventory(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] List<int> ItemIds,
            [property: MemoryPackOrder(2)] [property: Key(2)] Dictionary<int, string> ItemNames
        );
        """;

    [Test]
    public void OtherKindField_BuiltInListAndDictionaryFormatters_SerializeRowThenDeserializeRow_RoundTrips() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(SourceWithCollectionFields);
        var rowType = asm.GetType("TestNs.Inventory")!;
        var itemIds = new List<int> { 1, 2, 3 };
        var itemNames = new Dictionary<int, string> { [1] = "Sword", [2] = "Shield" };
        var row = Activator.CreateInstance(rowType, 1, itemIds, itemNames)!;

        var bytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbInventoryOps", "SerializeRow", row)!;
        var roundTripped = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbInventoryOps", "DeserializeRow", bytes);

        Assert.That(rowType.GetProperty("ItemIds")!.GetValue(roundTripped), Is.EqualTo(itemIds));
        Assert.That(rowType.GetProperty("ItemNames")!.GetValue(roundTripped), Is.EqualTo(itemNames));
    }

    // SerializeVersionedMemoryPack/SerializeMessagePack call MemoryPackSerializer.Serialize<T>/
    // MessagePackSerializer.Serialize<T> directly - unlike SerializeRow/SerializeKey (hand-rolled,
    // no formatter dependency), these need a REAL formatter registered for the row type. Confirmed
    // empirically: even a [MemoryPackable]-decorated type throws "not registered in this provider"
    // here, because GeneratorTestHost only runs TableGenerator - MemoryPack.Generator/
    // MessagePackAnalyzer never actually run against Metric/Ledger in this harness, so no real
    // formatter gets produced for them despite the attribute being present in source. A fully
    // unmanaged row (no reference-typed fields) sidesteps this for MemoryPack specifically - its
    // Serialize<T>/Deserialize<T> take a blit fast path requiring no formatter lookup at all - which
    // is what this test exercises. Proving the general (reference-typed-field) case, and the
    // MessagePack side (no equivalent formatter-free fast path), needs GeneratorTestHost extended to
    // run those generators too - tracked as task #82, not solved here.
    private const string SourceUnmanagedRow = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class PointDb : DbContext<PointDbTransaction> { }

        [Table(TableKind.Instant, typeof(PointDb))]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Point(
            [PrimaryKey] [property: Key(0)] int Id,
            [property: Key(1)] int X,
            [property: Key(2)] int Y
        );
        """;

    [Test]
    public void UnmanagedRow_SerializeVersionedMemoryPackThenDeserialize_RoundTrips() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(SourceUnmanagedRow);
        var rowType = asm.GetType("TestNs.Point")!;
        var row = Activator.CreateInstance(rowType, 1, 3, 4)!;

        var bytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.PointDbPointOps", "SerializeVersionedMemoryPack", row)!;
        var roundTripped = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.PointDbPointOps", "DeserializeVersionedMemoryPack", bytes);

        Assert.That(roundTripped, Is.EqualTo(row));
    }

    [Test]
    public void SerializeRow_ForDistinctRows_ProducesDistinctBytes() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var rowType = asm.GetType("TestNs.Metric")!;
        var rowA = Activator.CreateInstance(rowType, 1, 1u, 1L, 1m, "a")!;
        var rowB = Activator.CreateInstance(rowType, 2, 2u, 2L, 2m, "b")!;

        var bytesA = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbMetricOps", "SerializeRow", rowA)!;
        var bytesB = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbMetricOps", "SerializeRow", rowB)!;

        Assert.That(bytesA, Is.Not.EqualTo(bytesB));
    }
}
