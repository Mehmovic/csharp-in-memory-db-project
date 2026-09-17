namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class OffsetWriterDisposedException()
    : Exception("OffsetWriter is disposed");