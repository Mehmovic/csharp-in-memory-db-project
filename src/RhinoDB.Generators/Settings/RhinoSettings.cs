using System.IO;

using Microsoft.CodeAnalysis;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators;

static internal class RhinoSettings {
    static public IncrementalValueProvider<RhinoSettingsSnapshot> Provider(IncrementalGeneratorInitializationContext context) =>
        context.AdditionalTextsProvider
            .Where(static t => IsSettingsFile(t.Path))
            .Collect()
            .Select(static (texts, ct) => texts.Length == 0 ? RhinoSettingsSnapshot.Default : RhinoSettingsSnapshot.Read(texts[0].GetText(ct)?.ToString()));

    static public bool IsSettingsFile(string path) => Path.GetFileName(path) == GeneratorConfigLoader.ConfigFileName;
}
