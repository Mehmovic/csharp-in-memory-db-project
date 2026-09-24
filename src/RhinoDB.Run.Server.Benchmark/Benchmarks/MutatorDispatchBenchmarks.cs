using BenchmarkDotNet.Attributes;

using RhinoDB.Lib.Tables;

namespace RhinoDB.Run.Server.Benchmark.Benchmarks;

// Struct-vs-class row mutator dispatch probe (2026-09-23).
//
// OUTCOME: the generated RowMutator became a sealed class. The measured
// dispatch cost (~1 ns/call, ~2x on this micro-loop whose per-row work is
// deliberately trivial) is invisible against real update bodies, and the
// class buys the readonly field, one shared instance across Primary/Idx,
// and the disappearance of the TMutator generic + ref plumbing. This
// benchmark stays as the regression probe for that trade.
//
// The comparison is the per-row work QuerySet.ExecuteUpdate performs:
//
//     original = storage[offset];
//     mutator.Update(original, mutator.WithSamePrimaryKey(original, newRow));
//
// Loop_StructMutator and Loop_ClassMutator are byte-identical generic code
// over `where TMut : IRowMutator<BenchRow>`; only the type kind differs.
// The JIT specializes value types (constrained, often inlined - today's
// shape) and shares reference types behind interface dispatch (the proposed
// class shape). Identical bodies means the delta is dispatch, nothing else.
//
// Loop_Direct is the same body calling static non-virtual methods - the
// zero-dispatch floor both arms are measured against.
//
// Mutator work is realistic for the staging model: the generated Update
// appends a Change to the operation's change list (index maintenance happens
// later, in Apply) and WithSamePrimaryKey is a record copy - so per row:
// one row copy + one staged append, through two dispatch-or-direct calls.
[MemoryDiagnoser]
public class MutatorDispatchBenchmarks {
    private const int RowCount = 100;

    private BenchStorage structStorage = null!;
    private BenchStorage classStorage = null!;
    private BenchStorage directStorage = null!;
    private StructBenchMutator structMutator;
    private ClassBenchMutator classMutator = null!;

    [GlobalSetup]
    public void Setup() {
        structStorage = new BenchStorage(RowCount).Seed();
        classStorage = new BenchStorage(RowCount).Seed();
        directStorage = new BenchStorage(RowCount).Seed();
        structMutator = new StructBenchMutator(structStorage);
        classMutator = new ClassBenchMutator(classStorage);
    }

    // Identical loop, struct arm: JIT-constrained dispatch (today's model).
    [Benchmark]
    public int Loop_StructMutator() {
        return ExecuteLoop(structMutator, structStorage);
    }

    // Identical loop, class arm: interface dispatch (the proposed model).
    [Benchmark]
    public int Loop_ClassMutator() {
        return ExecuteLoop(classMutator, classStorage);
    }

    // Same body, static non-virtual calls: the no-dispatch floor.
    [Benchmark]
    public int Loop_Direct() {
        directStorage.Reset();
        var newRow = new BenchRow(0, 1);
        for (var i = 0; i < RowCount; i++) {
            var original = directStorage.Read(i);
            directStorage.Stage(original.Id, DirectBench.WithSamePrimaryKey(original, newRow));
        }
        return directStorage.ChangeCount;
    }

    private static int ExecuteLoop<TMutator>(TMutator mutator, BenchStorage storage)
        where TMutator : IRowMutator<BenchRow> {
        storage.Reset();
        var newRow = new BenchRow(0, 1);
        for (var i = 0; i < RowCount; i++) {
            var original = storage.Read(i);
            mutator.Update(original, mutator.WithSamePrimaryKey(original, newRow));
        }
        return storage.ChangeCount;
    }
}

// ---- benchmark-local row, storage, and the three binding styles ----

public readonly record struct BenchRow(int Id, int Value);

public static class DirectBench {
    public static BenchRow WithSamePrimaryKey(BenchRow original, BenchRow newRow) => newRow with { Id = original.Id };
}

// Both mutator kinds implement the same real IRowMutator<BenchRow> with
// byte-identical bodies - struct (today) vs class (proposed) is the ONLY
// difference between the two arms.
public struct StructBenchMutator(BenchStorage storage) : IRowMutator<BenchRow> {
    public void Update(BenchRow original, BenchRow newRow) => storage.Stage(original.Id, newRow);
    public void Delete(BenchRow row) => storage.StageDelete(row.Id);
    public BenchRow WithSamePrimaryKey(BenchRow original, BenchRow newRow) => newRow with { Id = original.Id };
}

public sealed class ClassBenchMutator(BenchStorage storage) : IRowMutator<BenchRow> {
    public void Update(BenchRow original, BenchRow newRow) => storage.Stage(original.Id, newRow);
    public void Delete(BenchRow row) => storage.StageDelete(row.Id);
    public BenchRow WithSamePrimaryKey(BenchRow original, BenchRow newRow) => newRow with { Id = original.Id };
}

// Staging stand-in with the same shape as the generated Ops.Update: append a
// change (index maintenance happens later, in Apply). Reset() at loop entry
// keeps capacity, so steady-state allocations stay at zero for every arm.
public sealed class BenchStorage(int rowCount) {
    private readonly BenchRow[] rows = new BenchRow[rowCount];
    private readonly List<(int Key, BenchRow Row)> changes = new(rowCount);

    public int ChangeCount => changes.Count;

    public BenchStorage Seed() {
        for (var i = 0; i < rows.Length; i++) rows[i] = new BenchRow(i, i);
        return this;
    }

    public BenchRow Read(int offset) => rows[offset];
    public void Stage(int key, BenchRow row) => changes.Add((key, row));
    public void StageDelete(int key) => changes.Add((key, default));
    public void Reset() => changes.Clear();
}
