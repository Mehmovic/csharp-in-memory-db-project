namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class RingBufferGapException()
    : Exception("Change ring buffer history has a gap - the requested LSN is older than the retained window");
