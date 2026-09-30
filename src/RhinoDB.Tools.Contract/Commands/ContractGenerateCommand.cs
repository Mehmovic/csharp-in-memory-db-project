using RhinoDB.SchemaContracts;
using RhinoDB.PreBuild;

namespace RhinoDB.Tools.Contract;

static public class ContractGenerateCommand {
    static public int Run(string[] args) {
        var projectPath = ProjectPathArg.Resolve(args, out var resolveError);
        if (resolveError is not null) {
            Console.Error.WriteLine(resolveError);
            return 1;
        }

        var projectDirectory = Path.GetDirectoryName(projectPath!)!;

        if (!args.Contains("--no-clean")) {
            _ = ShorthandExpansionRunner.Clean(projectDirectory);
        }

        var outcome = ShorthandExpansionRunner.Generate(projectDirectory);

        foreach (var written in outcome.WrittenFiles) Console.WriteLine($"  wrote {written}");
        foreach (var error in outcome.Errors) Console.Error.WriteLine(error);

        if (outcome.WrittenFiles.Count == 0 && outcome.Success)
            Console.WriteLine("Nothing to do - every generated file already matches its shorthand source.");

        return outcome.Success ? 0 : 1;
    }
}
