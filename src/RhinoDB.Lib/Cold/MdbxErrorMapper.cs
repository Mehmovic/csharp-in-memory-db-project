using RhinoDB.Native;

namespace RhinoDB.Lib.Cold;

static internal class MdbxErrorMapper {
    private const int MdbxKeyExist = -30799;
    private const int MdbxNotFound = -30798;
    private const int MdbxMapFull = -30792;

    private const int MdbxMapResized = -30785;

    static internal DbError Map(int code) => code switch {
        MdbxNotFound => DbError.IndexKeyNotFound(),
        MdbxKeyExist => DbError.DuplicateKey(),
        MdbxMapFull => DbError.ColdStorageFull(),
        MdbxMapResized => DbError.ColdStorageResized(),
        _ => DbError.SystemFailure(new MdbxNativeException(code, MdbxEnvironment.StrError(code))),
    };
}
