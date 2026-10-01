using RhinoDB.Lib.Tables;
using RhinoDB.Native;

namespace RhinoDB.Lib.Durability;

static public class WalArchive {
    public const string ArchiveDirectoryName = "wal-archive";
    private const string SegmentExtension = ".wal";
    private const int SequenceWidth = 8;

    static public DbError? WriteSegment(string archiveDirectory, Guid databaseId, DecodedWalEntry[] entries, uint generation) {
        if (entries.Length == 0) return null;

        try {
            Directory.CreateDirectory(archiveDirectory);
        } catch (Exception ex) {
            return DbError.SystemFailure(ex);
        }

        var segmentPath = Path.Combine(archiveDirectory, NextSegmentFileName(archiveDirectory));

        try {
            using var fileStream = new FileStream(segmentPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var header = WalFileHeaderCodec.Encode(databaseId, generation);
            fileStream.Write(header, 0, header.Length);

            foreach (var entry in entries) {
                var frame = WalRecordCodec.Encode(entry.Lsn, entry.Kind, entry.Changes, entry.UtcTicks);
                fileStream.Write(frame, 0, frame.Length);
            }

            fileStream.Flush(flushToDisk: true);
        } catch (Exception ex) {
            TryDeletePartialSegment(segmentPath);
            return DbError.SystemFailure(ex);
        }

        return DirectorySync.TrySync(archiveDirectory, out _) ? null : DbError.WalDirectorySyncFailed();
    }

    static public Result<List<(DecodedWalEntry Entry, int Generation)>> ReadHistory(
        string coldStorePath, IReadOnlyList<DecodedWalEntry> liveTail, int liveGeneration
    ) {
        var merged = new List<(DecodedWalEntry, int)>();
        var lastSeenLsn = 0UL;
        var started = false;

        var archiveDirectory = Path.Combine(coldStorePath, ArchiveDirectoryName);
        if (Directory.Exists(archiveDirectory)) {
            var segmentPaths = Directory.EnumerateFiles(archiveDirectory, $"*{SegmentExtension}")
                .OrderBy(p => p, StringComparer.Ordinal);

            foreach (var segmentPath in segmentPaths) {
                var readResult = ReadSegment(segmentPath);
                if (readResult.IsError()) return readResult.Void();

                var (generation, entries) = readResult.Unwrap();
                MergeInOrder(merged, entries, (int)generation, ref lastSeenLsn, ref started);
            }
        }

        var liveOperationEntries = liveTail.Where(e => e.Kind == WalEntryKind.Operation).ToArray();
        MergeInOrder(merged, liveOperationEntries, liveGeneration, ref lastSeenLsn, ref started);

        return merged;
    }

    static public Result<Option<int>> ReadOldestRetainedGeneration(string coldStorePath) {
        var archiveDirectory = Path.Combine(coldStorePath, ArchiveDirectoryName);
        if (!Directory.Exists(archiveDirectory)) return Result<Option<int>>.Ok(Option<int>.None());

        int? oldest = null;
        foreach (var segmentPath in Directory.EnumerateFiles(archiveDirectory, $"*{SegmentExtension}")) {
            byte[] headerBytes;
            try {
                using var stream = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                headerBytes = new byte[WalFileHeaderCodec.Size];
                var read = stream.Read(headerBytes, 0, headerBytes.Length);
                if (read < headerBytes.Length) return Result<Option<int>>.Error(DbError.WalCorrupted());
            } catch (Exception ex) {
                return Result<Option<int>>.Error(DbError.SystemFailure(ex));
            }

            if (!WalFileHeaderCodec.TryDecode(headerBytes, out var header)) return Result<Option<int>>.Error(DbError.WalCorrupted());

            var generation = (int)header.Generation;
            if (oldest is null || generation < oldest) oldest = generation;
        }

        return Result<Option<int>>.Ok(oldest is { } value ? Option<int>.Some(value) : Option<int>.None());
    }

    static public Result<int> DeleteSegmentsOlderThan(string coldStorePath, int targetGeneration) {
        var archiveDirectory = Path.Combine(coldStorePath, ArchiveDirectoryName);
        if (!Directory.Exists(archiveDirectory)) return Result.Ok(0);

        var deleted = 0;
        var filesInOrder = Directory.EnumerateFiles(archiveDirectory, $"*{SegmentExtension}").OrderBy(p => p, StringComparer.Ordinal);
        foreach (var segmentPath in filesInOrder) {
            byte[] headerBytes;
            try {
                using var stream = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                headerBytes = new byte[WalFileHeaderCodec.Size];
                var read = stream.Read(headerBytes, 0, headerBytes.Length);
                if (read < headerBytes.Length) return Result<int>.Error(DbError.WalCorrupted());
            } catch (Exception ex) {
                return Result<int>.Error(DbError.SystemFailure(ex));
            }

            if (!WalFileHeaderCodec.TryDecode(headerBytes, out var header)) return Result<int>.Error(DbError.WalCorrupted());
            if ((int)header.Generation >= targetGeneration) break;

            try {
                File.Delete(segmentPath);
                deleted++;
            } catch (Exception ex) {
                return Result<int>.Error(DbError.SystemFailure(ex));
            }
        }

        return DirectorySync.TrySync(archiveDirectory, out _)
            ? Result.Ok(deleted)
            : Result<int>.Error(DbError.WalDirectorySyncFailed());
    }

    static public Result MigrateSegments(
        string coldStorePath,
        int targetGeneration,
        Func<uint, int, ChangeKind, byte[], byte[]?, (byte[] Key, byte[]? Row)?> transform
    ) {
        var archiveDirectory = Path.Combine(coldStorePath, ArchiveDirectoryName);
        if (!Directory.Exists(archiveDirectory)) return Result.Ok();

        foreach (var segmentPath in Directory.EnumerateFiles(archiveDirectory, $"*{SegmentExtension}").OrderBy(p => p, StringComparer.Ordinal).ToArray()) {
            byte[] originalBytes;
            try {
                originalBytes = File.ReadAllBytes(segmentPath);
            } catch (Exception ex) {
                return Result.Error(DbError.SystemFailure(ex));
            }

            if (originalBytes.Length < WalFileHeaderCodec.Size || !WalFileHeaderCodec.TryDecode(originalBytes, out var header))
                return Result.Error(DbError.WalCorrupted());

            if (header.Generation >= targetGeneration) continue;

            var scan = WalRecordCodec.Scan(originalBytes.AsSpan(WalFileHeaderCodec.Size));
            if (scan.Status != WalScanStatus.Clean) return Result.Error(DbError.WalCorrupted());

            var migratedEntries = new List<DecodedWalEntry>();
            foreach (var entry in scan.Entries) {
                if (entry.Kind != WalEntryKind.Operation) continue;

                var newChanges = new List<WalChange>();
                foreach (var change in entry.Changes) {
                    var transformed = transform(change.TableId, (int)header.Generation, change.Kind, change.Key, change.Row);
                    if (transformed is not { } result) continue;

                    newChanges.Add(new WalChange(change.TableId, change.Kind, result.Key, result.Row));
                }

                if (newChanges.Count > 0) migratedEntries.Add(new DecodedWalEntry(entry.Lsn, entry.Kind, newChanges.ToArray(), entry.UtcTicks));
            }

            try {
                if (migratedEntries.Count == 0) {
                    File.Delete(segmentPath);
                    continue;
                }

                var tempPath = segmentPath + ".migrating";
                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
                    var newHeader = WalFileHeaderCodec.Encode(header.DatabaseId, (uint)targetGeneration);
                    fileStream.Write(newHeader, 0, newHeader.Length);

                    foreach (var entry in migratedEntries) {
                        var frame = WalRecordCodec.Encode(entry.Lsn, entry.Kind, entry.Changes, entry.UtcTicks);
                        fileStream.Write(frame, 0, frame.Length);
                    }

                    fileStream.Flush(flushToDisk: true);
                }

                File.Delete(segmentPath);
                File.Move(tempPath, segmentPath);
            } catch (Exception ex) {
                return Result.Error(DbError.SystemFailure(ex));
            }
        }

        return DirectorySync.TrySync(archiveDirectory, out _) ? Result.Ok() : Result.Error(DbError.WalDirectorySyncFailed());
    }

