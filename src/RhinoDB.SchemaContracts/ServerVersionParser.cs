namespace RhinoDB.SchemaContracts;

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

        return PackedVersion.Pack(major, minor, patch);
    }
}
