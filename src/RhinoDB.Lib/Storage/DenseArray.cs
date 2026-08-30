namespace RhinoDB.Lib.Storage;

public class DenseArray<T> where T: struct {
    private readonly List<T> values = [];

    public int Count { get; private set; } = 0;
    
    public int Insert(T item) {
        var index = Count;
        values.Add(item);
        Count += 1;
        return index;
    }

    public T Get(int index) {
        return values[index];
    }

    public void Delete(int index) {
        var lastIndex = Count - 1;
        
        if (index == lastIndex) {
            values.RemoveAt(index);
            Count -= 1;
            return;
        }
        
        T lastItem = values[lastIndex];
        values[index] = lastItem;
        values.RemoveAt(lastIndex);
        Count -= 1;
    }
}
