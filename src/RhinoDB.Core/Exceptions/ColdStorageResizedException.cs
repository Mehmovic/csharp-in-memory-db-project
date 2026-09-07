namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ColdStorageResizedException() : Exception("Cold storage map was resized and could not be extended for this process");
