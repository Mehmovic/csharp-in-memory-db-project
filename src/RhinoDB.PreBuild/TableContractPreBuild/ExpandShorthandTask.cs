using Microsoft.Build.Framework;

namespace RhinoDB.PreBuild;

public sealed class ExpandShorthandTask : Microsoft.Build.Utilities.Task {
    [Required]
    public string ProjectDirectory { get; set; } = "";

    public override bool Execute() {
        var outcome = ShorthandExpansionRunner.Generate(ProjectDirectory);
        foreach (var error in outcome.Errors) Log.LogError(error);
        return outcome.Success;
    }
}
