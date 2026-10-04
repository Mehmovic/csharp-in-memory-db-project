namespace RhinoDB.Lib.Server.Realtime;

public enum FrameType : ushort {
    Subscribe,
    Unsubscribe,
    DiffBatch,
    Heartbeat,
    Ack,
    Resume,
    Rpc,
    RpcResult,
    Hello,
}

public readonly record struct Frame(FrameType Type, ReadOnlyMemory<byte> Payload);