    static public Result<List<WalSegmentDescription>> DescribeSegments(string coldStorePath) {
        var archiveDirectory = Path.Combine(coldStorePath, ArchiveDirectoryName);
        var descriptions = new List<WalSegmentDescription>();
        if (!Directory.Exists(archiveDirectory)) return Result.Ok(descriptions);

        foreach (var segmentPath in Directory.EnumerateFiles(archiveDirectory, $"*{SegmentExtension}")
                     .OrderBy(p => p, StringComparer.Ordinal)) {
            var readResult = ReadSegment(segmentPath);
            if (readResult.IsError()) return readResult.Void();

            var (generation, entries) = readResult.Unwrap();
            var anyUnstamped = false;
            var oldest = ulong.MaxValue;
            var newest = 0UL;

            foreach (var entry in entries) {
                if (entry.UtcTicks == 0) { anyUnstamped = true; continue; }
                if (entry.UtcTicks < oldest) oldest = entry.UtcTicks;
                if (entry.UtcTicks > newest) newest = entry.UtcTicks;
            }

            var noneStamped = oldest == ulong.MaxValue;
            descriptions.Add(new WalSegmentDescription(
                segmentPath,
                generation,
                entries.Length,
                noneStamped ? 0 : oldest,
                noneStamped ? 0 : newest,
                anyUnstamped));
        }

        return Result.Ok(descriptions);
    }

