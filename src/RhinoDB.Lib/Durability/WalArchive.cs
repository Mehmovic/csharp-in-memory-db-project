using RhinoDB.Native;

namespace RhinoDB.Lib.Durability;

static public class WalArchive {
    public const string ArchiveDirectoryName = "wal-archive";
    private const string SegmentExtension = ".wal";
    private const int SequenceWidth = 8;

    static public DbError? WriteSegment(string archiveDirectory, Guid databaseId, DecodedWalEntry[] entries) {
        if (entries.Length == 0) return null;

        try {
            Directory.CreateDirectory(archiveDirectory);
        } catch (Exception ex) {
            return DbError.SystemFailure(ex);
        }

        var segmentPath = Path.Combine(archiveDirectory, NextSegmentFileName(archiveDirectory));

        try {
            using var fileStream = new FileStream(segmentPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var header = WalFileHeaderCodec.Encode(databaseId);
            fileStream.Write(header, 0, header.Length);

            foreach (var entry in entries) {
                var frame = WalRecordCodec.Encode(entry.Lsn, entry.Kind, entry.Changes);
                fileStream.Write(frame, 0, frame.Length);
            }

            fileStream.Flush(flushToDisk: true);
        } catch (Exception ex) {
            return DbError.SystemFailure(ex);
        }

        return DirectorySync.TrySync(archiveDirectory, out _) ? null : DbError.WalDirectorySyncFailed();
    }

    static public Result<List<DecodedWalEntry>> ReadHistory(string coldStorePath, IReadOnlyList<DecodedWalEntry> liveTail) {
        var merged = new List<DecodedWalEntry>();
        var lastSeenLsn = 0L;
        var started = false;

        var archiveDirectory = Path.Combine(coldStorePath, ArchiveDirectoryName);
        if (Directory.Exists(archiveDirectory)) {
            var segmentPaths = Directory.EnumerateFiles(archiveDirectory, $"*{SegmentExtension}")
                .OrderBy(p => p, StringComparer.Ordinal);

            foreach (var segmentPath in segmentPaths) {
                var readResult = ReadSegment(segmentPath);
                if (readResult.IsError()) return readResult.Void();

                MergeInOrder(merged, readResult.Unwrap(), ref lastSeenLsn, ref started);
            }
        }

        var liveOperationEntries = liveTail.Where(e => e.Kind == WalEntryKind.Operation).ToArray();
        MergeInOrder(merged, liveOperationEntries, ref lastSeenLsn, ref started);

        return merged;
    }

    static private Result<DecodedWalEntry[]> ReadSegment(string path) {
        byte[] bytes;
        try {
            bytes = File.ReadAllBytes(path);
        } catch (Exception ex) {
            return Result<DecodedWalEntry[]>.Error(DbError.SystemFailure(ex));
        }

        if (bytes.Length < WalFileHeaderCodec.Size || !WalFileHeaderCodec.TryDecode(bytes, out _))
            return Result<DecodedWalEntry[]>.Error(DbError.WalCorrupted());

        var scan = WalRecordCodec.Scan(bytes.AsSpan(WalFileHeaderCodec.Size));
        if (scan.Status != WalScanStatus.Clean) return Result<DecodedWalEntry[]>.Error(DbError.WalCorrupted());

        return scan.Entries.Where(e => e.Kind == WalEntryKind.Operation).ToArray();
    }

    static private void MergeInOrder(List<DecodedWalEntry> into, DecodedWalEntry[] batch, ref long lastSeenLsn, ref bool started) {
        foreach (var entry in batch) {
            if (started && entry.Lsn <= lastSeenLsn) continue;

            into.Add(entry);
            lastSeenLsn = entry.Lsn;
            started = true;
        }
    }

    static private string NextSegmentFileName(string archiveDirectory) {
        var maxSequence = 0;
        foreach (var path in Directory.EnumerateFiles(archiveDirectory, $"*{SegmentExtension}")) {
            var name = Path.GetFileNameWithoutExtension(path);
            if (int.TryParse(name, out var sequence) && sequence > maxSequence) maxSequence = sequence;
        }

        return $"{(maxSequence + 1).ToString($"D{SequenceWidth}")}{SegmentExtension}";
    }
}
