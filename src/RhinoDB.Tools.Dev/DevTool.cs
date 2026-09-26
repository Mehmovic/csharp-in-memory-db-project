namespace RhinoDB.Tools.Dev;

static public class DevTool {
    static public int Run(string[] args) {
        if (args.Length == 0) {
            PrintUsage();
            return 1;
        }

        return args[0] switch {
            "reset" => DevResetCommand.Run(args[1..]),
            _ => Unknown(args[0]),
        };
    }

    static private int Unknown(string verb) {
        Console.Error.WriteLine($"rhinodb dev: unknown command '{verb}'");
        PrintUsage();
        return 1;
    }

    static private void PrintUsage() {
        Console.WriteLine("""
            Usage:
              rhinodb dev reset [--project <path>] [--cold-path <path>]... [--yes]
            """);
    }
}
