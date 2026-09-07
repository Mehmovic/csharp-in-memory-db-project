using RhinoDB.Core;

namespace RhinoDB.Lib.Cold.Test;

// Table-driven against raw libmdbx status codes - no real mdbx interaction needed,
// MdbxErrorMapper.Map(int) is a pure function. Codes verified against the vendored
// src/RhinoDB.Native/vendor/libmdbx/mdbx.h rather than assumed (same discipline used
// for MdbxVal's field order in Part A) - MDBX_MAP_RESIZED in particular is worth
// flagging: it's deprecated in this mdbx version and aliased to
// MDBX_UNABLE_EXTEND_MAPSIZE (-30785), not a distinct enum value of its own.
public class MdbxErrorMapperTests {
    private const int MdbxKeyExist = -30799;
    private const int MdbxNotFound = -30798;
    private const int MdbxCorrupted = -30796;
    private const int MdbxPanic = -30795;
    private const int MdbxVersionMismatch = -30794;
    private const int MdbxMapFull = -30792;
    private const int MdbxMapResized = -30785; // == MDBX_UNABLE_EXTEND_MAPSIZE, see class comment
    private const int MdbxSomeUnrecognizedCode = -12345;

    [Test]
    public void Map_NotFound_ReturnsIndexKeyNotFound() {
        var error = MdbxErrorMapper.Map(MdbxNotFound);
        Assert.That(error.Kind, Is.EqualTo(ErrorKind.IndexKeyNotFound));
    }

    [Test]
    public void Map_KeyExist_ReturnsDuplicateKey() {
        var error = MdbxErrorMapper.Map(MdbxKeyExist);
        Assert.That(error.Kind, Is.EqualTo(ErrorKind.DuplicateKey));
    }

    [Test]
    public void Map_MapFull_ReturnsColdStorageFull() {
        var error = MdbxErrorMapper.Map(MdbxMapFull);
        Assert.That(error.Kind, Is.EqualTo(ErrorKind.ColdStorageFull));
    }

    [Test]
    public void Map_MapResized_ReturnsColdStorageResized() {
        var error = MdbxErrorMapper.Map(MdbxMapResized);
        Assert.That(error.Kind, Is.EqualTo(ErrorKind.ColdStorageResized));
    }

    [TestCase(-30796)] // MDBX_CORRUPTED
    [TestCase(-30795)] // MDBX_PANIC
    [TestCase(-30794)] // MDBX_VERSION_MISMATCH
    [TestCase(-12345)] // anything unrecognized
    public void Map_UnrecognizedOrFatalCodes_ReturnSystemFailureCarryingAnMdbxNativeException(int code) {
        var error = MdbxErrorMapper.Map(code);

        Assert.That(error.Kind, Is.EqualTo(ErrorKind.SystemFailure));
        var ex = error.ToException();
        Assert.That(ex, Is.InstanceOf<MdbxNativeException>());
        Assert.That(((MdbxNativeException)ex).Code, Is.EqualTo(code));
    }

    [Test]
    public void Map_Corrupted_MessageComesFromMdbxStrError() {
        // MdbxNativeException's message is whatever mdbx_strerror(code) reports,
        // not a hand-written string - proves the mapper actually calls it rather
        // than fabricating a generic message.
        var error = MdbxErrorMapper.Map(MdbxCorrupted);

        Assert.That(error.ToException().Message, Is.EqualTo(RhinoDB.Native.MdbxEnvironment.StrError(MdbxCorrupted)));
    }
}
