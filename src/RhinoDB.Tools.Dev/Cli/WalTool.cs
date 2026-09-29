namespace RhinoDB.Tools.Dev;

static public class WalTool {
    static public int Run(string[] args) {
        if (args.Length == 0) {
            PrintUsage();
            return 1;
        }

        return args[0] switch {
            "prune" => WalPruneCommand.Run(args[1..]),
            _ => Unknown(args[0]),
        };
    }

    static private int Unknown(string verb) {
        Console.Error.WriteLine($"rhinodb wal: unknown command '{verb}'");
        PrintUsage();
        return 1;
    }

    static private void PrintUsage() {
        Console.WriteLine("""
            Usage:
              rhinodb wal prune --cold-path <path> [--older-than <timestamp> | --keep-generations <n>] [--yes]
            """);
    }
}
