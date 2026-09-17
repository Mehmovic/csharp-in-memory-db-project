namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class OffsetListDisposedException()
    : Exception("OffsetList is disposed");