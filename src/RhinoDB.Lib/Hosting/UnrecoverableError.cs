namespace RhinoDB.Lib.Hosting;

public sealed record UnrecoverableError(string Database, DbError Error, int ExitCode);

public sealed class UnrecoverableErrorPolicy {
    private UnrecoverableErrorPolicy(Action<UnrecoverableError>? handler) => Handler = handler;

    internal Action<UnrecoverableError>? Handler { get; }

    // The default: print one line to stderr, best-effort shutdown, exit with UnrecoverableExitCodes.For(error).
    static public UnrecoverableErrorPolicy ExitProcess { get; } = new UnrecoverableErrorPolicy(null);

    // For tests and embedders that must not die: the process keeps running with the database poisoned.
    static public UnrecoverableErrorPolicy Callback(Action<UnrecoverableError> handler) => new UnrecoverableErrorPolicy(handler);
}

static public class UnrecoverableExitCodes {
    // sysexits.h: EX_SOFTWARE / EX_IOERR. Never 0 - a supervisor must see a failure.
    public const int Software = 70;
    public const int IoError = 74;

    static public int For(DbError error) => error.Kind switch {
        ErrorKind.WalDurabilityFailed
            or ErrorKind.WalDirectorySyncFailed
            or ErrorKind.ColdStorageDirectorySyncFailed
            or ErrorKind.MultiTxOutcomeUnknown
            => IoError,
        _ => Software,
    };
}

// Runs once per process, off whatever thread poisoned the database (a loop or a WAL flusher - neither may block).
internal sealed class UnrecoverableErrorHandler {
    static private readonly TimeSpan DefaultShutdownBudget = TimeSpan.FromSeconds(5);
    static private readonly TimeSpan DefaultExitWatchdog = TimeSpan.FromSeconds(7);

    private readonly UnrecoverableErrorPolicy policy;
    private readonly Action<int> exit;
    private readonly Action<string> failFast;
    private readonly TextWriter? stderr;
    private readonly TimeSpan shutdownBudget;
    private readonly TimeSpan exitWatchdog;
    private readonly TaskCompletionSource handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private int triggered;

    internal UnrecoverableErrorHandler(
        UnrecoverableErrorPolicy policy,
        Action<int>? exit = null,
        Action<string>? failFast = null,
        TextWriter? stderr = null,
        TimeSpan? shutdownBudget = null,
        TimeSpan? exitWatchdog = null
    ) {
        this.policy = policy;
        this.exit = exit ?? Environment.Exit;
        this.failFast = failFast ?? Environment.FailFast;
        this.stderr = stderr;
        this.shutdownBudget = shutdownBudget ?? DefaultShutdownBudget;
        this.exitWatchdog = exitWatchdog ?? DefaultExitWatchdog;
    }

    // Set by the host once it exists: stop accepting clients, push the healthy databases' WALs to disk.
    internal Func<Task>? Shutdown { get; set; }

    internal Task Handled => handled.Task;

    public void Trigger(string database, DbError error) {
        if (Interlocked.Exchange(ref triggered, 1) == 1) return;
        var info = new UnrecoverableError(database, error, UnrecoverableExitCodes.For(error));
        new Thread(() => Run(info)) { IsBackground = true, Name = "RhinoDB unrecoverable error" }.Start();
    }

    private void Run(UnrecoverableError info) {
        try {
            if (policy.Handler is { } handler) {
                try { handler(info); } catch { /* a failing callback must not take the engine down with it */ }
                return;
            }
            ExitProcess(info);
        } finally {
            handled.TrySetResult();
        }
    }

    private void ExitProcess(UnrecoverableError info) {
        var message = $"RhinoDB: unrecoverable error in {info.Database}: {info.Error.Kind} - {info.Error.ToException().Message} "
                      + $"Exiting with {info.ExitCode} so the supervisor restarts the engine.";
        try {
            var writer = stderr ?? Console.Error;
            writer.WriteLine(message);
            writer.Flush();
        } catch { /* nowhere left to report to */ }

        try { Shutdown?.Invoke().Wait(shutdownBudget); } catch { /* best effort - the exit must happen regardless */ }

        var exited = new ManualResetEventSlim(false);
        var watchdog = new Thread(() => {
                if (!exited.Wait(exitWatchdog)) failFast(message);
            }
        ) { IsBackground = true, Name = "RhinoDB exit watchdog" };
        watchdog.Start();
        try {
            exit(info.ExitCode);
        } catch {
            watchdog.Join();
            return;
        }
        exited.Set();
        watchdog.Join();
    }
}
