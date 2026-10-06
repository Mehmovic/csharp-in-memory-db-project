namespace RhinoDB.Lib.Prefs;

public sealed class RhinoPrefsBatch {
    private readonly RhinoPrefs prefs;
    private readonly List<PrefOp> ops = [];

    internal RhinoPrefsBatch(RhinoPrefs prefs) => this.prefs = prefs;

    public RhinoPrefsBatch SetString(string key, string value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetString(key, value, cacheFor));

    public RhinoPrefsBatch SetByte(string key, byte value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetValue(key, PrefKind.Byte, value, cacheFor));

    public RhinoPrefsBatch SetSByte(string key, sbyte value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetValue(key, PrefKind.SByte, value, cacheFor));

    public RhinoPrefsBatch SetInt16(string key, short value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetValue(key, PrefKind.Int16, value, cacheFor));

    public RhinoPrefsBatch SetUInt16(string key, ushort value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetValue(key, PrefKind.UInt16, value, cacheFor));

    public RhinoPrefsBatch SetInt32(string key, int value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetValue(key, PrefKind.Int32, value, cacheFor));

    public RhinoPrefsBatch SetUInt32(string key, uint value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetValue(key, PrefKind.UInt32, value, cacheFor));

    public RhinoPrefsBatch SetInt64(string key, long value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetValue(key, PrefKind.Int64, value, cacheFor));

    public RhinoPrefsBatch SetUInt64(string key, ulong value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetValue(key, PrefKind.UInt64, value, cacheFor));

    public RhinoPrefsBatch SetSingle(string key, float value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetValue(key, PrefKind.Single, value, cacheFor));

    public RhinoPrefsBatch SetDouble(string key, double value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetValue(key, PrefKind.Double, value, cacheFor));

    public RhinoPrefsBatch SetDecimal(string key, decimal value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetValue(key, PrefKind.Decimal, value, cacheFor));

    public RhinoPrefsBatch SetChar(string key, char value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetValue(key, PrefKind.Char, value, cacheFor));

    public RhinoPrefsBatch SetBool(string key, bool value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetValue(key, PrefKind.Bool, value, cacheFor));

    public RhinoPrefsBatch SetBytes(string key, byte[] value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetBytes(key, value, cacheFor));

    public RhinoPrefsBatch SetByteList(string key, List<byte> value, TimeSpan? cacheFor = null) => Add(RhinoPrefs.SetByteList(key, value, cacheFor));

    public RhinoPrefsBatch Set<T>(string key, T value, TimeSpan? cacheFor = null) where T : struct => Add(RhinoPrefs.Set(key, value, cacheFor));

    public RhinoPrefsBatch Delete(string key) => Add(RhinoPrefs.Delete(key));

    public RhinoPrefsBatch DeleteAll() => Add(PrefOp.ClearAll);

    public Task<Result> CommitAsync() => prefs.WriteAsync(ops.ToArray());

    private RhinoPrefsBatch Add(PrefOp op) {
        ops.Add(op);
        return this;
    }
}
