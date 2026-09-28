namespace RhinoDB.Tools.Dev;

static internal class ColdPathArg {
    static public List<string> Resolve(string[] args, out string? error) {
        var paths = new List<string>();

        for (var i = 0; i < args.Length; i++) {
            if (args[i] != "--cold-path") continue;

            if (i + 1 >= args.Length) {
                error = "--cold-path needs a directory after it.";
                return [];
            }
            paths.Add(Path.GetFullPath(args[i + 1]));
        }

        error = null;
        return paths;
    }

    static public string? ValueAfter(string[] args, string flag, out bool present) {
        for (var i = 0; i < args.Length - 1; i++) {
            if (args[i] != flag) continue;
            present = true;
            return args[i + 1];
        }
        present = false;
        return null;
    }
}