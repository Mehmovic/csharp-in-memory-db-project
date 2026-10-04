using System.Text;

namespace RhinoDB.Core.Rpc;

static public class RpcCommandHash {
    static public uint Compute(string commandName) {
        return Encoding.UTF8.GetBytes(commandName)
            .Aggregate(2166136261u, (current, b) => (current ^ b) * 16777619u);
    }
}