    static private void TryDeletePartialSegment(string segmentPath) {
        try { File.Delete(segmentPath); } catch { /* */ }
    }

    static public Result<int> DeleteSegmentsOlderThanTimestamp(string coldStorePath, ulong cutoffUtcTicks) {
        var archiveDirectory = Path.Combine(coldStorePath, ArchiveDirectoryName);
        if (!Directory.Exists(archiveDirectory)) return Result.Ok(0);

        var deleted = 0;
        var filesInOrder = Directory.EnumerateFiles(archiveDirectory, $"*{SegmentExtension}").OrderBy(p => p, StringComparer.Ordinal);
        foreach (var segmentPath in filesInOrder) {
            var readResult = ReadSegment(segmentPath);
            if (readResult.IsError()) return readResult.Void();

            var (_, entries) = readResult.Unwrap();
            if (!IsWhollyOlderThan(entries, cutoffUtcTicks)) break;

            try {
                File.Delete(segmentPath);
                deleted++;
            } catch (Exception ex) {
                return Result<int>.Error(DbError.SystemFailure(ex));
            }
        }

        return DirectorySync.TrySync(archiveDirectory, out _)
            ? Result.Ok(deleted)
            : Result<int>.Error(DbError.WalDirectorySyncFailed());
    }

    static private bool IsWhollyOlderThan(DecodedWalEntry[] entries, ulong cutoffUtcTicks) {
        if (entries.Length == 0) return true;

        foreach (var entry in entries) {
            if (entry.UtcTicks == 0) return false;
            if (entry.UtcTicks >= cutoffUtcTicks) return false;
        }
        return true;
    }
    
    static private Result<(uint Generation, DecodedWalEntry[] Entries)> ReadSegment(string path) {
        byte[] bytes;
        try {
            bytes = File.ReadAllBytes(path);
        } catch (Exception ex) {
            return Result<(uint, DecodedWalEntry[])>.Error(DbError.SystemFailure(ex));
        }

        if (bytes.Length < WalFileHeaderCodec.Size || !WalFileHeaderCodec.TryDecode(bytes, out var header))
            return Result<(uint, DecodedWalEntry[])>.Error(DbError.WalCorrupted());

        var scan = WalRecordCodec.Scan(bytes.AsSpan(WalFileHeaderCodec.Size));
        if (scan.Status != WalScanStatus.Clean) return Result<(uint, DecodedWalEntry[])>.Error(DbError.WalCorrupted());

        return (header.Generation, scan.Entries.Where(e => e.Kind == WalEntryKind.Operation).ToArray());
    }

    static private void MergeInOrder(List<(DecodedWalEntry, int)> into, DecodedWalEntry[] batch, int generation, ref ulong lastSeenLsn, ref bool started) {
        foreach (var entry in batch) {
            if (started && entry.Lsn <= lastSeenLsn) continue;

            into.Add((entry, generation));
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
