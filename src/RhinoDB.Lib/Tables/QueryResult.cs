using System.Buffers;

using RhinoDB.Lib.Storage;

namespace RhinoDB.Lib.Tables;

public ref struct QuerySet<TRow, TMutator>
(
    DenseArray<TRow> storage,
    StackArrayPoolContainer<int> offsets,
    ref TMutator mutator
) : IDisposable
    where TRow : struct
    where TMutator : struct, IRowMutator<TRow> {
    private StackArrayPoolContainer<int> offsets = offsets;
    private readonly ref TMutator mutator = ref mutator;
    private TRow[] buffer = offsets.Count > 0 ? ArrayPool<TRow>.Shared.Rent(offsets.Count) : [];

    public int Count => offsets.Count;

    public StackResult<ReadOnlySpan<TRow>> Get() {
        if (IsDisposed()) return StackResult.Error(DbError.QuerySetDisposed());

        var i = 0;
        foreach (var offset in offsets) buffer[i++] = storage.Get(offset);

        return StackResult<ReadOnlySpan<TRow>>.Ok(buffer.AsSpan(0, offsets.Count));
    }

    public readonly StackResult<StorageOffsetRefEnumerator<TRow>> GetRefEnumerator() {
        return IsDisposed()
            ? StackResult.Error(DbError.QuerySetDisposed())
            : StackResult<StorageOffsetRefEnumerator<TRow>>.Ok(
                new StorageOffsetRefEnumerator<TRow>(storage, offsets.Buffer())
            );
    }

    public Result ExecuteUpdate(TRow newRow) {
        if (IsDisposed()) return Result.Error(DbError.QuerySetDisposed());

        foreach (var offset in offsets) {
            var original = storage.Get(offset);
            mutator.Update(original, mutator.WithSamePrimaryKey(original, newRow));
        }
        
        return Result.Ok();
    }

    public Result ExecuteUpdate(Func<TRow, TRow> mutate) {
        if (IsDisposed()) return Result.Error(DbError.QuerySetDisposed());

        foreach (var offset in offsets) {
            var original = storage.Get(offset);
            mutator.Update(original, mutate(original));
        }
        
        return Result.Ok();
    }

    public Result ExecuteDelete() {
        if (IsDisposed()) return Result.Error(DbError.QuerySetDisposed());

        foreach (var offset in offsets) {
            mutator.Delete(storage.Get(offset));
        }
        
        return Result.Ok();
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

    public bool HasRow() => offsetResult.IsOk();
    public ref readonly TRow GetRef() => ref storage.GetRef(offsetResult.Unwrap());

    public Result ExecuteUpdate(TRow newRow) {
        if (offsetResult.IsError()) return offsetResult.Void();

        var original = storage.Get(offsetResult.Unwrap());
        mutator.Update(original, mutator.WithSamePrimaryKey(original, newRow));
        return Result.Ok();
    }

    public Result ExecuteUpdate(Func<TRow, TRow> mutate) {
        if (offsetResult.IsError()) return offsetResult.Void();

        var original = storage.Get(offsetResult.Unwrap());
        mutator.Update(original, mutate(original));
        return Result.Ok();
    }

    public Result ExecuteDelete() {
        if (offsetResult.IsError()) return offsetResult.Void();

        mutator.Delete(storage.Get(offsetResult.Unwrap()));
        return Result.Ok();
    }
}
