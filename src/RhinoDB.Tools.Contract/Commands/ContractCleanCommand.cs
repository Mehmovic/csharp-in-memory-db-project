using RhinoDB.PreBuild;

namespace RhinoDB.Tools.Contract;

static public class ContractCleanCommand {
    static public int Run(string[] args) {
        var projectPath = ProjectPathArg.Resolve(args, out var resolveError);
        if (resolveError is not null) {
            Console.Error.WriteLine(resolveError);
            return 1;
        }

        var projectDirectory = Path.GetDirectoryName(projectPath!)!;
        var deleted = ShorthandExpansionRunner.Clean(projectDirectory);

        if (deleted.Count == 0) {
            Console.WriteLine("Nothing to clean - no generated files found.");
            return 0;
        }

        foreach (var file in deleted) Console.WriteLine($"  deleted {file}");
        return 0;
    }
}
