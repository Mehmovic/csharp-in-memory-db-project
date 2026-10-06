using System.Buffers;

using MemoryPack;

namespace RhinoDB.Lib.Procedures;

static public class ProcedureEnvelope {
    static public byte[] WriteMemoryPackObject(params byte[][] members) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MemoryPackWriter<ArrayBufferWriter<byte>>(ref buffer, MemoryPackWriterOptionalStatePool.Rent(null));
        writer.WriteObjectHeader((byte)members.Length);
        foreach (var member in members) writer.WriteVarInt(member.Length);
        writer.Flush();
        foreach (var member in members) buffer.Write(member);
        return buffer.WrittenSpan.ToArray();
    }

    static public bool TryReadMemoryPackObject(ReadOnlyMemory<byte> body, out ReadOnlyMemory<byte>[] members) {
        members = [];
        int count;
        int[] lengths;
        int offset;
        var reader = new MemoryPackReader(body.Span, MemoryPackReaderOptionalStatePool.Rent(null));
        try {
            if (!reader.TryReadObjectHeader(out var header)) return false;
            count = header;
            lengths = new int[count];
            for (var i = 0; i < count; i++) {
                lengths[i] = reader.ReadVarIntInt32();
                if (lengths[i] < 0) return false;
            }
            offset = reader.Consumed;
        } catch (MemoryPackSerializationException) {
            return false;
        } finally {
            reader.Dispose();
        }

        var read = new ReadOnlyMemory<byte>[count];
        for (var i = 0; i < count; i++) {
            if (offset + (long)lengths[i] > body.Length) return false;
            read[i] = body.Slice(offset, lengths[i]);
            offset += lengths[i];
        }
        members = read;
        return true;
    }
}
