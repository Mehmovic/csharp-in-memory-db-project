namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ArrayPoolDisposedException()
    : Exception("The container holding the ArrayPool is disposed, so the rented array.");