using System.Buffers.Binary;

using RhinoDB.Core;

namespace RhinoDB.Lib.Server.Realtime;

static internal class RpcFrameCodec {
    private const int RequestHeaderLength = 8;
    private const int ResultHeaderLength = 5;

    public readonly record struct RpcRequest(uint CommandHash, uint RequestId, ReadOnlyMemory<byte> Body);

    static public RpcRequest DecodeRequest(ReadOnlyMemory<byte> payload) {
        var span = payload.Span;
        var commandHash = BinaryPrimitives.ReadUInt32LittleEndian(span);
        var requestId = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        return new RpcRequest(commandHash, requestId, payload[RequestHeaderLength..]);
    }

    static public byte[] EncodeResult(uint requestId, ErrorKind kind, ReadOnlyMemory<byte> body) {
        var buffer = new byte[ResultHeaderLength + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, requestId);
        buffer[4] = (byte)kind;
        body.Span.CopyTo(buffer.AsSpan(ResultHeaderLength));
        return buffer;
    }
}
