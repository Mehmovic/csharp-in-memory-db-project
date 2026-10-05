using Microsoft.CodeAnalysis;

namespace RhinoDB.Tools.Migration;

static public class CompilationErrors {
    private const int MaxReported = 10;

    static public bool Report(Compilation compilation, string projectPath) {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count == 0) return false;

        Console.Error.WriteLine($"'{projectPath}' doesn't compile ({errors.Count} error(s)) - fix the build before reading its schema:");
        foreach (var error in errors.Take(MaxReported)) Console.Error.WriteLine($"  {error}");
        if (errors.Count > MaxReported) Console.Error.WriteLine($"  ... and {errors.Count - MaxReported} more.");
        return true;
    }
}
