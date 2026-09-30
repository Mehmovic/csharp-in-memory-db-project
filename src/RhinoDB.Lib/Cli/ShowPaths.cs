using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Cli;

static public class ShowPaths {
    private const string Flag = "--show-paths";

    static public bool Requested(string[] args) {
        foreach (var arg in args) {
            if (arg == Flag) return true;
        }
        return false;
    }

    static public int Run(string[] args) {
        var projectFile = ProjectPathArg.Resolve(args, out var projectError, Directory.GetCurrentDirectory());
        if (projectFile is null) {
            Console.Error.WriteLine(projectError);
            return 1;
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        Console.WriteLine($"project  {projectDirectory}");

        var coldPaths = ColdPathArg.Resolve(args, out var coldError);
        if (coldError is not null) {
            Console.Error.WriteLine(coldError);
            return 1;
        }

        if (coldPaths.Count == 0) {
            Console.WriteLine("cold     (none given - a command that needs one will say so)");
        } else {
            foreach (var coldPath in coldPaths) Console.WriteLine($"cold     {coldPath}");
        }

        return 0;
    }
}
