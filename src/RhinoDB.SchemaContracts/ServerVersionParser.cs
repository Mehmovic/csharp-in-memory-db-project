namespace RhinoDB.SchemaContracts;

// Duplicates PackedVersion.Pack's exact bit layout (RhinoDB.Core) rather than referencing it - this
// project is netstandard2.0 with zero dependency on RhinoDB.Core, and the packing formula is one pure,
// stable line (same class of deliberate duplication as SchemaWalk.EmitReadField's private Camel copy).
static public class ServerVersionParser {
    static public uint Parse(string version) {
        var parts = version.Split('.');
        if (parts.Length != 3
            || !byte.TryParse(parts[0], out var major)
            || !byte.TryParse(parts[1], out var minor)
            || !ushort.TryParse(parts[2], out var patch)) {
            throw new GeneratorConfigException(
                $"Server.Version '{version}' is not a valid 'major.minor.patch' version string - "
                + "major/minor must each fit in a byte (0-255), patch in a ushort (0-65535).");
        }

        return ((uint)major << 24) | ((uint)minor << 16) | patch;
    }
}
