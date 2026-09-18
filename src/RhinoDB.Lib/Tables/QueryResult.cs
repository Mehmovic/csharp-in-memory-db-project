using System.Buffers;

using RhinoDB.Lib.Storage;

namespace RhinoDB.Lib.Tables;

public ref struct QueryResultSet<TRow, TMutator>
(
    DenseArray<TRow> storage,
    OffsetList offsets,
    ref TMutator mutator
) : IDisposable
    where TRow : struct
    where TMutator : struct, IRowMutator<TRow> {
    private OffsetList offsets = offsets;
    private readonly ref TMutator mutator = ref mutator;
    private TRow[] buffer = offsets.Count > 0 ? ArrayPool<TRow>.Shared.Rent(offsets.Count) : [];

    public int Count => offsets.Count;

    public StackResult<ReadOnlySpan<TRow>> Get() {
        if (IsDisposed()) return StackResult.Error(DbError.QueryResultSetDisposed());

        var i = 0;
        foreach (var offset in offsets) buffer[i++] = storage.Get(offset);

        return StackResult<ReadOnlySpan<TRow>>.Ok(buffer.AsSpan(0, offsets.Count));
    }

    public Result<int> Update(TRow newRow) {
        if (IsDisposed()) return Result.Error(DbError.QueryResultSetDisposed());

        var affectedCount = 0;
        foreach (var offset in offsets) {
            var original = storage.Get(offset);
            mutator.Update(original, mutator.WithSamePrimaryKey(original, newRow));
            affectedCount++;
        }
        return affectedCount;
    }

    public Result<int> Update(Func<TRow, TRow> mutate) {
        if (IsDisposed()) return Result.Error(DbError.QueryResultSetDisposed());

        var affectedCount = 0;
        foreach (var offset in offsets) {
            var original = storage.Get(offset);
            mutator.Update(original, mutate(original));
            affectedCount++;
        }
        return affectedCount;
    }

    public Result<int> Delete() {
        if (IsDisposed()) return Result.Error(DbError.QueryResultSetDisposed());

        var affectedCount = 0;
        foreach (var offset in offsets) {
            mutator.Delete(storage.Get(offset));
            affectedCount++;
        }
        return affectedCount;
    }

    public void Dispose() {
        if (IsDisposed()) return;

        if (buffer.Length > 0) ArrayPool<TRow>.Shared.Return(buffer);
        buffer = null!;

        offsets.Dispose();
    }

    private readonly bool IsDisposed() => buffer == null;
}

public readonly ref struct QuerySingle<TRow, TMutator>(DenseArray<TRow> storage, Result<int> offsetResult, ref TMutator mutator)
    where TRow : struct
    where TMutator : struct, IRowMutator<TRow> {
    private readonly ref TMutator mutator = ref mutator;

    public Result<TRow> Get() => offsetResult.IsError() ? offsetResult.Void() : storage.Get(offsetResult.Unwrap());

    public Result<int> Update(TRow newRow) {
        if (offsetResult.IsError()) return offsetResult.Void();

        var original = storage.Get(offsetResult.Unwrap());
        mutator.Update(original, mutator.WithSamePrimaryKey(original, newRow));
        return 1;
    }

    public Result<int> Update(Func<TRow, TRow> mutate) {
        if (offsetResult.IsError()) return offsetResult.Void();

        var original = storage.Get(offsetResult.Unwrap());
        mutator.Update(original, mutate(original));
        return 1;
    }

    public Result<int> Delete() {
        if (offsetResult.IsError()) return offsetResult.Void();

        mutator.Delete(storage.Get(offsetResult.Unwrap()));
        return 1;
    }
}
