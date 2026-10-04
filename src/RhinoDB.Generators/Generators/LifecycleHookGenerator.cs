using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators;

[Generator]
public sealed class LifecycleHookGenerator : IIncrementalGenerator {
    static private readonly ImmutableArray<LifecycleHookKind> AllKinds =
        [LifecycleHookKind.OnInit, LifecycleHookKind.OnStart, LifecycleHookKind.OnClientConnect, LifecycleHookKind.OnClientDisconnect];

    static private readonly DiagnosticDescriptor InvalidOnInitMethodSignatureDiagnostic = InvalidSignatureDiagnostic("RHINO030", "OnInit");
    static private readonly DiagnosticDescriptor InvalidOnStartMethodSignatureDiagnostic = InvalidSignatureDiagnostic("RHINO031", "OnStart");
    static private readonly DiagnosticDescriptor InvalidOnClientConnectMethodSignatureDiagnostic = InvalidSignatureDiagnostic("RHINO033", "OnClientConnect");
    static private readonly DiagnosticDescriptor InvalidOnClientDisconnectMethodSignatureDiagnostic = InvalidSignatureDiagnostic("RHINO034", "OnClientDisconnect");

    static private readonly DiagnosticDescriptor DuplicateLifecycleHookDiagnostic = new DiagnosticDescriptor(
        "RHINO032",
        "Database has more than one hook of the same kind",
        "Database '{0}' has more than one [{1}] hook ({2}) - a database can have at most one {1} hook",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private DiagnosticDescriptor InvalidSignatureDiagnostic(string id, string hookName) => new DiagnosticDescriptor(
        id,
        $"[{hookName}] method has an invalid signature",
        $"'{{0}}' is [{hookName}]-attributed but must be a static method shaped like " + "'static Task<Result> Method(RhinoContext<TDb> ctx)' for some database type TDb",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private string AttributeFullNameFor(LifecycleHookKind kind) => kind switch {
        LifecycleHookKind.OnInit => "RhinoDB.Core.Tables.OnInitAttribute",
        LifecycleHookKind.OnStart => "RhinoDB.Core.Tables.OnStartAttribute",
        LifecycleHookKind.OnClientConnect => "RhinoDB.Core.Tables.OnClientConnectAttribute",
        LifecycleHookKind.OnClientDisconnect => "RhinoDB.Core.Tables.OnClientDisconnectAttribute",
        _ => throw new System.ArgumentOutOfRangeException(nameof(kind))
    };

    static private DiagnosticDescriptor InvalidSignatureDiagnosticFor(LifecycleHookKind kind) => kind switch {
        LifecycleHookKind.OnInit => InvalidOnInitMethodSignatureDiagnostic,
        LifecycleHookKind.OnStart => InvalidOnStartMethodSignatureDiagnostic,
        LifecycleHookKind.OnClientConnect => InvalidOnClientConnectMethodSignatureDiagnostic,
        LifecycleHookKind.OnClientDisconnect => InvalidOnClientDisconnectMethodSignatureDiagnostic,
        _ => throw new System.ArgumentOutOfRangeException(nameof(kind))
    };

    static private string OverrideMethodNameFor(LifecycleHookKind kind) => kind switch {
        LifecycleHookKind.OnInit => "OnInitAsync",
        LifecycleHookKind.OnStart => "OnStartAsync",
        LifecycleHookKind.OnClientConnect => "OnClientConnectAsync",
        LifecycleHookKind.OnClientDisconnect => "OnClientDisconnectAsync",
        _ => throw new System.ArgumentOutOfRangeException(nameof(kind))
    };

    static private bool TakesSession(LifecycleHookKind kind) =>
        kind is LifecycleHookKind.OnClientConnect or LifecycleHookKind.OnClientDisconnect;

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var databases = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                DatabaseDiscovery.DatabaseAttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => DatabaseDiscovery.ToDatabaseModel(ctx))
            .Collect();

        var onInitHooks = HookProvider(context, LifecycleHookKind.OnInit);
        var onStartHooks = HookProvider(context, LifecycleHookKind.OnStart);
        var onClientConnectHooks = HookProvider(context, LifecycleHookKind.OnClientConnect);
        var onClientDisconnectHooks = HookProvider(context, LifecycleHookKind.OnClientDisconnect);

        var allHooks = onInitHooks
            .Combine(onStartHooks)
            .Combine(onClientConnectHooks)
            .Combine(onClientDisconnectHooks)
            .Select(static (nested, _) => nested.Left.Left.Left
                .AddRange(nested.Left.Left.Right)
                .AddRange(nested.Left.Right)
                .AddRange(nested.Right));

        var combined = databases.Combine(allHooks);

        context.RegisterSourceOutput(combined, static (spc, pair) => Emit(spc, pair.Left, pair.Right));
    }

