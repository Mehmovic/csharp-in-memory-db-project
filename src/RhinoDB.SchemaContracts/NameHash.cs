using System.Text;

namespace RhinoDB.SchemaContracts;

static public class NameHash {
    static public uint Compute(string name) {
        return Encoding.UTF8.GetBytes(name)
            .Aggregate(2166136261u, (current, b) => unchecked((current ^ b) * 16777619u));
    }
}
