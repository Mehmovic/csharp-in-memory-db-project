using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

using RhinoDB.Native;

namespace RhinoDB.Lib.Durability;

public interface IChainResolver {
    Result<bool> Resolve(Guid chainId, string[] participants, bool? knownDecision);
    void Acknowledge(IEnumerable<Guid> chainIds);
}

public sealed class ChainLog {
    public const string FileName = "chains.log";
    public const string RootParticipantId = ".";
    private const string WalFileName = "wal.dat";
    private const int FrameHeaderSize = 8;

    private enum RecordKind : byte {
        Commit = 1,
        Abort = 2,
        Ack = 3,
    }

    private sealed class ChainRecord(bool commit, string[] participants) {
        public bool Commit { get; } = commit;
        public string[] Participants { get; } = participants;
        public HashSet<string> Acknowledged { get; } = new HashSet<string>(StringComparer.Ordinal);
    }

    private readonly string rootColdPath;
    private readonly string path;
    private readonly Lock gate = new Lock();
    private readonly Dictionary<Guid, ChainRecord> chains;

    private ChainLog(string rootColdPath, string path, Dictionary<Guid, ChainRecord> chains) {
        this.rootColdPath = rootColdPath;
        this.path = path;
        this.chains = chains;
    }

    static public Result<ChainLog> Open(string rootColdPath) {
        var path = Path.Combine(rootColdPath, FileName);
        try {
            Directory.CreateDirectory(rootColdPath);
            var chains = new Dictionary<Guid, ChainRecord>();
            var isNew = !File.Exists(path);
            var needsRewrite = false;

            if (!isNew) {
                var bytes = File.ReadAllBytes(path);
                var validLength = Replay(bytes, chains);
                needsRewrite = validLength != bytes.Length;
            }

            foreach (var (chainId, record) in chains.ToArray()) {
                if (!record.Participants.All(record.Acknowledged.Contains)) continue;
                chains.Remove(chainId);
                needsRewrite = true;
            }

            if (needsRewrite) RewriteCompacted(rootColdPath, path, chains);

            if (isNew) {
                using (var created = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                    created.Flush(flushToDisk: true);
                if (!DirectorySync.TrySync(rootColdPath, out _)) return Result<ChainLog>.Error(DbError.WalDirectorySyncFailed());
            }
            return Result<ChainLog>.Ok(new ChainLog(rootColdPath, path, chains));
        } catch (IOException ex) {
            return Result<ChainLog>.Error(DbError.SystemFailure(ex));
        }
    }

    public string WalPathOf(string participantId) =>
        participantId == RootParticipantId
            ? Path.Combine(rootColdPath, WalFileName)
            : Path.Combine(rootColdPath, participantId, WalFileName);

    public bool TryGetDecision(Guid chainId, out bool commit) {
        lock (gate) {
            if (chains.TryGetValue(chainId, out var record)) {
                commit = record.Commit;
                return true;
            }
        }
        commit = false;
        return false;
    }

    public int RetainedChainCount {
        get { lock (gate) return chains.Count; }
    }

    public Result<bool> Record(Guid chainId, bool commit, string[] participants) {
        lock (gate) return RecordUnderGate(chainId, commit, participants);
    }

    private Result<bool> RecordUnderGate(Guid chainId, bool commit, string[] participants) {
        if (chains.TryGetValue(chainId, out var existing)) return Result<bool>.Ok(existing.Commit);
        try {
            Append(EncodeDecision(commit ? RecordKind.Commit : RecordKind.Abort, chainId, participants), durable: true);
        } catch (Exception ex) {
            return Result<bool>.Error(DbError.WalDurabilityFailed(ex));
        }
        chains[chainId] = new ChainRecord(commit, participants);
        return Result<bool>.Ok(commit);
    }

    public void Acknowledge(Guid chainId, string participantId) {
        lock (gate) {
            if (!chains.TryGetValue(chainId, out var record) || !record.Acknowledged.Add(participantId)) return;
            try { Append(EncodeAck(chainId, participantId), durable: false); }
            catch { /* best-effort */ }
        }
    }

    public IChainResolver ResolverFor(string participantId) => new Resolver(this, participantId);

    private sealed class Resolver(ChainLog log, string self) : IChainResolver {
        public Result<bool> Resolve(Guid chainId, string[] participants, bool? knownDecision) =>
            log.Resolve(chainId, participants, knownDecision, self);

        public void Acknowledge(IEnumerable<Guid> chainIds) {
            foreach (var chainId in chainIds) log.Acknowledge(chainId, self);
        }
    }

    private Result<bool> Resolve(Guid chainId, string[] participants, bool? knownDecision, string self) {
        lock (gate) {
            if (chains.TryGetValue(chainId, out var existing)) return Result<bool>.Ok(existing.Commit);
            if (knownDecision is { } known) return RecordUnderGate(chainId, known, participants);

            var commit = true;
            foreach (var participant in participants) {
                if (participant == self) continue;

                var entriesResult = WriteAheadLog.ReadEntriesShared(WalPathOf(participant));
                if (entriesResult.IsError()) return entriesResult.Void();

                var verdict = ParticipantVerdict(entriesResult.Unwrap(), chainId);
                if (verdict) continue;
                commit = false;
                break;
            }
            return RecordUnderGate(chainId, commit, participants);
        }
    }

    // true = prepared or committed, false = missing or aborted.
    static private bool ParticipantVerdict(DecodedWalEntry[] entries, Guid chainId) {
        var prepared = false;
        foreach (var entry in entries) {
            if (entry.ChainId != chainId) continue;
            switch (entry.Kind) {
                case WalEntryKind.ChainAbort: return false;
                case WalEntryKind.ChainCommit: return true;
                case WalEntryKind.ChainPrepare: prepared = true; break;
            }
        }
        return prepared;
    }

    private void Append(byte[] payload, bool durable) {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        WriteFrame(stream, payload);
        stream.Flush(flushToDisk: durable);
    }

    static private byte[] EncodeDecision(RecordKind kind, Guid chainId, string[] participants) {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8);
        writer.Write((byte)kind);
        writer.Write(chainId.ToByteArray());
        writer.Write((ushort)participants.Length);
        foreach (var participant in participants) writer.Write(participant);
        writer.Flush();
        return buffer.ToArray();
    }

