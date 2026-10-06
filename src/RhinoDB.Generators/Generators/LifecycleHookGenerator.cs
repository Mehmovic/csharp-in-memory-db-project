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

    static private readonly DiagnosticDescriptor NoDatabaseFoundForHookDiagnostic = new DiagnosticDescriptor(
        "RHINO038",
        "[Hook] omits the database and no [Database] type was found",
        "'{0}' is [{1}]-attributed without specifying an explicit database, and this compilation "
        + "declares no [Database] type to infer it from - specify one explicitly, e.g. [{1}<YourDb>]",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor HookTargetNotARootDiagnostic = new DiagnosticDescriptor(
        "RHINO039",
        "Hook's explicit target must be a Root [Database]",
        "'{0}' targets '{1}', which {2} - hooks only ever fire on the Root database; a Child database "
        + "must be reached explicitly via ctx.BeginTx from a [Procedure], never through a hook",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private DiagnosticDescriptor InvalidSignatureDiagnostic(string id, string hookName) => new DiagnosticDescriptor(
        id,
        $"[{hookName}] method has an invalid signature",
        $"'{{0}}' is [{hookName}]-attributed but must be a static method shaped like 'static Task<Result> Method(RhinoCtx ctx)'",
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

    static private string GenericAttributeFullNameFor(LifecycleHookKind kind) => kind switch {
        LifecycleHookKind.OnInit => "RhinoDB.Core.Tables.OnInitAttribute`1",
        LifecycleHookKind.OnStart => "RhinoDB.Core.Tables.OnStartAttribute`1",
        LifecycleHookKind.OnClientConnect => "RhinoDB.Core.Tables.OnClientConnectAttribute`1",
        LifecycleHookKind.OnClientDisconnect => "RhinoDB.Core.Tables.OnClientDisconnectAttribute`1",
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
        var rootDatabases = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                DatabaseDiscovery.DatabaseAttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => DatabaseDiscovery.ToDatabaseModel(ctx))
            .Collect();

        var declaredRootFullNames = rootDatabases.Select(static (dbs, _) => dbs.Select(d => d.FullName).ToImmutableArray());

        var declaredChildFullNames = DatabaseDiscovery
            .ChildDatabases(context, static (ctx, _) => DatabaseDiscovery.ToDatabaseModel(ctx))
            .Collect()
            .Select(static (dbs, _) => dbs.Select(d => d.FullName).ToImmutableArray());

        // Hooks only ever emit onto the Root - never a Child, per direct user instruction.
        var emitTargets = rootDatabases;

        var onInitHooks = HookProviderForKind(context, LifecycleHookKind.OnInit, declaredRootFullNames, declaredChildFullNames);
        var onStartHooks = HookProviderForKind(context, LifecycleHookKind.OnStart, declaredRootFullNames, declaredChildFullNames);
        var onClientConnectHooks = HookProviderForKind(context, LifecycleHookKind.OnClientConnect, declaredRootFullNames, declaredChildFullNames);
        var onClientDisconnectHooks = HookProviderForKind(context, LifecycleHookKind.OnClientDisconnect, declaredRootFullNames, declaredChildFullNames);

        var allHooks = onInitHooks
            .Combine(onStartHooks)
            .Combine(onClientConnectHooks)
            .Combine(onClientDisconnectHooks)
            .Select(static (nested, _) => nested.Left.Left.Left
                .AddRange(nested.Left.Left.Right)
                .AddRange(nested.Left.Right)
                .AddRange(nested.Right));

        var combined = emitTargets.Combine(allHooks).Combine(LibAccess.OverrideModifier(context));

        context.RegisterSourceOutput(combined, static (spc, input) => Emit(spc, input.Left.Left, input.Left.Right, input.Right));
    }

    static private IncrementalValueProvider<ImmutableArray<HookModel>> HookProviderForKind(
        IncrementalGeneratorInitializationContext context, LifecycleHookKind kind,
        IncrementalValueProvider<ImmutableArray<string>> declaredRootFullNames,
        IncrementalValueProvider<ImmutableArray<string>> declaredChildFullNames) {
        var explicitHooks = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                GenericAttributeFullNameFor(kind),
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: (ctx, _) => ToExplicitCandidate(ctx, kind))
            .Collect()
            .Combine(declaredRootFullNames)
            .Combine(declaredChildFullNames)
            .Select(static (pair, _) => ValidateExplicit(pair.Left.Left, pair.Left.Right, pair.Right));

        var omittedHooks = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeFullNameFor(kind),
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: (ctx, _) => ToOmittedCandidate(ctx, kind))
            .Collect()
            .Combine(declaredRootFullNames)
            .Select(static (pair, _) => ExpandOmitted(pair.Left, pair.Right));

        return explicitHooks.Combine(omittedHooks).Select(static (pair, _) => pair.Left.AddRange(pair.Right));
    }

    static private bool HasValidSignature(IMethodSymbol method) =>
        method.IsStatic
        && method.Parameters.Length == 1
        && method.Parameters[0].Type is INamedTypeSymbol { Name: "RhinoCtx", IsGenericType: false } contextType
        && contextType.ContainingNamespace.ToDisplayString() == "RhinoDB.Lib.Execution"
        && method.ReturnType is INamedTypeSymbol { Name: "Task", TypeArguments.Length: 1 } taskType
        && taskType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks"
        && taskType.TypeArguments[0] is INamedTypeSymbol { Name: "Result" } resultType
        && resultType.ContainingNamespace.ToDisplayString() == "RhinoDB.Core";

    static private string MethodReferenceFor(IMethodSymbol method) =>
        method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + method.Name;

    static private ExplicitHookCandidate ToExplicitCandidate(GeneratorAttributeSyntaxContext ctx, LifecycleHookKind kind) {
        var method = (IMethodSymbol)ctx.TargetSymbol;
        if (!HasValidSignature(method)) {
            var location = method.Locations.FirstOrDefault() ?? Location.None;
            return new ExplicitHookCandidate(kind, method.ToDisplayString(), null,
                Diagnostic.Create(InvalidSignatureDiagnosticFor(kind), location, method.ToDisplayString()));
        }

        var attributeClass = (INamedTypeSymbol)ctx.Attributes[0].AttributeClass!;
        var databaseFullName = attributeClass.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return new ExplicitHookCandidate(kind, MethodReferenceFor(method), databaseFullName, null);
    }

    static private ImmutableArray<HookModel> ValidateExplicit(
        ImmutableArray<ExplicitHookCandidate> candidates, ImmutableArray<string> declaredRootFullNames, ImmutableArray<string> declaredChildFullNames) {
        var results = ImmutableArray.CreateBuilder<HookModel>();
        foreach (var candidate in candidates) {
            if (candidate.Diagnostic is not null) {
                results.Add(new HookModel(candidate.Kind, candidate.MethodReference, null, candidate.Diagnostic));
                continue;
            }
            if (declaredRootFullNames.Contains(candidate.DatabaseFullName!)) {
                results.Add(new HookModel(candidate.Kind, candidate.MethodReference, candidate.DatabaseFullName, null));
                continue;
            }
            var reason = declaredChildFullNames.Contains(candidate.DatabaseFullName!)
                ? "is a [ChildDatabase<...>], not a Root"
                : "is not declared as a [Database] anywhere in this compilation";
            results.Add(new HookModel(candidate.Kind, candidate.MethodReference, null, Diagnostic.Create(
                HookTargetNotARootDiagnostic, Location.None, candidate.MethodReference, candidate.DatabaseFullName, reason)));
        }
        return results.ToImmutable();
    }

    static private OmittedHookCandidate ToOmittedCandidate(GeneratorAttributeSyntaxContext ctx, LifecycleHookKind kind) {
        var method = (IMethodSymbol)ctx.TargetSymbol;
        if (!HasValidSignature(method)) {
            var location = method.Locations.FirstOrDefault() ?? Location.None;
            return new OmittedHookCandidate(kind, method.ToDisplayString(),
                Diagnostic.Create(InvalidSignatureDiagnosticFor(kind), location, method.ToDisplayString()));
        }
        return new OmittedHookCandidate(kind, MethodReferenceFor(method), null);
    }

    static private ImmutableArray<HookModel> ExpandOmitted(ImmutableArray<OmittedHookCandidate> candidates, ImmutableArray<string> declaredRootFullNames) {
        var results = ImmutableArray.CreateBuilder<HookModel>();
        foreach (var candidate in candidates) {
            if (candidate.Diagnostic is not null) {
                results.Add(new HookModel(candidate.Kind, candidate.MethodReference, null, candidate.Diagnostic));
                continue;
            }
            if (declaredRootFullNames.Length == 0) {
                results.Add(new HookModel(candidate.Kind, candidate.MethodReference, null, Diagnostic.Create(
                    NoDatabaseFoundForHookDiagnostic, Location.None, candidate.MethodReference, candidate.Kind)));
                continue;
            }
            foreach (var root in declaredRootFullNames)
                results.Add(new HookModel(candidate.Kind, candidate.MethodReference, root, null));
        }
        return results.ToImmutable();
    }

    static private void Emit(SourceProductionContext context, ImmutableArray<DatabaseModel> databases, ImmutableArray<HookModel> hooks, string overrideModifier) {
        foreach (var hook in hooks)
            if (hook.Diagnostic is not null) context.ReportDiagnostic(hook.Diagnostic);

        var resolved = ResolveAll(context, hooks);

        foreach (var database in databases) {
            var body = new StringBuilder();
            foreach (var kind in AllKinds) {
                if (!resolved.TryGetValue((kind, database.FullName), out var methodReference)) continue;
                var paramList = TakesSession(kind) ? "Session session" : "";
                var ctxArgs = TakesSession(kind) ? "session.Identity, session" : "Identity.System";
                body.AppendLine($"    {overrideModifier} override Task<Result> {OverrideMethodNameFor(kind)}({paramList}) => {methodReference}(new RhinoCtx(this, {ctxArgs}));");
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

    private sealed class OmittedHookCandidate(LifecycleHookKind kind, string methodReference, Diagnostic? diagnostic) {
        public LifecycleHookKind Kind { get; } = kind;
        public string MethodReference { get; } = methodReference;
        public Diagnostic? Diagnostic { get; } = diagnostic;
    }

    private sealed class ExplicitHookCandidate(LifecycleHookKind kind, string methodReference, string? databaseFullName, Diagnostic? diagnostic) {
        public LifecycleHookKind Kind { get; } = kind;
        public string MethodReference { get; } = methodReference;
        public string? DatabaseFullName { get; } = databaseFullName;
        public Diagnostic? Diagnostic { get; } = diagnostic;
    }
}
