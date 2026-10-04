using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators;

[Generator]
public sealed class RpcCommandGenerator : IIncrementalGenerator {
    private const string RpcCommandAttributeFullName = "RhinoDB.Core.Rpc.RpcCommandAttribute";

    static private readonly DiagnosticDescriptor InvalidRpcCommandSignatureDiagnostic = new DiagnosticDescriptor(
        "RHINO035",
        "[RpcCommand] method has an invalid signature",
        "'{0}' is [RpcCommand]-attributed but must be a static method shaped like 'static Task<Result<ReadOnlyMemory<byte>>> "
        + "Method(RhinoHost host, ReadOnlyMemory<byte> body, CancellationToken ct)'",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor DuplicateRpcCommandHashDiagnostic = new DiagnosticDescriptor(
        "RHINO036",
        "Two or more [RpcCommand] methods resolve to the same command hash",
        "'{0}' and '{1}' both resolve to RPC command hash {2} (names '{3}' and '{4}') - give one of them " + "an explicit, distinct Name",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var commands = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                RpcCommandAttributeFullName,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, _) => ToCommandModel(ctx))
            .Collect();

        context.RegisterSourceOutput(commands, static (spc, models) => Emit(spc, models));
    }

    static private CommandModel ToCommandModel(GeneratorAttributeSyntaxContext ctx) {
        var method = (IMethodSymbol)ctx.TargetSymbol;
        var nameArg = ctx.Attributes[0].NamedArguments.FirstOrDefault(kv => kv.Key == "Name");
        var name = nameArg.Value.Value as string ?? method.Name;

        var isValid =
            method.IsStatic
            && method.Parameters.Length == 3
            && method.Parameters[0].Type is INamedTypeSymbol { Name: "RhinoHost" } hostType
                && hostType.ContainingNamespace.ToDisplayString() == "RhinoDB.Lib.Hosting"
            && IsReadOnlyMemoryOfByte(method.Parameters[1].Type)
            && method.Parameters[2].Type is INamedTypeSymbol { Name: "CancellationToken" } ctType
                && ctType.ContainingNamespace.ToDisplayString() == "System.Threading"
            && method.ReturnType is INamedTypeSymbol { Name: "Task", TypeArguments.Length: 1 } taskType
                && taskType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks"
                && taskType.TypeArguments[0] is INamedTypeSymbol { Name: "Result", TypeArguments.Length: 1 } resultType
                && resultType.ContainingNamespace.ToDisplayString() == "RhinoDB.Core"
                && IsReadOnlyMemoryOfByte(resultType.TypeArguments[0]);

        if (!isValid) {
            var location = method.Locations.FirstOrDefault() ?? Location.None;
            return new CommandModel(name, 0, "", Diagnostic.Create(InvalidRpcCommandSignatureDiagnostic, location, method.ToDisplayString()));
        }

        var hash = TableIdHash.Compute(name);
        var methodReference = method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + method.Name;
        return new CommandModel(name, hash, methodReference, null);
    }

    static private bool IsReadOnlyMemoryOfByte(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "ReadOnlyMemory", TypeArguments.Length: 1 } romType
        && romType.ContainingNamespace.ToDisplayString() == "System"
        && romType.TypeArguments[0].SpecialType == SpecialType.System_Byte;

    static private void Emit(SourceProductionContext context, ImmutableArray<CommandModel> commands) {
        foreach (var command in commands)
            if (command.Diagnostic is not null) context.ReportDiagnostic(command.Diagnostic);

        var resolved = ImmutableArray.CreateBuilder<CommandModel>();
        foreach (var group in commands.Where(c => c.Diagnostic is null).GroupBy(c => c.Hash)) {
            if (group.Count() > 1) {
                var colliding = group.ToImmutableArray();
                for (var i = 1; i < colliding.Length; i++)
                    context.ReportDiagnostic(Diagnostic.Create(
                        DuplicateRpcCommandHashDiagnostic, Location.None,
                        colliding[0].MethodReference, colliding[i].MethodReference, colliding[0].Hash, colliding[0].Name, colliding[i].Name));
                continue;
            }
            resolved.Add(group.First());
        }

        if (resolved.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Generated by RhinoDB.Generators.RpcCommandGenerator from every [RpcCommand] method.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace RhinoDB.Lib.Hosting;");
        sb.AppendLine();
        sb.AppendLine("public static class GeneratedRpcCommands {");
        foreach (var command in resolved)
            sb.AppendLine($"    public const uint {ConstIdentifier(command)}CommandHash = {command.Hash}u;");
        sb.AppendLine();
        sb.AppendLine("    public static RhinoHostBuilder AddGeneratedRpcCommands(this RhinoHostBuilder builder) {");
        foreach (var command in resolved)
            sb.AppendLine($"        builder.AddRpcCommand({ConstIdentifier(command)}CommandHash, {command.MethodReference});");
        sb.AppendLine("        return builder;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource("GeneratedRpcCommands.g.cs", sb.ToString());
    }

    // The command's routing Name can be any string a developer chooses - the generated const's C#
    // identifier instead derives from the fully-qualified method reference, which is always a valid,
    // already-unique identifier, so no name-sanitization/collision logic is needed for it separately.
    static private string ConstIdentifier(CommandModel command) =>
        command.MethodReference.Replace("global::", "").Replace(".", "_");

    private sealed class CommandModel(string name, uint hash, string methodReference, Diagnostic? diagnostic) {
        public string Name { get; } = name;
        public uint Hash { get; } = hash;
        public string MethodReference { get; } = methodReference;
        public Diagnostic? Diagnostic { get; } = diagnostic;
    }
}