    static private byte[] EncodeAck(Guid chainId, string participantId) {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8);
        writer.Write((byte)RecordKind.Ack);
        writer.Write(chainId.ToByteArray());
        writer.Write(participantId);
        writer.Flush();
        return buffer.ToArray();
    }

    // Returns how many bytes were valid - a torn or corrupt tail stops the replay there.
    static private int Replay(byte[] bytes, Dictionary<Guid, ChainRecord> chains) {
        var offset = 0;
        while (offset + FrameHeaderSize <= bytes.Length) {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            if (length <= 0 || offset + FrameHeaderSize + length > bytes.Length) break;
            var payload = bytes.AsSpan(offset + FrameHeaderSize, length);
            if (Crc32.HashToUInt32(payload) != BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4))) break;

            if (!TryApply(payload.ToArray(), chains)) break;
            offset += FrameHeaderSize + length;
        }
        return offset;
    }

    static private bool TryApply(byte[] payload, Dictionary<Guid, ChainRecord> chains) {
        try {
            using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            var kind = (RecordKind)reader.ReadByte();
            var chainId = new Guid(reader.ReadBytes(16));
            switch (kind) {
                case RecordKind.Commit or RecordKind.Abort: {
                    var count = reader.ReadUInt16();
                    var participants = new string[count];
                    for (var i = 0; i < count; i++) participants[i] = reader.ReadString();
                    chains.TryAdd(chainId, new ChainRecord(kind == RecordKind.Commit, participants));
                    return true;
                }
                case RecordKind.Ack: {
                    var participant = reader.ReadString();
                    if (chains.TryGetValue(chainId, out var record)) record.Acknowledged.Add(participant);
                    return true;
                }
                default: return false;
            }
        } catch (EndOfStreamException) {
            return false;
        }
    }

    static private void RewriteCompacted(string rootColdPath, string path, Dictionary<Guid, ChainRecord> chains) {
        var tempPath = path + ".tmp";
        using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
            foreach (var (chainId, record) in chains) {
                WriteFrame(temp, EncodeDecision(record.Commit ? RecordKind.Commit : RecordKind.Abort, chainId, record.Participants));
                foreach (var participant in record.Acknowledged) WriteFrame(temp, EncodeAck(chainId, participant));
            }
            temp.Flush(flushToDisk: true);
        }
        File.Move(tempPath, path, overwrite: true);
        DirectorySync.TrySync(rootColdPath, out _);
    }

    static private void WriteFrame(Stream target, byte[] payload) {
        Span<byte> header = stackalloc byte[FrameHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Crc32.HashToUInt32(payload));
        target.Write(header);
        target.Write(payload);
    }
}
