namespace RhinoDB.Core;

public readonly struct FilterDescriptor<T> where T : IEquatable<T> {
    private readonly bool include;
    private readonly T key;

    internal FilterDescriptor(bool include, T key) {
        this.include = include;
        this.key = key;
    }

    public bool IsInclude => include;
    public T Key => key;

    public bool MustExclude(T entryKey) {
        var match = EqualityComparer<T>.Default.Equals(entryKey, key);
        return match != include;
    }
    
    public bool MustInclude(T entryKey) {
        var match = EqualityComparer<T>.Default.Equals(entryKey, key);
        return match == include;
    }

    static public FilterDescriptor<T> Include(T key) => new FilterDescriptor<T>(true, key);
    static public FilterDescriptor<T> Exclude(T key) => new FilterDescriptor<T>(false, key);
}

static public class FilterDescriptor {
    static public FilterDescriptor<T> Include<T>(T key) where T : IEquatable<T> => new FilterDescriptor<T>(true, key);
    static public FilterDescriptor<T> Exclude<T>(T key) where T : IEquatable<T> => new FilterDescriptor<T>(false, key);
}
