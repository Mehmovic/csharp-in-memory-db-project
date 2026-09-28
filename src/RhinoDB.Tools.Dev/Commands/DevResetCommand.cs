using RhinoDB.SchemaContracts;

namespace RhinoDB.Tools.Dev;

static public class DevResetCommand {
    static public int Run(string[] args) {
        var projectPath = ProjectPathArg.Resolve(args, out var resolveError);
        if (resolveError is not null) {
            Console.Error.WriteLine(resolveError);
            return 1;
        }

        var coldPaths = ColdPathArg.Resolve(args, out var coldPathError);
        if (coldPathError is not null) {
            Console.Error.WriteLine(coldPathError);
            return 1;
        }
        var confirmed = args.Contains("--yes");

        var projectDirectory = Path.GetDirectoryName(projectPath!)!;
        var config = GeneratorConfigLoader.Load(projectDirectory);
        var descriptorPath = Path.Combine(projectDirectory, config.SchemaDescriptorPath);
        var migrationsDirectory = Path.Combine(projectDirectory, config.MigrationsOutputDirectory);

        var targets = new List<string>();
        if (File.Exists(descriptorPath)) targets.Add(descriptorPath);
        if (Directory.Exists(migrationsDirectory)) targets.Add(migrationsDirectory);
        foreach (var coldPath in coldPaths) {
            if (Directory.Exists(coldPath)) targets.Add(coldPath);
        }

        if (targets.Count == 0) {
            Console.WriteLine("Nothing to reset - no committed schema history or cold-storage directory found.");
            return 0;
        }

        Console.WriteLine(confirmed ? "Deleting:" : "Would delete (pass --yes to actually delete):");
        foreach (var target in targets) Console.WriteLine($"  {target}");

        if (!confirmed) {
            Console.WriteLine();
            Console.WriteLine("Dry run only - nothing was deleted. Re-run with --yes to actually reset.");
            return 1;
        }

        if (File.Exists(descriptorPath)) File.Delete(descriptorPath);
        if (Directory.Exists(migrationsDirectory)) Directory.Delete(migrationsDirectory, recursive: true);
        foreach (var coldPath in coldPaths) {
            if (Directory.Exists(coldPath)) Directory.Delete(coldPath, recursive: true);
        }

        Console.WriteLine();
        Console.WriteLine("Done - schema history and cold storage reset. The next build/run starts fresh.");
        return 0;
    }
}
