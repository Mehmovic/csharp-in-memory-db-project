namespace RhinoDB.Native.Interop;

static internal unsafe class MdbxValMarshal {
    static public int Get(nint txn, uint dbi, ReadOnlySpan<byte> key, out byte[] value) {
        fixed (byte* keyPtr = key) {
            var keyVal = new MdbxVal { Data = (nint)keyPtr, Length = (nuint)key.Length };
            var rc = MdbxNative.mdbx_get(txn, dbi, in keyVal, out MdbxVal dataVal);
            if (rc != 0) {
                value = [];
                return rc;
            }

            value = new byte[dataVal.Length];
            new ReadOnlySpan<byte>((void*)dataVal.Data, (int)dataVal.Length).CopyTo(value);
            return rc;
        }
    }

    static public int Put(nint txn, uint dbi, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, uint flags) {
        fixed (byte* keyPtr = key)
        fixed (byte* valuePtr = value) {
            var keyVal = new MdbxVal { Data = (nint)keyPtr, Length = (nuint)key.Length };
            var valueVal = new MdbxVal { Data = (nint)valuePtr, Length = (nuint)value.Length };
            return MdbxNative.mdbx_put(txn, dbi, in keyVal, ref valueVal, flags);
        }
    }

    static public int Del(nint txn, uint dbi, ReadOnlySpan<byte> key) {
        fixed (byte* keyPtr = key) {
            var keyVal = new MdbxVal { Data = (nint)keyPtr, Length = (nuint)key.Length };
            return MdbxNative.mdbx_del(txn, dbi, in keyVal, 0);
        }
    }
}
