using System.Buffers.Binary;

using RhinoDB.Core;

namespace RhinoDB.Lib.Server.Realtime;

// Rpc:       procedureHash u32 | requestId u32 | args body
// RpcResult: requestId u32 | kind u8 | customCode u16 | value body (empty on error)
// A client learns an error's kind and custom code only - never a message or exception text (ClientErrors).
static internal class RpcFrameCodec {
    private const int RequestHeaderLength = 8;
    public const int ResultHeaderLength = 7;

    public readonly record struct RpcRequest(uint ProcedureHash, uint RequestId, ReadOnlyMemory<byte> Body);

    static public bool TryDecodeRequest(ReadOnlyMemory<byte> payload, out RpcRequest request) {
        if (payload.Length < RequestHeaderLength) {
            request = default;
            return false;
        }
        var span = payload.Span;
        request = new RpcRequest(BinaryPrimitives.ReadUInt32LittleEndian(span), BinaryPrimitives.ReadUInt32LittleEndian(span[4..]), payload[RequestHeaderLength..]);
        return true;
    }

    static public byte[] EncodeResult(uint requestId, ErrorKind kind, ushort customCode, ReadOnlyMemory<byte> body) {
        var buffer = new byte[ResultHeaderLength + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, requestId);
        buffer[4] = (byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(5), customCode);
        body.Span.CopyTo(buffer.AsSpan(ResultHeaderLength));
        return buffer;
    }
}
