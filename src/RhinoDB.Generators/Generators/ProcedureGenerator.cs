using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators;

[Generator]
public sealed class ProcedureGenerator : IIncrementalGenerator {
    private const string ProcedureAttributeFullName = "RhinoDB.Core.Procedures.ProcedureAttribute";
    private const string TxCtxSuffix = "TxCtx";

    static private readonly DiagnosticDescriptor ProcedureNotStaticDiagnostic = new(
        "RHINO041",
        "[Procedure] method must be static",
        "'{0}' is a [Procedure] but is not static - procedures are static methods; anything they need comes through their context and parameters",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor InvalidProcedureSignatureDiagnostic = new(
        "RHINO042",
        "[Procedure] method has an invalid signature",
        "'{0}': {1} - a procedure is either 'static Task<Result> M(RhinoCtx ctx, ...)' (or Task<Result<T>>, ValueTask) or "
        + "'static Result M({{Db}}TxCtx ctx, ...)' for the Root or a singleton Child",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor TransactionProcedureNotSynchronousDiagnostic = new(
        "RHINO043",
        "Transaction-only [Procedure] must be synchronous and take only its transaction context",
        "'{0}' takes a {1} and {2} - a transaction-only procedure runs as one synchronous transaction: return Result, "
        + "take no RhinoCtx or CancellationToken; use the general shape (RhinoCtx ctx) for async work",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor TransactionProcedureOnKeyedChildDiagnostic = new(
        "RHINO044",
        "Transaction-only [Procedure] on a keyed Child database",
        "'{0}' takes {1}, but '{2}' is a keyed Child - its key has to be looked up first, so use the general shape: "
        + "'static Task<Result> M(RhinoCtx ctx, ...)' and ctx.BeginTx(key, ...)",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor UnsupportedProcedureTypeDiagnostic = new(
        "RHINO045",
        "[Procedure] parameter or result type is not supported",
        "'{0}': {1} has type '{2}', which {3} - procedure parameters and results may be primitives, enums, string, Guid, "
        + "DateTime, DateTimeOffset, TimeSpan, one-dimensional arrays of those, or a [CustomType]",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    // Like a SpacetimeDB reducer: a transaction's effects reach clients through their views, never as a reply value.
    static private readonly DiagnosticDescriptor TransactionProcedureReturnsAValueDiagnostic = new(
        "RHINO047",
        "Transaction-only [Procedure] cannot return a value",
        "'{0}' returns {1} - a transaction-only procedure returns Result: its changes reach clients through their views. "
        + "To send a value back to the caller only, use the general shape: 'static Task<Result<T>> M(RhinoCtx ctx, ...)'.",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor DuplicateProcedureHashDiagnostic = new(
        "RHINO046",
        "Two [Procedure] methods resolve to the same name hash",
        "'{0}' and '{1}' both resolve to procedure hash {2} (names '{3}' and '{4}') - give one of them an explicit, distinct Name",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly ImmutableHashSet<string> ScalarStructs =
        ImmutableHashSet.Create("System.Guid", "System.DateTime", "System.DateTimeOffset", "System.TimeSpan");

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var procedures = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ProcedureAttributeFullName,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, _) => ToModel(ctx))
            .Collect();

        var roots = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                DatabaseDiscovery.DatabaseAttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => {
                    var model = DatabaseDiscovery.ToDatabaseModel(ctx);
                    return new DatabaseTarget(model.FullName, model.SimpleName, null, false);
                })
            .Collect();

        var children = DatabaseDiscovery
            .ChildDatabases(context, static (ctx, _) => {
                var model = DatabaseDiscovery.ToDatabaseModel(ctx);
                var (rootFullName, keyFullName) = DatabaseDiscovery.ToChildDatabaseTypeArgs(ctx);
                return new DatabaseTarget(model.FullName, model.SimpleName, rootFullName, keyFullName is not null);
            })
            .Collect();

        var protocol = RhinoSettings.Provider(context).Select(static (settings, _) => settings.ClientProtocol);

        context.RegisterSourceOutput(procedures.Combine(roots.Combine(children)).Combine(protocol), static (spc, input) => {
            var ((models, (rootTargets, childTargets)), clientProtocol) = input;
            Emit(spc, models, rootTargets.AddRange(childTargets), clientProtocol);
        });
    }

    // ---- model ----

    static private ProcedureModel ToModel(GeneratorAttributeSyntaxContext ctx) {
        var method = (IMethodSymbol)ctx.TargetSymbol;
        var location = method.Locations.FirstOrDefault() ?? Location.None;
        var display = method.ToDisplayString();
        var nameArg = ctx.Attributes[0].NamedArguments.FirstOrDefault(kv => kv.Key == "Name");
        var name = nameArg.Value.Value as string ?? method.Name;
        var methodReference = method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + method.Name;
        var model = new ProcedureModel(name, NameHash.Compute(name), methodReference, location, display);

        if (!method.IsStatic) return model.Fail(Diagnostic.Create(ProcedureNotStaticDiagnostic, location, display));
        if (method.IsGenericMethod) return model.Fail(Invalid(location, display, "it is generic"));
        if (method.Parameters.Any(p => p.RefKind != RefKind.None))
            return model.Fail(Invalid(location, display, "it has a ref, in or out parameter"));
        if (method.Parameters.Length == 0) return model.Fail(Invalid(location, display, "it has no context parameter"));

        var first = method.Parameters[0].Type;
        var parameters = method.Parameters.Skip(1).ToList();

        if (IsRhinoCtx(first)) {
            model.Shape = ProcedureShape.General;
            if (parameters.Count > 0 && IsCancellationToken(parameters[parameters.Count - 1].Type)) {
                model.TakesCancellationToken = true;
                parameters.RemoveAt(parameters.Count - 1);
            }
            if (!TryReadAsyncResult(method.ReturnType, out var valueType, out var isValueTask))
                return model.Fail(Invalid(location, display, "a general procedure must return Task<Result>, Task<Result<T>>, ValueTask<Result> or ValueTask<Result<T>>"));
            model.ReturnsValueTask = isValueTask;
            model.Value = valueType is null ? null : Classify(valueType, "its result", model);
        } else if (first.Name.EndsWith(TxCtxSuffix) && first.Name.Length > TxCtxSuffix.Length) {
            model.Shape = ProcedureShape.Transaction;
            model.TxDatabaseSimpleName = first.Name.Substring(0, first.Name.Length - TxCtxSuffix.Length);
            model.TxCtxDisplay = first.Name;

            var problem = method.IsAsync || IsTaskLike(method.ReturnType) ? "is asynchronous"
                : parameters.Any(p => IsRhinoCtx(p.Type)) ? "also takes a RhinoCtx"
                : parameters.Any(p => IsCancellationToken(p.Type)) ? "takes a CancellationToken"
                : null;
            if (problem is not null)
                return model.Fail(Diagnostic.Create(TransactionProcedureNotSynchronousDiagnostic, location, display, first.Name, problem));
            if (!TryReadResult(method.ReturnType, out var valueType))
                return model.Fail(Invalid(location, display, "a transaction-only procedure must return Result"));
            if (valueType is not null)
                return model.Fail(Diagnostic.Create(TransactionProcedureReturnsAValueDiagnostic, location, display, method.ReturnType.ToDisplayString()));
        } else {
            return model.Fail(Invalid(location, display, $"its first parameter is '{first.ToDisplayString()}', not RhinoCtx or a {{Db}}TxCtx"));
        }

        for (var i = 0; i < parameters.Count; i++) {
            var classified = Classify(parameters[i].Type, $"parameter '{parameters[i].Name}'", model);
            if (classified is not null) model.Parameters.Add(classified.WithIndex(i));
        }
        return model;
    }

    static private Diagnostic Invalid(Location location, string display, string why) =>
        Diagnostic.Create(InvalidProcedureSignatureDiagnostic, location, display, why);

    static private bool IsRhinoCtx(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "RhinoCtx" } named && named.ContainingNamespace.ToDisplayString() == "RhinoDB.Lib.Execution";

    static private bool IsCancellationToken(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "CancellationToken" } named && named.ContainingNamespace.ToDisplayString() == "System.Threading";

    static private bool IsTaskLike(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "Task" or "ValueTask" } named && named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";

    // Result -> (true, null); Result<T> -> (true, T).
    static private bool TryReadResult(ITypeSymbol type, out ITypeSymbol? valueType) {
        valueType = null;
        if (type is not INamedTypeSymbol { Name: "Result" } result || result.ContainingNamespace.ToDisplayString() != "RhinoDB.Core") return false;
        if (result.TypeArguments.Length == 1) valueType = result.TypeArguments[0];
        return result.TypeArguments.Length <= 1;
    }

    static private bool TryReadAsyncResult(ITypeSymbol type, out ITypeSymbol? valueType, out bool isValueTask) {
        valueType = null;
        isValueTask = false;
        if (type is not INamedTypeSymbol { TypeArguments.Length: 1 } task || !IsTaskLike(task)) return false;
        isValueTask = task.Name == "ValueTask";
        return TryReadResult(task.TypeArguments[0], out valueType);
    }

    static private WireValue? Classify(ITypeSymbol type, string what, ProcedureModel model) {
        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var kind = type.SpecialType == SpecialType.System_String ? RowFieldKind.String
            : type.IsUnmanagedType ? RowFieldKind.Unmanaged
            : RowFieldKind.Other;

        if (IsScalar(type) || type.SpecialType == SpecialType.System_String)
            return new WireValue(fullName, kind, isCustomType: false, hasMemoryPackable: true, hasMessagePackObject: true, what);
        if (type is IArrayTypeSymbol { Rank: 1 } array && (IsScalar(array.ElementType) || array.ElementType.SpecialType == SpecialType.System_String))
            return new WireValue(fullName, RowFieldKind.Other, isCustomType: false, hasMemoryPackable: true, hasMessagePackObject: true, what);
        if (type is INamedTypeSymbol named && SchemaWalk.HasCustomTypeAttribute(named))
            return new WireValue(fullName, kind, isCustomType: true, SchemaWalk.HasMemoryPackable(named), SchemaWalk.HasMessagePackObject(named), what);

        model.Diagnostics.Add(Diagnostic.Create(UnsupportedProcedureTypeDiagnostic, model.Location, model.Display, what, type.ToDisplayString(), "is not one of them"));
        return null;
    }

    static private bool IsScalar(ITypeSymbol type) =>
        type.TypeKind == TypeKind.Enum
        || type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Char
            or SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal
        || ScalarStructs.Contains(type.OriginalDefinition.ToDisplayString());

    // ---- validation that needs the whole compilation (databases, protocol, every other procedure) ----

    static private void Emit(SourceProductionContext spc, ImmutableArray<ProcedureModel> models, ImmutableArray<DatabaseTarget> databases, ClientProtocolKind protocol) {
        var valid = ImmutableArray.CreateBuilder<ProcedureModel>();
        foreach (var model in models) {
            if (model.Shape == ProcedureShape.Transaction && model.Diagnostics.Count == 0) ResolveTransactionTarget(model, databases);
            foreach (var value in model.AllValues()) CheckProtocolAttributes(model, value, protocol);
            foreach (var diagnostic in model.Diagnostics) spc.ReportDiagnostic(diagnostic);
            if (model.Diagnostics.Count == 0) valid.Add(model);
        }

        var resolved = new List<ProcedureModel>();
        foreach (var group in valid.GroupBy(m => m.Hash)) {
            var colliding = group.ToList();
            if (colliding.Count > 1) {
                for (var i = 1; i < colliding.Count; i++)
                    spc.ReportDiagnostic(Diagnostic.Create(DuplicateProcedureHashDiagnostic, colliding[i].Location,
                        colliding[0].MethodReference, colliding[i].MethodReference, colliding[0].Hash, colliding[0].Name, colliding[i].Name));
                continue;
            }
            resolved.Add(colliding[0]);
        }
        if (resolved.Count == 0) return;

        spc.AddSource("GeneratedProcedures.g.cs", EmitSource(resolved, protocol));
    }

    static private void ResolveTransactionTarget(ProcedureModel model, ImmutableArray<DatabaseTarget> databases) {
        var target = databases.FirstOrDefault(d => d.SimpleName == model.TxDatabaseSimpleName);
        if (target is null) {
            model.Diagnostics.Add(Invalid(model.Location, model.Display, $"'{model.TxCtxDisplay}' doesn't name a [Database] or singleton [ChildDatabase<TRoot>] in this project"));
            return;
        }
        if (target.IsKeyedChild) {
            model.Diagnostics.Add(Diagnostic.Create(TransactionProcedureOnKeyedChildDiagnostic, model.Location, model.Display, model.TxCtxDisplay, target.FullName));
            return;
        }
        model.TxDatabase = target;
    }

    static private void CheckProtocolAttributes(ProcedureModel model, WireValue value, ClientProtocolKind protocol) {
        if (!value.IsCustomType) return;
        var missing = protocol switch {
            ClientProtocolKind.VersionedMemoryPack when !value.HasMemoryPackable => "[MemoryPackable]",
            ClientProtocolKind.MessagePack when !value.HasMessagePackObject => "[MessagePackObject]",
            _ => null,
        };
        if (missing is not null)
            model.Diagnostics.Add(Diagnostic.Create(UnsupportedProcedureTypeDiagnostic, model.Location, model.Display, value.What, value.TypeFullName,
                $"is missing {missing}, which the project's ClientProtocol '{protocol}' needs"));
    }

    // ---- emission ----

    private const string Result = "global::RhinoDB.Core.Result";
    private const string DbErrorType = "global::RhinoDB.Core.DbError";
    private const string Bytes = "global::System.ReadOnlyMemory<byte>";
    private const string Reply = "global::RhinoDB.Core.Result<global::System.ReadOnlyMemory<byte>>";
    private const string Session = "global::RhinoDB.Lib.Realtime.Session";

    static private string EmitSource(List<ProcedureModel> procedures, ClientProtocolKind protocol) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Generated by RhinoDB.Generators.ProcedureGenerator from every [Procedure] method.");
        sb.AppendLine($"// Client protocol: {protocol}.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS8619, CS8625");
        sb.AppendLine();
        sb.AppendLine("namespace RhinoDB.Lib.Hosting;");
        sb.AppendLine();
        sb.AppendLine("internal static class GeneratedProcedures {");
        sb.AppendLine("    public static global::RhinoDB.Lib.Hosting.RhinoHostBuilder AddGeneratedProcedures(this global::RhinoDB.Lib.Hosting.RhinoHostBuilder builder) {");
        foreach (var procedure in procedures)
            sb.AppendLine($"        builder.AddProcedure(new global::RhinoDB.Lib.Procedures.ProcedureDescriptor({procedure.ClassName}.Name, {procedure.ClassName}.Hash, {procedure.ClassName}.SingleTransaction, {procedure.ClassName}.Invoke));");
        sb.AppendLine("        return builder;");
        sb.AppendLine("    }");

        foreach (var procedure in procedures) EmitProcedure(sb, procedure, protocol);

        sb.AppendLine("}");
        return sb.ToString();
    }

    static private void EmitProcedure(StringBuilder sb, ProcedureModel p, ClientProtocolKind protocol) {
        var parameters = p.Parameters;
        var valueType = p.Value?.TypeFullName;
        var userResult = valueType is null ? Result : $"{Result}<{valueType}>";

        sb.AppendLine();
        sb.AppendLine($"    // {p.MethodReference} ({(p.Shape == ProcedureShape.General ? "general" : "transaction-only")})");
        sb.AppendLine($"    public static class {p.ClassName} {{");
        sb.AppendLine($"        public const string Name = {Literal(p.Name)};");
        sb.AppendLine($"        public const uint Hash = {p.Hash}u;");
        sb.AppendLine($"        public const bool SingleTransaction = {(p.Shape == ProcedureShape.Transaction ? "true" : "false")};");
        sb.AppendLine();

        // Invoke: the server-side handler. Never throws.
        sb.AppendLine($"        public static async global::System.Threading.Tasks.Task<{Reply}> Invoke(global::RhinoDB.Lib.Hosting.RhinoHost host, {Session} session, {Bytes} body, global::System.Threading.CancellationToken ct) {{");
        foreach (var parameter in parameters) sb.AppendLine($"            {parameter.TypeFullName} {parameter.Local};");
        sb.AppendLine("            try {");
        sb.AppendLine($"                DecodeArgs(body{string.Concat(parameters.Select(x => $", out {x.Local}"))});");
        sb.AppendLine("            } catch (global::System.Exception ex) {");
        sb.AppendLine($"                return {Reply}.Error({DbErrorType}.ProcedureArgsInvalid(ex));");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            var ctx = new global::RhinoDB.Lib.Execution.RhinoCtx(host, session.Identity, session);");
        sb.AppendLine($"            {userResult} result;");
        sb.AppendLine("            try {");
        if (p.Shape == ProcedureShape.General) {
            var args = string.Concat(parameters.Select(x => $", {x.Local}")) + (p.TakesCancellationToken ? ", ct" : "");
            sb.AppendLine($"                result = await {p.MethodReference}(ctx{args});");
        } else {
            EmitTransactionCall(sb, p, userResult);
        }
        sb.AppendLine("            } catch (global::System.Exception ex) {");
        sb.AppendLine($"                return {Reply}.Error({DbErrorType}.ProcedureFailed(ex));");
        sb.AppendLine("            }");
        sb.AppendLine($"            if (result.IsError()) return {Reply}.Error(result.GetError());");
        if (valueType is null) {
            sb.AppendLine($"            return {Reply}.Ok({Bytes}.Empty);");
        } else {
            sb.AppendLine("            try {");
            sb.AppendLine($"                return {Reply}.Ok(EncodeResult(result.Unwrap()));");
            sb.AppendLine("            } catch (global::System.Exception ex) {");
            sb.AppendLine($"                return {Reply}.Error({DbErrorType}.ProcedureFailed(ex));");
            sb.AppendLine("            }");
        }
        sb.AppendLine("        }");
        sb.AppendLine();

        EmitArgsCodec(sb, parameters, protocol);
        if (p.Value is not null) EmitResultCodec(sb, p.Value, protocol);

        sb.AppendLine("    }");
    }

    // One transaction, no closure: the args travel as a tuple next to a static lambda. The procedure's context is built
    // on the loop - Timestamp is when the transaction started there - and a throw becomes ProcedureFailed, which aborts it.
    static private void EmitTransactionCall(StringBuilder sb, ProcedureModel p, string userResult) {
        var db = p.TxDatabase!;
        var tx = db.FullName + "Transaction";
        var txCtx = db.FullName + TxCtxSuffix;
        var tupleType = $"({Session} Session, uint ServerVersion{string.Concat(p.Parameters.Select(x => $", {x.TypeFullName} {x.TupleName}"))})";
        var tupleValue = $"(session, host.ServerVersion{string.Concat(p.Parameters.Select(x => $", {x.Local}"))})";
        var target = db.RootFullName is null
            ? $"ctx.BeginTx<{db.FullName}, {tx}, {tupleType}>("
            : $"ctx.BeginTx<{db.FullName}, {tx}, string, {tupleType}>(global::RhinoDB.Lib.Hosting.SingletonChild.Key, ";
        var userArgs = string.Concat(p.Parameters.Select(x => $", a.{x.TupleName}"));

        sb.AppendLine($"                result = await {target}static (db, tx, a) => {{");
        sb.AppendLine("                    var random = default(global::RhinoDB.Lib.Execution.LazyRhinoRandom);");
        sb.AppendLine("                    try {");
        sb.AppendLine($"                        return {p.MethodReference}(new {txCtx}(tx, a.Session, global::System.DateTime.UtcNow, a.ServerVersion, ref random){userArgs});");
        sb.AppendLine("                    } catch (global::System.Exception ex) {");
        sb.AppendLine($"                        return {userResult}.Error({DbErrorType}.ProcedureFailed(ex));");
        sb.AppendLine("                    }");
        sb.AppendLine($"                }}, {tupleValue});");
    }

    static private void EmitArgsCodec(StringBuilder sb, List<WireValue> parameters, ClientProtocolKind protocol) {
        var outParams = string.Concat(parameters.Select(x => $", out {x.TypeFullName} {x.Local}"));
        var inParams = string.Join(", ", parameters.Select(x => $"{x.TypeFullName} {x.Local}"));

        // EncodeArgs: what a client sends. Generated next to the decoder so the two can't drift.
        sb.AppendLine($"        public static byte[] EncodeArgs({inParams}) {{");
        switch (protocol) {
            case ClientProtocolKind.Raw:
                EmitRawWriterOpen(sb);
                foreach (var x in parameters) EmitIndented(sb, b => SchemaWalk.EmitWriteField(b, x.Field, x.Local), "        ");
                EmitRawWriterClose(sb, "ToArray()");
                break;
            case ClientProtocolKind.VersionedMemoryPack:
                sb.AppendLine($"            return global::RhinoDB.Lib.Procedures.ProcedureEnvelope.WriteMemoryPackObject({string.Join(", ", parameters.Select(x => $"global::MemoryPack.MemoryPackSerializer.Serialize({x.Local})"))});");
                break;
            case ClientProtocolKind.MessagePack:
                sb.AppendLine("            var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();");
                sb.AppendLine("            var writer = new global::MessagePack.MessagePackWriter(buffer);");
                sb.AppendLine($"            writer.WriteArrayHeader({parameters.Count});");
                foreach (var x in parameters)
                    sb.AppendLine($"            global::MessagePack.MessagePackSerializer.Serialize(ref writer, {x.Local}, global::MessagePack.MessagePackSerializer.DefaultOptions);");
                sb.AppendLine("            writer.Flush();");
                sb.AppendLine("            return buffer.WrittenSpan.ToArray();");
                break;
        }
        sb.AppendLine("        }");
        sb.AppendLine();

        // DecodeArgs: throws on a malformed body; Invoke turns that into ProcedureArgsInvalid.
        sb.AppendLine($"        public static void DecodeArgs({Bytes} body{outParams}) {{");
        switch (protocol) {
            case ClientProtocolKind.Raw:
                if (parameters.Count == 0) break;
                sb.AppendLine("            var reader = new global::MemoryPack.MemoryPackReader(body.Span, global::MemoryPack.MemoryPackReaderOptionalStatePool.Rent(null));");
                sb.AppendLine("            try {");
                foreach (var x in parameters) {
                    EmitIndented(sb, b => SchemaWalk.EmitReadField(b, x.Field), "        ");
                    sb.AppendLine($"                {x.Local} = {x.RawReadVariable};");
                }
                sb.AppendLine("            } finally {");
                sb.AppendLine("                reader.Dispose();");
                sb.AppendLine("            }");
                break;
            case ClientProtocolKind.VersionedMemoryPack:
                sb.AppendLine("            if (!global::RhinoDB.Lib.Procedures.ProcedureEnvelope.TryReadMemoryPackObject(body, out var members))");
                sb.AppendLine("                throw new global::System.FormatException(\"Not a VersionTolerant MemoryPack object envelope.\");");
                foreach (var x in parameters)
                    sb.AppendLine($"            {x.Local} = members.Length > {x.Index} ? global::MemoryPack.MemoryPackSerializer.Deserialize<{x.TypeFullName}>(members[{x.Index}].Span)! : default!;");
                break;
            case ClientProtocolKind.MessagePack:
                sb.AppendLine("            var reader = new global::MessagePack.MessagePackReader(body);");
                sb.AppendLine("            var count = reader.ReadArrayHeader();");
                foreach (var x in parameters)
                    sb.AppendLine($"            {x.Local} = count > {x.Index} ? global::MessagePack.MessagePackSerializer.Deserialize<{x.TypeFullName}>(ref reader, global::MessagePack.MessagePackSerializer.DefaultOptions)! : default!;");
                break;
        }
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    static private void EmitResultCodec(StringBuilder sb, WireValue value, ClientProtocolKind protocol) {
        sb.AppendLine($"        public static byte[] EncodeResult({value.TypeFullName} value) {{");
        switch (protocol) {
            case ClientProtocolKind.Raw:
                EmitRawWriterOpen(sb);
                EmitIndented(sb, b => SchemaWalk.EmitWriteField(b, value.Field, "value"), "        ");
                EmitRawWriterClose(sb, "ToArray()");
                break;
            case ClientProtocolKind.VersionedMemoryPack:
                sb.AppendLine("            return global::MemoryPack.MemoryPackSerializer.Serialize(value);");
                break;
            case ClientProtocolKind.MessagePack:
                sb.AppendLine("            return global::MessagePack.MessagePackSerializer.Serialize(value);");
                break;
        }
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine($"        public static {value.TypeFullName} DecodeResult({Bytes} body) {{");
        switch (protocol) {
            case ClientProtocolKind.Raw:
                sb.AppendLine("            var reader = new global::MemoryPack.MemoryPackReader(body.Span, global::MemoryPack.MemoryPackReaderOptionalStatePool.Rent(null));");
                sb.AppendLine("            try {");
                EmitIndented(sb, b => SchemaWalk.EmitReadField(b, value.Field), "        ");
                sb.AppendLine($"                return {value.RawReadVariable};");
                sb.AppendLine("            } finally {");
                sb.AppendLine("                reader.Dispose();");
                sb.AppendLine("            }");
                break;
            case ClientProtocolKind.VersionedMemoryPack:
                sb.AppendLine($"            return global::MemoryPack.MemoryPackSerializer.Deserialize<{value.TypeFullName}>(body.Span)!;");
                break;
            case ClientProtocolKind.MessagePack:
                sb.AppendLine($"            return global::MessagePack.MessagePackSerializer.Deserialize<{value.TypeFullName}>(body);");
                break;
        }
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    static private void EmitRawWriterOpen(StringBuilder sb) {
        sb.AppendLine("            var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();");
        sb.AppendLine("            var writer = new global::MemoryPack.MemoryPackWriter<global::System.Buffers.ArrayBufferWriter<byte>>(ref buffer, global::MemoryPack.MemoryPackWriterOptionalStatePool.Rent(null));");
    }

    static private void EmitRawWriterClose(StringBuilder sb, string toResult) {
        sb.AppendLine("            writer.Flush();");
        sb.AppendLine($"            return buffer.WrittenSpan.{toResult};");
    }

    // SchemaWalk emits at 8 spaces (a class member body); procedure codecs sit one level deeper inside a nested class.
    static private void EmitIndented(StringBuilder sb, System.Action<StringBuilder> emit, string extraIndent) {
        var inner = new StringBuilder();
        emit(inner);
        foreach (var line in inner.ToString().Split('\n')) {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0) sb.AppendLine(extraIndent + trimmed);
        }
    }

    static private string Literal(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private enum ProcedureShape { General, Transaction }

    private sealed class DatabaseTarget(string fullName, string simpleName, string? rootFullName, bool isKeyedChild) {
        public string FullName { get; } = fullName;
        public string SimpleName { get; } = simpleName;
        public string? RootFullName { get; } = rootFullName;
        public bool IsKeyedChild { get; } = isKeyedChild;
    }

    private sealed class WireValue(string typeFullName, RowFieldKind kind, bool isCustomType, bool hasMemoryPackable, bool hasMessagePackObject, string what) {
        public string TypeFullName { get; } = typeFullName;
        public RowFieldKind Kind { get; } = kind;
        public bool IsCustomType { get; } = isCustomType;
        public bool HasMemoryPackable { get; } = hasMemoryPackable;
        public bool HasMessagePackObject { get; } = hasMessagePackObject;
        public string What { get; } = what;
        public int Index { get; private set; } = -1;

        // Positional names keep user parameter names (which may be C# keywords or clash with ours) out of generated code.
        public string Local => Index < 0 ? "value" : $"p{Index}";
        public string TupleName => $"P{Index}";
        private string FieldName => Index < 0 ? "Value" : $"P{Index}";
        public RowFieldModel Field => new RowFieldModel(FieldName, TypeFullName, Kind, IsCustomType);
        public string RawReadVariable => char.ToLowerInvariant(FieldName[0]) + FieldName.Substring(1) + "Value";

        public WireValue WithIndex(int index) {
            Index = index;
            return this;
        }
    }

    private sealed class ProcedureModel(string name, uint hash, string methodReference, Location location, string display) {
        public string Name { get; } = name;
        public uint Hash { get; } = hash;
        public string MethodReference { get; } = methodReference;
        public Location Location { get; } = location;
        public string Display { get; } = display;
        public ProcedureShape Shape { get; set; }
        public bool TakesCancellationToken { get; set; }
        public bool ReturnsValueTask { get; set; }
        public string? TxDatabaseSimpleName { get; set; }
        public string? TxCtxDisplay { get; set; }
        public DatabaseTarget? TxDatabase { get; set; }
        public WireValue? Value { get; set; }
        public List<WireValue> Parameters { get; } = [];
        public List<Diagnostic> Diagnostics { get; } = [];

        // A C# identifier unique per method: the fully-qualified reference with separators flattened.
        public string ClassName => MethodReference.Replace("global::", "").Replace(".", "_");

        public IEnumerable<WireValue> AllValues() => Value is null ? Parameters : Parameters.Concat([Value]);

        public ProcedureModel Fail(Diagnostic diagnostic) {
            Diagnostics.Add(diagnostic);
            return this;
        }
    }
}
