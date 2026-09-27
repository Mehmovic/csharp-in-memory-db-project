namespace RhinoDB.Tools.Contract;

static public class ContractTool {
    static public int Run(string[] args) {
        if (args.Length == 0) {
            PrintUsage();
            return 1;
        }

        return args[0] switch {
            "generate" => ContractGenerateCommand.Run(args[1..]),
            "clean" => ContractCleanCommand.Run(args[1..]),
            _ => Unknown(args[0]),
        };
    }

    static private int Unknown(string verb) {
        Console.Error.WriteLine($"rhinodb contract: unknown command '{verb}'");
        PrintUsage();
        return 1;
    }

    static private void PrintUsage() {
        Console.WriteLine("""
            Usage:
              rhinodb contract generate [--project <path>] [--no-clean]
              rhinodb contract clean [--project <path>]
            """);
    }
}
