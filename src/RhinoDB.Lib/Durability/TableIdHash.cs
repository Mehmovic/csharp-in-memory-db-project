using System.Text;

namespace RhinoDB.Lib.Durability;

static public class TableIdHash {
    static public uint Compute(string accessor) {
        return Encoding.UTF8.GetBytes(accessor)
            .Aggregate(2166136261u, (current, b) => (current ^ b) * 16777619u);
    }
}