    static private IncrementalValueProvider<ImmutableArray<HookModel>> HookProvider(
        IncrementalGeneratorInitializationContext context, LifecycleHookKind kind) =>
        context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeFullNameFor(kind),
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: (ctx, _) => ToHookModel(ctx, kind))
            .Collect();

    static private HookModel ToHookModel(GeneratorAttributeSyntaxContext ctx, LifecycleHookKind kind) {
        var method = (IMethodSymbol)ctx.TargetSymbol;

        var returnsTaskOfResult =
            method.ReturnType is INamedTypeSymbol { Name: "Task", TypeArguments.Length: 1 } taskType
            && taskType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks"
            && taskType.TypeArguments[0] is INamedTypeSymbol { Name: "Result" } resultType
            && resultType.ContainingNamespace.ToDisplayString() == "RhinoDB.Core";

        var hasValidContextParam =
            method.IsStatic
            && method.Parameters.Length == 1
            && method.Parameters[0].Type is INamedTypeSymbol { Name: "RhinoContext", TypeArguments.Length: 1 } contextType
            && contextType.ContainingNamespace.ToDisplayString() == "RhinoDB.Lib.Execution";

        if (!returnsTaskOfResult || !hasValidContextParam) {
            var location = method.Locations.FirstOrDefault() ?? Location.None;
            return new HookModel(kind, method.ToDisplayString(), null,
                Diagnostic.Create(InvalidSignatureDiagnosticFor(kind), location, method.ToDisplayString()));
        }

        var contextTypeSymbol = (INamedTypeSymbol)method.Parameters[0].Type;
        var databaseFullName = contextTypeSymbol.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var methodReference = method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + method.Name;

        return new HookModel(kind, methodReference, databaseFullName, null);
    }

    static private void Emit(SourceProductionContext context, ImmutableArray<DatabaseModel> databases, ImmutableArray<HookModel> hooks) {
        foreach (var hook in hooks)
            if (hook.Diagnostic is not null) context.ReportDiagnostic(hook.Diagnostic);

        var resolved = ResolveAll(context, hooks);

        foreach (var database in databases) {
            var body = new StringBuilder();
            foreach (var kind in AllKinds) {
                if (!resolved.TryGetValue((kind, database.FullName), out var methodReference)) continue;
                var paramList = TakesSession(kind) ? "Session session" : "";
                var ctxArgs = TakesSession(kind) ? "this, session" : "this, Session.System";
                body.AppendLine($"    protected override Task<Result> {OverrideMethodNameFor(kind)}({paramList}) => {methodReference}(new RhinoContext<{database.SimpleName}>({ctxArgs}));");
            }
            if (body.Length == 0) continue;

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine($"// Generated by RhinoDB.Generators.LifecycleHookGenerator for database {database.SimpleName}.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using RhinoDB.Core;");
            sb.AppendLine("using RhinoDB.Lib.Execution;");
            sb.AppendLine("using RhinoDB.Lib.Realtime;");
            sb.AppendLine();
            if (database.Namespace is not null) {
                sb.AppendLine($"namespace {database.Namespace};");
                sb.AppendLine();
            }

            sb.AppendLine($"public partial class {database.SimpleName} {{");
            sb.Append(body);
            sb.AppendLine("}");

            context.AddSource($"{(database.Namespace is null ? "" : database.Namespace + ".")}{database.SimpleName}.LifecycleHooks.g.cs", sb.ToString());
        }
    }

    static private Dictionary<(LifecycleHookKind Kind, string DatabaseFullName), string> ResolveAll(
        SourceProductionContext context, ImmutableArray<HookModel> hooks) {
        var result = new Dictionary<(LifecycleHookKind, string), string>();
        foreach (var group in hooks.Where(h => h.Diagnostic is null).GroupBy(h => (h.Kind, h.DatabaseFullName!))) {
            if (group.Count() > 1) {
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateLifecycleHookDiagnostic, Location.None,
                    group.Key.Item2, group.Key.Kind, string.Join(", ", group.Select(h => h.MethodReference))));
                continue;
            }
            result[group.Key] = group.First().MethodReference;
        }
        return result;
    }

    private enum LifecycleHookKind { OnInit, OnStart, OnClientConnect, OnClientDisconnect }

    private sealed class HookModel(LifecycleHookKind kind, string methodReference, string? databaseFullName, Diagnostic? diagnostic) {
        public LifecycleHookKind Kind { get; } = kind;
        public string MethodReference { get; } = methodReference;
        public string? DatabaseFullName { get; } = databaseFullName;
        public Diagnostic? Diagnostic { get; } = diagnostic;
    }
}
