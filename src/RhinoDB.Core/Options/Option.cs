namespace RhinoDB.Core.Options;

public readonly struct Option {
    static public Option None() => default;

    static public Option<T> Some<T>(T value) => Option<T>.Some(value);
}
