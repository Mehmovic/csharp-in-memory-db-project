namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class PrefCacheDurationInvalidException(Exception? inner = null)
    : Exception("A prefs cache duration must be positive, TimeSpan.Zero (never cached) or Timeout.InfiniteTimeSpan (never evicted).", inner);
