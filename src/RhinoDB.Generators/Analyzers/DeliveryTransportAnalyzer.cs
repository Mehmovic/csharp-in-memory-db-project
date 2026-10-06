using System.Collections.Immutable;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeliveryTransportAnalyzer : DiagnosticAnalyzer {
    private const string DeliveryTypeFullName = "RhinoDB.Lib.Server.Realtime.Delivery";

    static private readonly DiagnosticDescriptor UnreliableDeliveryUnsupportedDiagnostic = new DiagnosticDescriptor(
        "RHINO040",
        "Delivery mode not supported by the configured transport",
        "Delivery.{0} is not available - rdbsettings.json selects Network.Transport '{1}', which only delivers reliably and in order",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [UnreliableDeliveryUnsupportedDiagnostic];

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start => {
            var delivery = start.Compilation.GetTypeByMetadataName(DeliveryTypeFullName);
            if (delivery is null) return;

            var settingsFile = start.Options.AdditionalFiles.FirstOrDefault(f => RhinoSettings.IsSettingsFile(f.Path));
            var settings = settingsFile is null
                ? RhinoSettingsSnapshot.Default
                : RhinoSettingsSnapshot.Read(settingsFile.GetText(start.CancellationToken)?.ToString());
            if (settings.Transport != NetworkTransportKind.WebSocket) return;

            start.RegisterOperationAction(operationContext => {
                var field = ((IFieldReferenceOperation)operationContext.Operation).Field;
                if (!SymbolEqualityComparer.Default.Equals(field.ContainingType, delivery) || field.Name == "ReliableOrdered") return;
                operationContext.ReportDiagnostic(Diagnostic.Create(
                    UnreliableDeliveryUnsupportedDiagnostic, operationContext.Operation.Syntax.GetLocation(), field.Name, settings.Transport));
            }, OperationKind.FieldReference);
        });
    }
}
