using System.Globalization;

using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Hosting;

namespace RhinoDB.Tools.Dev;

static public class DevPruneCommand {
    static public int Run(string[] args) {
        var coldPaths = ColdPathArg.Resolve(args, out var resolveError);
        if (resolveError is not null) {
            Console.Error.WriteLine(resolveError);
            return 1;
        }
        if (coldPaths.Count == 0) {
            Console.Error.WriteLine("rhinodb dev prune: --cold-path <dir> is required (repeatable).");
            return 1;
        }

        var olderThanText = ColdPathArg.ValueAfter(args, "--older-than", out var hasOlderThan);
        var keepGenerationsText = ColdPathArg.ValueAfter(args, "--keep-generations", out var hasKeepGenerations);

        if (hasOlderThan == hasKeepGenerations) {
            Console.Error.WriteLine(
                "rhinodb dev prune: give exactly one of --older-than <timestamp> or --keep-generations <n>. " + "Two retention policies at once is ambiguous, not a merge."
            );
            return 1;
        }

        long? cutoffUtcTicks = null;
        if (hasOlderThan) {
            var resolved = RhinoHostOptions.ResolveUtcTicks(olderThanText, "--older-than");
            if (resolved.IsError()) {
                Console.Error.WriteLine(resolved.GetError().ToException().Message);
                return 1;
            }
            cutoffUtcTicks = resolved.Unwrap();
        }

        int? keepGenerations = null;
        if (hasKeepGenerations) {
            if (!int.TryParse(keepGenerationsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) {
                Console.Error.WriteLine($"--keep-generations must be an integer, got '{keepGenerationsText}'.");
                return 1;
            }
            keepGenerations = parsed;
        }

        var confirmed = args.Contains("--yes");
        var totalWouldDelete = 0;

        foreach (var coldPath in coldPaths) {
            if (!Directory.Exists(coldPath)) {
                Console.Error.WriteLine($"No such cold-storage directory: {coldPath}");
                return 1;
            }

            var cold = ColdStore.Open(coldPath).UnwrapOr(null!);
            if (cold is null) {
                Console.Error.WriteLine($"Could not open {coldPath} for pruning.");
                return 1;
            }

            using (cold) {
                Console.WriteLine(coldPath);
                if (cutoffUtcTicks is { } cutoff) {
                    var local = new DateTimeOffset(cutoff, TimeSpan.Zero).ToLocalTime();
                    Console.WriteLine($"  keep everything newer than {Format(local)}  (local)  =  {Format(new DateTimeOffset(cutoff, TimeSpan.Zero))}  (UTC)");
                } else {
                    Console.WriteLine($"  keep generations {keepGenerations} and newer");
                }

                var described = cold.DescribeArchiveSegments();
                if (described.IsError()) {
                    Console.Error.WriteLine(described.GetError().ToException().Message);
                    return 1;
                }

                var segments = described.Unwrap();
                if (segments.Count == 0) {
                    Console.WriteLine("  (no archived segments)");
                    Console.WriteLine();
                    continue;
                }

                var doomed = new List<WalSegmentDescription>();
                foreach (var segment in segments) {
                    var goes = cutoffUtcTicks is { } c
                        ? segment.IsWhollyOlderThan(c)
                        : (int)segment.Generation < keepGenerations!.Value;
                    if (goes) doomed.Add(segment);

                    Console.WriteLine($"    {segment.FileName}  gen {segment.Generation,3}  {segment.EntryCount,5} entries  {DescribeRange(segment),-40}  {(goes ? "DELETE" : "keep")}");
                }

                totalWouldDelete += doomed.Count;
                Console.WriteLine();
            }
        }

        if (totalWouldDelete == 0) {
            Console.WriteLine("Nothing to prune - no archived segment matches the policy.");
            return 0;
        }

        if (!confirmed) {
            Console.WriteLine();
            Console.WriteLine("Dry run only - nothing was deleted. Re-run with --yes to actually prune.");
            return 1;
        }

        var deleted = 0;
        foreach (var coldPath in coldPaths) {
            var cold = ColdStore.Open(coldPath).UnwrapOr(null!);
            if (cold is null) return 1;

            using (cold) {
                var result = cutoffUtcTicks is { } cutoff
                    ? cold.PruneArchiveOlderThan(cutoff)
                    : cold.PruneArchiveOlderThanGeneration(FloorFromKeepCount(cold, keepGenerations!.Value));
                if (result.IsError()) {
                    Console.Error.WriteLine(result.GetError().ToException().Message);
                    return 1;
                }
                deleted += result.Unwrap();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Done - {deleted} archived segment(s) removed. The retention floor has advanced.");
        return 0;
    }

    static private int FloorFromKeepCount(ColdStore cold, int keepCount) {
        var oldest = cold.DescribeArchiveSegments().UnwrapOr([]);
        if (oldest.Count == 0) return 0;

        var newest = (int)oldest.Max(s => s.Generation);
        return Math.Max(0, newest - keepCount + 1);
    }

    static private string DescribeRange(WalSegmentDescription segment) {
        if (segment is { OldestUtcTicks: 0, NewestUtcTicks: 0 }) return "(age unknown - unstamped entries)";
        return $"{Format(FromTicks(segment.OldestUtcTicks))} .. {Format(FromTicks(segment.NewestUtcTicks))}";
    }

    static private DateTimeOffset FromTicks(long utcTicks) => new DateTimeOffset(utcTicks, TimeSpan.Zero);

    static private string Format(DateTimeOffset value) => value.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
