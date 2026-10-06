namespace RhinoDB.Core.CustomTypes;

static public class CustomTypeCodec<T> where T : struct {
    static private Func<T, byte[]>? serialize;
    static private Func<byte[], T>? deserialize;

    static public bool IsRegistered => serialize is not null;
    // ReSharper disable once StaticMemberInGenericType
    static public uint TypeHash { get; private set; }

    static public void Register(uint typeHash, Func<T, byte[]> serializeRow, Func<byte[], T> deserializeRow) {
        TypeHash = typeHash;
        deserialize = deserializeRow;
        serialize = serializeRow;
    }

    static public byte[] Serialize(T value) => serialize!(value);

    static public T Deserialize(byte[] bytes) => deserialize!(bytes);
}
