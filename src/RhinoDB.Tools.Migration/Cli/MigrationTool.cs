namespace RhinoDB.Tools.Migration;

static public class MigrationTool {
    static public int Run(string[] args) {
        if (args.Length == 0) {
            PrintUsage();
            return 1;
        }

        return args[0] switch {
            "status" => MigrationStatusCommand.Run(args[1..]),
            "create" => MigrationCreateCommand.Run(args[1..]),
            "prune-history" => MigrationPruneHistoryCommand.Run(args[1..]),
            _ => Unknown(args[0]),
        };
    }

    static private int Unknown(string verb) {
        Console.Error.WriteLine($"rhinodb migration: unknown command '{verb}'");
        PrintUsage();
        return 1;
    }

    static private void PrintUsage() {
        Console.WriteLine("""
            Usage:
              rhinodb migration status [--project <path>]
              rhinodb migration create <Name> [--project <path>]
              rhinodb migration prune-history [--project <path>]
            """);
    }
}
