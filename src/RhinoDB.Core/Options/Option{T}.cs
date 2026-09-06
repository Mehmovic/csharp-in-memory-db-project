namespace RhinoDB.Core;

public readonly struct Option<T> {
    private readonly T value;

    private readonly bool isNone;

    private Option(T value, bool isNone) {
        this.value = value;
        this.isNone = isNone;
    }

    static public Option<T> Some(T value) {
        return value is null
            ? throw new ArgumentNullException(nameof(value))
            : new Option<T>(value, false);
    }
    
    static public Option<T> None() => new Option<T>(default!, true);

    public bool IsSome() => !isNone;
    public bool IsNone() => isNone;

    public T Get() {
        return !isNone ? value : throw new InvalidOperationException("Option has no value.");
    }

    public bool TryGet(out T result) {
        result = !isNone ? value : default!;
        return !isNone;
    }

    public T OrElse(T orValue) {
        return !isNone ? value : orValue;
    }

    public TResult Match<TResult>(Func<T, TResult> onSome, Func<TResult> onNone) =>
        !isNone ? onSome(value) : onNone();

    static public implicit operator Option<T>(T value) => Some(value);

    static public implicit operator Option<T>(Option _) => None();
}
