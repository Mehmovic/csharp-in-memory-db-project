using Microsoft.Build.Locator;

namespace RhinoDB.Run.Cli;

static internal class Program {
    static private int Main(string[] args) {
        MSBuildLocator.RegisterDefaults();
        return Dispatch(args);
    }

    // Split from Main so MSBuildLocator.RegisterDefaults() runs before the JIT needs to resolve any
    // Microsoft.Build.*/Microsoft.CodeAnalysis.MSBuild type reference - those assemblies don't exist at a
    // fixed, discoverable location until MSBuildLocator finds the installed SDK and registers it. Dispatch
    // itself, and everything it calls into (RhinoDB.Tools.Migration included), must stay out of Main's own
    // method body for the same reason.
    static private int Dispatch(string[] args) {
        if (args.Length == 0) {
            PrintUsage();
            return 1;
        }

        return args[0] switch {
            "migration" => RhinoDB.Tools.Migration.MigrationTool.Run(args[1..]),
            "dev" => RhinoDB.Tools.Dev.DevTool.Run(args[1..]),
            "wal" => RhinoDB.Tools.Dev.WalTool.Run(args[1..]),
            "contract" => RhinoDB.Tools.Contract.ContractTool.Run(args[1..]),
            _ => Unknown(args[0]),
        };
    }

    static private int Unknown(string tool) {
        Console.Error.WriteLine($"rhinodb: unknown command '{tool}'");
        PrintUsage();
        return 1;
    }

    static private void PrintUsage() {
        Console.WriteLine("""
            rhinodb - RhinoDB's unified CLI

            Usage:
              rhinodb migration status [--project <path>]
              rhinodb migration create <Name> [--project <path>]
              rhinodb dev reset [--project <path>] [--cold-path <path>]... [--yes]
              rhinodb wal prune --cold-path <path> [--older-than <timestamp> | --keep-generations <n>] [--yes]
              rhinodb contract generate [--project <path>] [--no-clean]
              rhinodb contract clean [--project <path>]
            """);
    }
}
