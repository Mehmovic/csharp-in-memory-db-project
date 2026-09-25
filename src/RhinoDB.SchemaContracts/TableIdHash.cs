using System.Text;

namespace RhinoDB.SchemaContracts;

// The single FNV-1a implementation behind tableId. A tableId is part of the on-disk contract:
// WAL frames carry it and cold storage keys its sub-databases by it, so the generator (which bakes
// it into a const at generation time), the runtime (which recomputes it from the accessor handed
// to ColdStore.OpenTable) and any external tooling reading a database must all agree. This lives in
// SchemaContracts because it is the one project visible to both the generator and the runtime;
// Core cannot host it (Generators references Core, not the other way round).
static public class TableIdHash {
    static public uint Compute(string accessor) {
        return Encoding.UTF8.GetBytes(accessor)
            .Aggregate(2166136261u, (current, b) => (current ^ b) * 16777619u);
    }
}