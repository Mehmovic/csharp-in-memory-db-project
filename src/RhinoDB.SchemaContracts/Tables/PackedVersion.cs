namespace RhinoDB.SchemaContracts;

static public class PackedVersion {
    static public uint Pack(byte major, byte minor, ushort patch) =>
        ((uint)major << 24) | ((uint)minor << 16) | patch;

    static public (byte Major, byte Minor, ushort Patch) Unpack(uint packed) =>
        ((byte)(packed >> 24), (byte)((packed >> 16) & 0xFF), (ushort)(packed & 0xFFFF));
}
