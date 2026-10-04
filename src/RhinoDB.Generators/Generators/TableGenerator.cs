using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RhinoDB.SchemaContracts;
using RhinoDB.Core.Tables;

namespace RhinoDB.Generators;

[Generator]
public sealed class TableGenerator : IIncrementalGenerator {
    private const string TableAttributeFullName = "RhinoDB.Core.Tables.TableAttribute";
    private const string PrimaryKeyAttributeFullName = "RhinoDB.Core.Tables.PrimaryKeyAttribute";
    private const string AutoIncrementAttributeFullName = "RhinoDB.Core.Tables.AutoIncrementAttribute";
    private const string IndexAttributeFullName = "RhinoDB.Core.Tables.IndexAttribute";
    private const string ValidateAttributeFullName = "RhinoDB.Core.Tables.ValidateAttribute";
    private const string MigrationAttributeFullName = "RhinoDB.Core.Tables.MigrationAttribute";
    private const string ClientDowngradeAttributeFullName = "RhinoDB.Core.Tables.ClientDowngradeAttribute";
    private const string InvalidRevisionsAttributeFullName = "RhinoDB.Core.Tables.InvalidRevisionsAttribute";
    private const string InvalidClientAppVersionsAttributeFullName = "RhinoDB.Core.Tables.InvalidClientAppVersionsAttribute";
    private const string DbErrorFullName = "RhinoDB.Core.DbError";

    static private readonly DiagnosticDescriptor MissingPrimaryKeyDiagnostic = new DiagnosticDescriptor(
        "RHINO001",
        "Table row missing [PrimaryKey]",
        "Row type '{0}' is [Table]-attributed but declares no [PrimaryKey] parameter",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor EmptyAccessorDiagnostic = new DiagnosticDescriptor(
        "RHINO002",
        "Empty Accessor name",
        "{0} has an explicit Accessor that is an empty string - omit Accessor for the default name, or give it a real one",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor CompositeIndexKindMismatchDiagnostic = new DiagnosticDescriptor(
        "RHINO003",
        "Composite index fields disagree on Kind/Uniqueness",
        "Fields sharing Accessor '{0}' on '{1}' must all declare the same IndexKind and Uniqueness",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor CompositeIndexTooManyFieldsDiagnostic = new DiagnosticDescriptor(
        "RHINO004",
        "Composite index has too many fields",
        "Composite index '{0}' on '{1}' has {2} fields sharing one Accessor - at most 3 are supported",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor DuplicateOrderDiagnostic = new DiagnosticDescriptor(
        "RHINO005",
        "Duplicate explicit Order in composite index",
        "Composite index '{0}' on '{1}' has two or more fields with the same explicit Order value",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor InvalidAutoIncrementTypeDiagnostic = new DiagnosticDescriptor(
        "RHINO007",
        "AutoIncrement field must be an incrementable unmanaged integer type",
        "'{0}.{1}' is [AutoIncrement] but its type isn't one of sbyte/byte/short/ushort/int/uint/long/ulong - " + "AutoIncrement needs a type the system can generate a new value for",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor EvictableOnInstantKindDiagnostic = new DiagnosticDescriptor(
        "RHINO008",
        "Evictable has no effect on Instant-kind tables",
        "'{0}' is TableKind.Instant and sets Evictable = true - Instant-kind tables have no cold storage " + "to evict to/from at all. Remove Evictable or use TableKind.Persistent.",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor InvalidValidateMethodSignatureDiagnostic = new DiagnosticDescriptor(
        "RHINO009",
        "Invalid [Validate] method signature",
        "'{0}.{1}' is [Validate] but must be an at-least-internal static method shaped 'static DbError? {1}({0} row)' - "
        + "generated code calls it from a sibling class in the same assembly, so it can't be private",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor TableIdCollisionDiagnostic = new DiagnosticDescriptor(
        "RHINO011",
        "Table id hash collision within one database",
        "{0} has two tables whose Accessors hash to the same tableId: {1} and {2} - rename one Accessor, since the tableId (FNV-1a of the Accessor) identifies tables in the WAL and in cold storage",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    static private readonly DiagnosticDescriptor DuplicateIndexAccessorOnFieldDiagnostic = new DiagnosticDescriptor(
        "RHINO012",
        "Duplicate [Index] accessor on one field",
        "{0}.{1} declares two or more [Index] attributes that resolve to the same Accessor '{2}' - each index on a field needs its own Accessor",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    static private readonly DiagnosticDescriptor ReservedIndexAccessorDiagnostic = new DiagnosticDescriptor(
        "RHINO013",
        "Index accessor collides with a generated member",
        "Index '{0}' on '{1}' would generate a member with the same name as the generated ops API - '{0}' is taken by Primary or by Insert/Update/Delete/Iter/Evict, so give this index a different Accessor",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor IndexAccessorFieldNameCollisionDiagnostic = new DiagnosticDescriptor(
        "RHINO014",
        "Index accessors collide on the generated field name",
        "Index accessors '{0}' and '{1}' on '{2}' differ only in the casing of their first letter, so they would both generate the field name '{3}Index' - rename one Accessor",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    static private readonly DiagnosticDescriptor DuplicateAccessorDiagnostic = new DiagnosticDescriptor(
        "RHINO010",
        "Duplicate table Accessor within one database and TableKind",
        "'{0}' has two or more tables of the same TableKind using Accessor '{1}' - each table's Accessor must be "
        + "unique within its owning database AND TableKind, since TableKind.Instant and TableKind.Persistent "
        + "tables surface under separate accessors (Db.Instant.X vs Db.X) and so don't collide with each other",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor MissingSerializationAttributesDiagnostic = new DiagnosticDescriptor(
        "RHINO015",
        "Table row missing mandatory client-protocol serialization attribute",
        "Row type '{0}' is [Table]-attributed but is missing {1} - this project's ClientProtocol "
        + "(rdbsettings.json's Generator section) requires it on every table row ([MemoryPackable] needs "
        + "GenerateType.VersionTolerant if the row has any reference-typed field - MemoryPack rejects "
        + "VersionTolerant on a fully-unmanaged struct), so client access always has a real, "
        + "source-generated formatter available for whichever protocol this project chose",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor InvalidMigrationMethodSignatureDiagnostic = new DiagnosticDescriptor(
        "RHINO018",
        "Invalid [Migration] method signature",
        "'{0}.{1}' is [Migration(FromRevision=N)] but must be an at-least-internal static method taking "
        + "exactly one parameter and returning a non-void type - it transforms one revision's row shape "
        + "into the next revision's shape",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor MigrationChainGapDiagnostic = new DiagnosticDescriptor(
        "RHINO020",
        "[Migration] chain has an internal gap",
        "'{0}' has [Migration(FromRevision=N)] methods registered for revisions {1}, but revision {2} is "
        + "missing in between - the chain must be hole-free (N -> N+1 -> N+2 -> ...), or a database still "
        + "behind on an older revision would have no way to bridge across the gap",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor MissingMigrationForBreakingChangeDiagnostic = new DiagnosticDescriptor(
        "RHINO019",
        "Breaking schema change has no matching [Migration]",
        "'{0}.{1}' changed in a way that breaks Raw decoding of its already-persisted rows (a field "
        + "added/removed/reordered/retyped, primary key type changed, or table kind changed), but no "
        + "[Migration(FromRevision = {2})] was found on '{3}' to bridge from the previously-committed "
        + "shape - register one, or if this is genuinely a pure trailing-field append, double check nothing "
        + "in the middle actually changed",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor InvalidGenerationValueDiagnostic = new DiagnosticDescriptor(
        "RHINO021",
        "[Database(InvalidGenerations=)] contains a nonsensical generation number",
        "'{0}' declares InvalidGenerations containing {1}, which is <= 0 - generation 0 is a database's "
        + "starting point (nothing preceded it to have failed a migration into it), so it can never be a "
        + "genuinely-invalidated past generation",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor MalformedDescriptorDiagnostic = new DiagnosticDescriptor(
        "RHINO023",
        "Descriptor.json is malformed",
        "The committed Descriptor.json additional file failed to parse: {0} - fix or regenerate it; until "
        + "then it's treated as absent, so RHINO019/020 and G_binary fall back to their no-descriptor "
        + "defaults rather than blocking the build outright",
        "RhinoDB.Generators",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor InvalidDowngradeMethodSignatureDiagnostic = new DiagnosticDescriptor(
        "RHINO026",
        "Invalid [ClientDowngrade] method signature",
        "'{0}.{1}' is [ClientDowngrade(ToRevision=N)] but must be an at-least-internal static method taking "
        + "exactly one parameter and returning a non-void type - it transforms one revision's row shape "
        + "into the previous revision's shape, the reverse direction of [Migration]",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor InvalidRevisionValueDiagnostic = new DiagnosticDescriptor(
        "RHINO027",
        "[InvalidRevisions(Revisions=)] contains a nonsensical revision number",
        "'{0}' declares InvalidRevisions containing {1}, which is <= 0 - revision 0 is a row type's "
        + "starting shape (nothing preceded it to have failed a migration into it), so it can never be a "
        + "genuinely-invalidated past revision",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor MixedExplicitAndOmittedDatabaseDiagnostic = new DiagnosticDescriptor(
        "RHINO028",
        "[Table] mixes an explicit typeof(...) with an omitted one on the same row",
        "'{0}' has both a [Table] attribute naming an explicit database and one that omits it - omitting "
        + "typeof(...) means \"build this table for every declared [Database],\" which no longer makes sense "
        + "once another attribute on the same row already pins one specific database. Either give every "
        + "attribute an explicit typeof(...), or omit it on all of them.",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor NoDatabaseFoundDiagnostic = new DiagnosticDescriptor(
        "RHINO029",
        "[Table] omits the database type and no [Database] type was found",
        "'{0}' is [Table]-attributed without specifying typeof(...) for its database, and this compilation "
        + "declares no [Database] type to infer it from - specify one explicitly, e.g. [Table(TableKind.X, typeof(YourDb))]",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor MalformedGeneratorConfigDiagnostic = new DiagnosticDescriptor(
        "RHINO024",
        "rdbsettings.json is malformed",
        "The committed rdbsettings.json additional file failed to parse: {0} - fix or regenerate it; "
        + "until then ClientProtocol falls back to its default (Raw) rather than blocking the build outright",
        "RhinoDB.Generators",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    static private readonly ImmutableHashSet<string> ReservedOpsMemberNames =
        ImmutableHashSet.Create("Insert", "Update", "Delete", "Iter", "Evict", "Primary");

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var configResult = context.AdditionalTextsProvider
            .Where(static t => Path.GetFileName(t.Path) == "rdbsettings.json")
            .Collect()
            .Select(static (texts, ct) => {
                if (texts.Length == 0) return (Protocol: ClientProtocolKind.Raw, ParseError: (Diagnostic?)null);
                var text = texts[0].GetText(ct)?.ToString();
                if (string.IsNullOrEmpty(text)) return (Protocol: ClientProtocolKind.Raw, ParseError: (Diagnostic?)null);
                try {
                    var parsedConfig = GeneratorConfigLoader.Parse(text!);
                    return (Protocol: ClientProtocolParser.Parse(parsedConfig.ClientProtocol), ParseError: (Diagnostic?)null);
                } catch (Exception ex) {
                    return (Protocol: ClientProtocolKind.Raw, ParseError: Diagnostic.Create(MalformedGeneratorConfigDiagnostic, Location.None, ex.Message));
                }
            });

        var configProtocol = configResult.Select(static (r, _) => r.Protocol);

        var databases = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                DatabaseDiscovery.DatabaseAttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => DatabaseDiscovery.ToDatabaseModel(ctx)
            );

        var declaredDatabaseFullNames = databases.Collect()
            .Select(static (dbs, _) => dbs.Select(d => d.FullName).ToImmutableArray());

        var tableResults = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                TableAttributeFullName,
                predicate: static (node, _) => node is StructDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (ctx, _) => ctx
            )
            .Combine(configProtocol)
            .Combine(declaredDatabaseFullNames)
            .Select(static (pair, _) => ToTableModels(pair.Left.Left, pair.Left.Right, pair.Right))
            .SelectMany(static (results, _) => results);

        context.RegisterSourceOutput(
            tableResults,
            static (spc, result) => {
                foreach (var diagnostic in result.Diagnostics) spc.ReportDiagnostic(diagnostic);
            }
        );

        var tables = tableResults.Collect();

        var descriptor = context.AdditionalTextsProvider
            .Where(static t => Path.GetFileName(t.Path) == "Descriptor.json")
            .Collect()
            .Select(static (texts, ct) => {
                if (texts.Length == 0) return (Parsed: (DatabaseContractDescriptor?)null, ParseError: (Diagnostic?)null);
                var text = texts[0].GetText(ct)?.ToString();
                if (string.IsNullOrEmpty(text)) return (Parsed: (DatabaseContractDescriptor?)null, ParseError: (Diagnostic?)null);
                try {
                    return (Parsed: ContractDescriptorJson.Parse(text!), ParseError: (Diagnostic?)null);
                } catch (Exception ex) {
                    return (Parsed: (DatabaseContractDescriptor?)null, ParseError: Diagnostic.Create(MalformedDescriptorDiagnostic, Location.None, ex.Message));
                }
            });

        var combined = databases.Combine(tables).Combine(descriptor).Combine(configResult);
        context.RegisterSourceOutput(combined, static (spc, pair) => {
            if (pair.Left.Right.ParseError is { } descriptorDiagnostic) spc.ReportDiagnostic(descriptorDiagnostic);
            if (pair.Right.ParseError is { } configDiagnostic) spc.ReportDiagnostic(configDiagnostic);
            Emit(spc, pair.Left.Left.Left, pair.Left.Left.Right, pair.Left.Right.Parsed, pair.Right.Protocol);
        });

        var clientAppVersionRules = context.CompilationProvider.Select(static (compilation, _) => {
            var attribute = compilation.Assembly.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == InvalidClientAppVersionsAttributeFullName);
            return attribute is null
                ? (Whitelist: ImmutableArray<uint>.Empty, LessThanOrEqualTo: ImmutableArray<uint>.Empty, NotEqualTo: ImmutableArray<uint>.Empty)
                : (
                    Whitelist: UIntArrayNamedArg(attribute, "Whitelist"),
                    LessThanOrEqualTo: UIntArrayNamedArg(attribute, "LessThanOrEqualTo"),
                    NotEqualTo: UIntArrayNamedArg(attribute, "NotEqualTo")
                );
        });
        context.RegisterSourceOutput(clientAppVersionRules, static (spc, rules) => {
            spc.AddSource("RhinoClientCompat.g.cs", EmitClientCompatHolder(rules));
        });
    }

    static private string EmitClientCompatHolder((ImmutableArray<uint> Whitelist, ImmutableArray<uint> LessThanOrEqualTo, ImmutableArray<uint> NotEqualTo) rules) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine();
        sb.AppendLine("public static class RhinoClientCompat {");
        sb.AppendLine($"    private static readonly HashSet<uint> Whitelist = new HashSet<uint> {{ {string.Join(", ", rules.Whitelist.Select(v => $"{v}u"))} }};");
        sb.AppendLine($"    private static readonly uint[] LessThanOrEqualTo = new uint[] {{ {string.Join(", ", rules.LessThanOrEqualTo.Select(v => $"{v}u"))} }};");
        sb.AppendLine($"    private static readonly HashSet<uint> NotEqualTo = new HashSet<uint> {{ {string.Join(", ", rules.NotEqualTo.Select(v => $"{v}u"))} }};");
        sb.AppendLine("    public static bool IsAppVersionInvalid(uint version) {");
        sb.AppendLine("        if (Whitelist.Contains(version)) return false;");
        sb.AppendLine("        if (LessThanOrEqualTo.Any(floor => version <= floor)) return true;");
        sb.AppendLine("        return NotEqualTo.Count > 0 && !NotEqualTo.Contains(version);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    static private (bool Provided, string? Value) StringNamedArg(AttributeData attr, string name) {
        foreach (var kv in attr.NamedArguments)
            if (kv.Key == name)
                return (true, (string?)kv.Value.Value);
        return (false, null);
    }

    static private (bool Provided, int Value) IntNamedArg(AttributeData attr, string name) {
        foreach (var kv in attr.NamedArguments)
            if (kv.Key == name)
                return (true, (int)kv.Value.Value!);
        return (false, 0);
    }

    static private ImmutableArray<int> IntArrayNamedArg(AttributeData attr, string name) {
        foreach (var kv in attr.NamedArguments)
            if (kv.Key == name)
                return kv.Value.IsNull ? ImmutableArray<int>.Empty : kv.Value.Values.Select(v => (int)v.Value!).ToImmutableArray();
        return ImmutableArray<int>.Empty;
    }

    static private ImmutableArray<uint> UIntArrayNamedArg(AttributeData attr, string name) {
        foreach (var kv in attr.NamedArguments)
            if (kv.Key == name)
                return kv.Value.IsNull ? ImmutableArray<uint>.Empty : kv.Value.Values.Select(v => (uint)v.Value!).ToImmutableArray();
        return ImmutableArray<uint>.Empty;
    }

    static private bool BoolNamedArg(AttributeData attr, string name, bool defaultValue = false) {
        foreach (var kv in attr.NamedArguments)
            if (kv.Key == name)
                return (bool)kv.Value.Value!;
        return defaultValue;
    }

    static private Location Loc(AttributeData attr) => attr.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? Location.None;

    static private bool? ClassifyAutoIncrementType(ITypeSymbol type) => type.SpecialType switch {
        SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64 => true,
        SpecialType.System_Byte or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64 => false,
        _ => null,
    };

    static private ImmutableArray<(TableModel? Model, ImmutableArray<Diagnostic> Diagnostics)> ToTableModels(
        GeneratorAttributeSyntaxContext ctx, ClientProtocolKind clientProtocol, ImmutableArray<string> declaredDatabaseFullNames) {
        var rowDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var rowType = (INamedTypeSymbol)ctx.TargetSymbol;

        var primaryCtor = rowType.InstanceConstructors.FirstOrDefault(c =>
            c.Parameters.Length > 0 && !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, rowType))
        );

        var primaryKeyParam = primaryCtor?.Parameters.FirstOrDefault(p =>
            p.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == PrimaryKeyAttributeFullName)
        );
        if (primaryCtor is null || primaryKeyParam is null) {
            rowDiagnostics.Add(Diagnostic.Create(MissingPrimaryKeyDiagnostic, ctx.TargetNode.GetLocation(), rowType.Name));
            return ImmutableArray.Create<(TableModel?, ImmutableArray<Diagnostic>)>((null, rowDiagnostics.ToImmutable()));
        }

        var missingSerializationAttrs = ImmutableArray.CreateBuilder<string>();
        if (clientProtocol == ClientProtocolKind.VersionedMemoryPack && !SchemaWalk.HasMemoryPackable(rowType)) missingSerializationAttrs.Add("[MemoryPackable]");
        if (clientProtocol == ClientProtocolKind.MessagePack && !SchemaWalk.HasMessagePackObject(rowType)) missingSerializationAttrs.Add("[MessagePackObject]");
        if (missingSerializationAttrs.Count > 0) {
            rowDiagnostics.Add(Diagnostic.Create(
                MissingSerializationAttributesDiagnostic, ctx.TargetNode.GetLocation(), rowType.Name, string.Join(" and ", missingSerializationAttrs)));
            return ImmutableArray.Create<(TableModel?, ImmutableArray<Diagnostic>)>((null, rowDiagnostics.ToImmutable()));
        }

        rowDiagnostics.AddRange(SchemaWalk.CheckOtherKindFieldAttributes(primaryCtor.Parameters, rowType.Name, ctx.TargetNode.GetLocation(), clientProtocol));
        if (rowDiagnostics.Count > 0) return ImmutableArray.Create<(TableModel?, ImmutableArray<Diagnostic>)>((null, rowDiagnostics.ToImmutable()));

        var primaryKeyAttribute = primaryKeyParam.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == PrimaryKeyAttributeFullName);
        var primaryKeyKind = primaryKeyAttribute.ConstructorArguments.Length > 0
            ? (IndexKind)(int)primaryKeyAttribute.ConstructorArguments[0].Value!
            : IndexKind.Hash;

        var rowFields = SchemaWalk.ToRowFieldModels(primaryCtor.Parameters);

        var autoIncrementFields = ImmutableArray.CreateBuilder<AutoIncrementFieldModel>();
        foreach (var p in primaryCtor.Parameters) {
            var autoIncrementAttribute = p.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AutoIncrementAttributeFullName);
            if (autoIncrementAttribute is null) continue;

            var signed = ClassifyAutoIncrementType(p.Type);
            if (signed is null) {
                rowDiagnostics.Add(Diagnostic.Create(InvalidAutoIncrementTypeDiagnostic, Loc(autoIncrementAttribute), rowType.Name, p.Name));
                continue;
            }

            autoIncrementFields.Add(new AutoIncrementFieldModel(p.Name, p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), signed.Value));
        }

        var indexedParams = primaryCtor.Parameters
            .Select((p, declIndex) => (Param: p, DeclIndex: declIndex))
            .SelectMany(x => x.Param.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == IndexAttributeFullName)
                .Select(indexAttribute => {
                    var indexKind = (IndexKind)(int)indexAttribute.ConstructorArguments[0].Value!;
                    var uniqueness = indexAttribute.ConstructorArguments.Length > 1
                        ? (Uniqueness)(int)indexAttribute.ConstructorArguments[1].Value!
                        : Uniqueness.NonUnique;
                    var (accessorProvided, accessorValue) = StringNamedArg(indexAttribute, "Accessor");
                    if (accessorProvided && accessorValue == "")
                        rowDiagnostics.Add(Diagnostic.Create(EmptyAccessorDiagnostic, Loc(indexAttribute), $"[Index] on '{rowType.Name}.{x.Param.Name}'"));
                    var accessor = accessorProvided && accessorValue != "" ? accessorValue! : x.Param.Name;
                    var order = indexAttribute.NamedArguments
                        .Where(kv => kv.Key == "Order")
                        .Select(kv => (int)kv.Value.Value!)
                        .DefaultIfEmpty(-1)
                        .First();
                    return (x.Param, x.DeclIndex, Kind: indexKind, Uniqueness: uniqueness, Accessor: accessor, Order: order, Attr: indexAttribute);
                })
            )
            .ToImmutableArray();

        foreach (var camelGroup in indexedParams.GroupBy(x => Camel(x.Accessor))) {
            var distinctAccessors = camelGroup.Select(x => x.Accessor).Distinct().ToImmutableArray();
            if (distinctAccessors.Length < 2) continue;
            rowDiagnostics.Add(Diagnostic.Create(IndexAccessorFieldNameCollisionDiagnostic, Loc(camelGroup.First().Attr), distinctAccessors[0], distinctAccessors[1], rowType.Name, Camel(distinctAccessors[0])));
        }
        
        var indexes = indexedParams
            .GroupBy(x => x.Accessor)
            .Select(g => {
                    var group = g.ToImmutableArray();

                    if (ReservedOpsMemberNames.Contains(g.Key)) {
                        rowDiagnostics.Add(Diagnostic.Create(ReservedIndexAccessorDiagnostic, Loc(group[0].Attr), g.Key, rowType.Name));
                        return null;
                    }
                    if (group.Select(x => x.Param.Name).Distinct().Count() != group.Length) {
                        rowDiagnostics.Add(Diagnostic.Create(DuplicateIndexAccessorOnFieldDiagnostic, Loc(group[0].Attr), rowType.Name, group[0].Param.Name, g.Key));
                        return null;
                    }

                    if (group.Length > 3) {
                        rowDiagnostics.Add(Diagnostic.Create(CompositeIndexTooManyFieldsDiagnostic, Loc(group[0].Attr), g.Key, rowType.Name, group.Length));
                        return null;
                    }

                    if (group.Any(x => x.Kind != group[0].Kind || x.Uniqueness != group[0].Uniqueness)) {
                        rowDiagnostics.Add(Diagnostic.Create(CompositeIndexKindMismatchDiagnostic, Loc(group[0].Attr), g.Key, rowType.Name));
                        return null;
                    }

                    var explicitOrders = group.Where(x => x.Order != -1).Select(x => x.Order).ToImmutableArray();
                    if (explicitOrders.Length != explicitOrders.Distinct().Count()) {
                        rowDiagnostics.Add(Diagnostic.Create(DuplicateOrderDiagnostic, Loc(group[0].Attr), g.Key, rowType.Name));
                        return null;
                    }

                    var ordered = group.OrderBy(x => x.Order == -1 ? x.DeclIndex : x.Order).ToImmutableArray();
                    var fields = ordered
                        .Select(x => new IndexFieldModel(x.Param.Name, x.Param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
                        .ToImmutableArray();
                    return new IndexModel(g.Key, ordered[0].Kind, ordered[0].Uniqueness, fields);
                }
            )
            .Where(m => m is not null)
            .Select(m => m!)
            .ToImmutableArray();

        var validateMethodNames = ImmutableArray.CreateBuilder<string>();
        foreach (var member in rowType.GetMembers().OfType<IMethodSymbol>()) {
            if (!member.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == ValidateAttributeFullName)) continue;

            var validSignature = member.IsStatic
                                 && member.DeclaredAccessibility != Accessibility.Private
                                 && member.Parameters.Length == 1
                                 && SymbolEqualityComparer.Default.Equals(member.Parameters[0].Type, rowType)
                                 && member.ReturnType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } returnType
                                 && returnType.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{DbErrorFullName}";

            if (!validSignature) {
                rowDiagnostics.Add(Diagnostic.Create(InvalidValidateMethodSignatureDiagnostic, member.Locations.FirstOrDefault() ?? Location.None, rowType.Name, member.Name));
                continue;
            }

            validateMethodNames.Add(member.Name);
        }

        var migrationMethods = ImmutableArray.CreateBuilder<MigrationMethodModel>();
        foreach (var member in rowType.GetMembers().OfType<IMethodSymbol>()) {
            var migrationAttribute = member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MigrationAttributeFullName);
            if (migrationAttribute is null) continue;

            var validSignature = member.IsStatic
                                 && member.DeclaredAccessibility != Accessibility.Private
                                 && member.Parameters.Length == 1
                                 && member.ReturnType.SpecialType != SpecialType.System_Void;

            if (!validSignature) {
                rowDiagnostics.Add(Diagnostic.Create(InvalidMigrationMethodSignatureDiagnostic, member.Locations.FirstOrDefault() ?? Location.None, rowType.Name, member.Name));
                continue;
            }

            var fromRevision = (int)migrationAttribute.ConstructorArguments[0].Value!;
            migrationMethods.Add(new MigrationMethodModel(
                fromRevision,
                member.Name,
                member.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                member.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            ));
        }

        var sortedRevisions = migrationMethods.Select(m => m.FromRevision).Distinct().OrderBy(r => r).ToImmutableArray();
        for (var i = 1; i < sortedRevisions.Length; i++) {
            if (sortedRevisions[i] - sortedRevisions[i - 1] == 1) continue;
            for (var missing = sortedRevisions[i - 1] + 1; missing < sortedRevisions[i]; missing++)
                rowDiagnostics.Add(Diagnostic.Create(
                    MigrationChainGapDiagnostic, ctx.TargetNode.GetLocation(), rowType.Name, string.Join(", ", sortedRevisions), missing));
        }

        var downgradeMethods = ImmutableArray.CreateBuilder<DowngradeMethodModel>();
        foreach (var member in rowType.GetMembers().OfType<IMethodSymbol>()) {
            var downgradeAttribute = member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == ClientDowngradeAttributeFullName);
            if (downgradeAttribute is null) continue;

            var validSignature = member.IsStatic
                                 && member.DeclaredAccessibility != Accessibility.Private
                                 && member.Parameters.Length == 1
                                 && member.ReturnType.SpecialType != SpecialType.System_Void;

            if (!validSignature) {
                rowDiagnostics.Add(Diagnostic.Create(InvalidDowngradeMethodSignatureDiagnostic, member.Locations.FirstOrDefault() ?? Location.None, rowType.Name, member.Name));
                continue;
            }

            var toRevision = (int)downgradeAttribute.ConstructorArguments[0].Value!;
            downgradeMethods.Add(new DowngradeMethodModel(
                toRevision,
                member.Name,
                member.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                member.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            ));
        }

        // [InvalidRevisions(Revisions=)] - row-type-scoped sibling of [Database(InvalidGenerations=)],
        // resolved directly off the attribute at compile time (no Descriptor.json round-trip needed, exactly
        // like InvalidGenerations doesn't need one either).
        var invalidRevisionsAttribute = rowType.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == InvalidRevisionsAttributeFullName);
        var invalidRevisions = invalidRevisionsAttribute is null
            ? ImmutableArray<int>.Empty
            : IntArrayNamedArg(invalidRevisionsAttribute, "Revisions");
        foreach (var revision in invalidRevisions.Where(r => r <= 0))
            rowDiagnostics.Add(Diagnostic.Create(InvalidRevisionValueDiagnostic, ctx.TargetNode.GetLocation(), rowType.Name, revision));

        var descriptorFields = DescriptorBuilder.FlattenFields(primaryCtor.Parameters);

        if (rowDiagnostics.Count > 0) return [(null, rowDiagnostics.ToImmutable())];

        var hasExplicitDatabase = ctx.Attributes.Any(a => a.ConstructorArguments[1].Value is INamedTypeSymbol);
        var hasOmittedDatabase = ctx.Attributes.Any(a => a.ConstructorArguments[1].Value is not INamedTypeSymbol);
        if (hasExplicitDatabase && hasOmittedDatabase) {
            return [
                (null, ImmutableArray.Create(Diagnostic.Create(MixedExplicitAndOmittedDatabaseDiagnostic, ctx.TargetNode.GetLocation(), rowType.Name)))
            ];
        }

        var expanded = ImmutableArray.CreateBuilder<(AttributeData Attribute, string OwnerDatabaseFullName)>();
        foreach (var attribute in ctx.Attributes) {
            if (attribute.ConstructorArguments[1].Value is INamedTypeSymbol explicitDatabaseType) {
                expanded.Add((attribute, explicitDatabaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
                continue;
            }

            if (declaredDatabaseFullNames.Length == 0) {
                return [
                    (null, ImmutableArray.Create(Diagnostic.Create(NoDatabaseFoundDiagnostic, Loc(attribute), rowType.Name)))
                ];
            }

            foreach (var databaseFullName in declaredDatabaseFullNames)
                expanded.Add((attribute, databaseFullName));
        }

        var results = ImmutableArray.CreateBuilder<(TableModel? Model, ImmutableArray<Diagnostic> Diagnostics)>();
        foreach (var (attribute, ownerDatabaseFullName) in expanded) {
            var attrDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

            var kind = (TableKind)(int)attribute.ConstructorArguments[0].Value!;

            var (tableAccessorProvided, tableAccessorValue) = StringNamedArg(attribute, "Accessor");
            if (tableAccessorProvided && tableAccessorValue == "")
                attrDiagnostics.Add(Diagnostic.Create(EmptyAccessorDiagnostic, Loc(attribute), $"[Table] on '{rowType.Name}'"));
            var tableAccessor = tableAccessorProvided && tableAccessorValue != "" ? tableAccessorValue! : rowType.Name;

            var (chunkSizeProvided, chunkSizeValue) = IntNamedArg(attribute, "ChunkSize");
            var chunkSize = chunkSizeProvided ? chunkSizeValue : 4096;

            var evictable = BoolNamedArg(attribute, "Evictable");
            if (evictable && kind == TableKind.Instant)
                attrDiagnostics.Add(Diagnostic.Create(EvictableOnInstantKindDiagnostic, Loc(attribute), rowType.Name));

            var (ringBufferCapacityProvided, ringBufferCapacityValue) = IntNamedArg(attribute, "RingBufferCapacity");
            var ringBufferCapacity = ringBufferCapacityProvided ? ringBufferCapacityValue : 0;

            if (attrDiagnostics.Count > 0) {
                results.Add((null, attrDiagnostics.ToImmutable()));
                continue;
            }

            var model = new TableModel(
                rowType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                rowType.ContainingNamespace.IsGlobalNamespace ? null : rowType.ContainingNamespace.ToDisplayString(),
                kind,
                ownerDatabaseFullName,
                primaryKeyParam.Name,
                primaryKeyParam.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                primaryKeyKind,
                tableAccessor,
                chunkSize,
                evictable,
                ringBufferCapacity,
                autoIncrementFields.ToImmutable(),
                indexes,
                validateMethodNames.ToImmutable(),
                migrationMethods.ToImmutable(),
                downgradeMethods.ToImmutable(),
                invalidRevisions,
                descriptorFields,
                rowFields
            );
            results.Add((model, ImmutableArray<Diagnostic>.Empty));
        }
        return results.ToImmutable();
    }

    static private void Emit(
        SourceProductionContext context, DatabaseModel database,
        ImmutableArray<(TableModel? Model, ImmutableArray<Diagnostic> Diagnostics)> allResults, DatabaseContractDescriptor? descriptor,
        ClientProtocolKind clientProtocol
    ) {
        var tables = allResults
            .Select(r => r.Model)
            .Where(m => m is not null)
            .Select(m => m!)
            .Where(t => t.OwnerDatabaseFullName == database.FullName)
            .ToImmutableArray();

        var nonsensicalInvalidGenerations = database.InvalidGenerations.Where(g => g <= 0).ToImmutableArray();
        if (nonsensicalInvalidGenerations.Length > 0) {
            foreach (var generation in nonsensicalInvalidGenerations)
                context.ReportDiagnostic(Diagnostic.Create(InvalidGenerationValueDiagnostic, Location.None, database.SimpleName, generation));
            return;
        }

        var duplicateAccessors = tables.GroupBy(t => (t.Kind, t.Accessor)).Where(g => g.Count() > 1).Select(g => g.Key.Accessor).ToImmutableArray();
        if (duplicateAccessors.Length > 0) {
            foreach (var accessor in duplicateAccessors)
                context.ReportDiagnostic(Diagnostic.Create(DuplicateAccessorDiagnostic, Location.None, database.SimpleName, accessor));
            return;
        }

        var collidingTableIds = tables.GroupBy(t => (t.Kind, Id: ComputeTableId(t.Accessor))).Where(g => g.Count() > 1).ToImmutableArray();
        if (collidingTableIds.Length > 0) {
            foreach (var group in collidingTableIds) {
                var colliding = group.Select(t => t.Accessor).ToImmutableArray();
                context.ReportDiagnostic(Diagnostic.Create(TableIdCollisionDiagnostic, Location.None, database.SimpleName, colliding[0], colliding[1]));
            }
            return;
        }

        CheckBreakingChangesHaveMigrations(context, database, tables, descriptor);

        foreach (var table in tables)
            context.AddSource($"{database.SimpleName}{table.Accessor}Ops.g.cs", EmitOpsClass(table, database, descriptor, clientProtocol));

        context.AddSource($"{database.SimpleName}.g.cs", EmitDatabase(database, tables, descriptor));
    }

    static private void CheckBreakingChangesHaveMigrations(
        SourceProductionContext context, DatabaseModel database, ImmutableArray<TableModel> tables, DatabaseContractDescriptor? descriptor
    ) {
        if (descriptor is null) return;

        foreach (var table in tables) {
            if (table.Kind != TableKind.Persistent) continue;

            var oldTable = descriptor.Tables.FirstOrDefault(t => t.DatabaseFullName == database.FullName && t.Accessor == table.Accessor);
            if (oldTable is null) continue;

            var newTable = new TableDescriptor {
                DatabaseFullName = database.FullName,
                Accessor = table.Accessor,
                RowTypeFullName = table.RowTypeFullName,
                Kind = "Persistent",
                PrimaryKey = new FieldDescriptor { Path = table.PrimaryKeyName, TypeFullName = table.PrimaryKeyTypeFullName },
                Fields = table.DescriptorFields,
            };

            if (ContractDiff.Diff(oldTable, newTable) != DiffClassification.Breaking) continue;
            if (table.MigrationMethods.Any(m => m.FromRevision == oldTable.Revision)) continue;

            context.ReportDiagnostic(Diagnostic.Create(
                MissingMigrationForBreakingChangeDiagnostic, Location.None,
                database.SimpleName, table.Accessor, oldTable.Revision, table.RowTypeFullName));
        }
    }

    static private string PrimaryIndexType(TableModel table) {
        var keyType = table.PrimaryKeyTypeFullName;
        return table.PrimaryKeyKind switch {
            IndexKind.Hash => $"HashIndex<{keyType}>",
            IndexKind.BTree => $"BTreeIndex<{keyType}, {ComparerType(keyType)}>",
            _ => throw new Exception("Invalid index kind")
        };
    }

    static private bool IsTupleKeyType(string keyType) => keyType.StartsWith("(");

    static private bool IsStringType(string type)
        => type is "string" or "System.String" or "global::System.String";

    static private string ComparerType(string keyType) {
        if (!IsTupleKeyType(keyType)) return ElementComparerType(keyType);

        var elementTypes = TupleElementTypes(keyType);
        if (elementTypes.Length is 2 or 3) {
            var comparers = elementTypes.Select(ElementComparerType);
            return $"TupleComparer<{string.Join(", ", elementTypes)}, {string.Join(", ", comparers)}>";
        }

        if (elementTypes.Any(IsStringType))
            throw new Exception($"Unsupported composite index arity {elementTypes.Length} for key {keyType}");
        return $"DefaultComparer<{keyType}>";
    }

    static private string ElementComparerType(string type)
        => IsStringType(type) ? "OrdinalStringComparer" : $"DefaultComparer<{type}>";

    static private string[] TupleElementTypes(string keyType) {
        var inner = keyType.Substring(1, keyType.Length - 2);
        var elements = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++) {
            switch (inner[i]) {
                case '<' or '(' or '[': depth++; break;
                case '>' or ')' or ']': depth--; break;
                case ',' when depth == 0:
                    elements.Add(StripElementName(inner.Substring(start, i - start)));
                    start = i + 1;
                    break;
            }
        }
        elements.Add(StripElementName(inner.Substring(start)));
        return elements.ToArray();
    }

    static private string StripElementName(string element) {
        element = element.Trim();
        var depth = 0;
        for (var i = element.Length - 1; i >= 0; i--) {
            switch (element[i]) {
                case '>' or ')' or ']': depth++; break;
                case '<' or '(' or '[': depth--; break;
                case ' ' when depth == 0: return element.Substring(0, i).Trim();
            }
        }
        return element;
    }

    static private string ConcreteIndexType(IndexModel idx) {
        var keyType = KeyType(idx);
        return (idx.Kind, idx.Uniqueness) switch {
            (IndexKind.Hash, Uniqueness.Unique) => $"HashIndex<{keyType}>",
            (IndexKind.Hash, Uniqueness.NonUnique) => $"NonUniqueHashIndex<{keyType}>",

            (IndexKind.BTree, Uniqueness.Unique) => $"BTreeIndex<{keyType}, {ComparerType(keyType)}>",
            (IndexKind.BTree, Uniqueness.NonUnique) => $"NonUniqueBTreeIndex<{keyType}, {ComparerType(keyType)}>",

            _ => throw new Exception("Invalid index kind or uniqueness") 
        };
    }

    static private string KeyType(IndexModel idx) =>
        idx.Fields.Length == 1
            ? idx.Fields[0].FieldTypeFullName
            : $"({string.Join(", ", idx.Fields.Select(f => $"{f.FieldTypeFullName} {f.FieldName}"))})";

    static private string KeyExpr(string rowVar, IndexModel idx) =>
        idx.Fields.Length == 1
            ? $"{rowVar}.{idx.Fields[0].FieldName}"
            : $"({string.Join(", ", idx.Fields.Select(f => $"{rowVar}.{f.FieldName}"))})";

    static private string IndexFieldName(IndexModel idx) => $"{Camel(idx.AccessorName)}Index";

    static private string UndoInsertDeleteArgs(IndexModel idx) =>
        idx.Uniqueness == Uniqueness.Unique
            ? KeyExpr("inserted", idx)
            : $"{KeyExpr("inserted", idx)}, i";

    static private uint ComputeTableId(string accessor) => TableIdHash.Compute(accessor);

    static private string EmitOpsClass(TableModel table, DatabaseModel database, DatabaseContractDescriptor? descriptor, ClientProtocolKind clientProtocol) =>
        table.Kind == TableKind.Persistent
            ? EmitPersistentOpsClass(table, database.SimpleName, FindRevisionHistory(database, table, descriptor), clientProtocol)
            : EmitInstantOpsClass(table, database.SimpleName, clientProtocol);

    static private List<RevisionHistoryEntry> FindRevisionHistory(DatabaseModel database, TableModel table, DatabaseContractDescriptor? descriptor) =>
        descriptor?.Tables.FirstOrDefault(t => t.DatabaseFullName == database.FullName && t.Accessor == table.Accessor)?.RevisionHistory
            ?? [];

    static private void EmitOpsClassHeader(StringBuilder sb, TableModel table, bool isPersistent) {
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Buffers;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("using MemoryPack;");
        sb.AppendLine("using MessagePack;");
        sb.AppendLine("using RhinoDB.Core;");
        sb.AppendLine("using RhinoDB.Lib.Changes;");
        if (isPersistent) {
            sb.AppendLine("using RhinoDB.Lib.Cold;");
        }
        sb.AppendLine("using RhinoDB.Lib.Indexing;");
        sb.AppendLine("using RhinoDB.Lib.Storage;");
        sb.AppendLine("using RhinoDB.Lib.Tables;");
        sb.AppendLine();
        if (table.RowNamespace is not null) {
            sb.AppendLine($"namespace {table.RowNamespace};");
            sb.AppendLine();
        }
    }

    static private void EmitStorageAndIndexFields(StringBuilder sb, TableModel table, string primaryIndexType) {
        sb.AppendLine($"    private readonly DenseArray<{table.RowTypeFullName}> storage;");
        sb.AppendLine($"    private readonly {primaryIndexType} primaryIndex;");
        foreach (var aif in table.AutoIncrementFields)
            sb.AppendLine($"    private readonly AutoIncrementCounter {Camel(aif.FieldName)}Counter;");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"    private readonly {ConcreteIndexType(idx)} {IndexFieldName(idx)};");
    }

    static private void EmitChangeTrackingFields(StringBuilder sb, string key, string row) {
        sb.AppendLine($"    private readonly List<Change<{key}, {row}>> changes = [];");
        sb.AppendLine("    public bool Dirty { get; private set; }");
        sb.AppendLine("    private DbError lastError;");
        sb.AppendLine($"    private UndoRecord<{row}>[]? undoJournal;");
        sb.AppendLine("    private int undoJournalCount;");
        sb.AppendLine("    private int preApplyCount;");
        sb.AppendLine("    private ulong appliedLsn;");
        sb.AppendLine("    private System.Collections.Generic.HashSet<int>? orphanOffsets;");
        sb.AppendLine("    public int PendingStorageOrphanCount => orphanOffsets?.Count ?? 0;");
        sb.AppendLine("    public bool AppliedInOperation { get; private set; }");
        sb.AppendLine("    #if DEBUG");
        sb.AppendLine("    public System.Action<int>? TestOnlyApplyFault { get; set; }");
        sb.AppendLine("    public System.Action? TestOnlyRevertFault { get; set; }");
        sb.AppendLine("    #endif");
        sb.AppendLine("    public DbError LastError => lastError;");
        sb.AppendLine();
    }

    static private void EmitEvictableField(StringBuilder sb, TableModel table) {
        sb.AppendLine($"    public const bool Evictable = {table.Evictable.ToString().ToLower()};");
        sb.AppendLine();
    }

    static private void AppendAutoIncrementAndIndexParams(StringBuilder sb, TableModel table) {
        foreach (var aif in table.AutoIncrementFields) sb.AppendLine($"        ,AutoIncrementCounter {Camel(aif.FieldName)}Counter");
        foreach (var idx in table.Indexes) sb.AppendLine($"        ,{ConcreteIndexType(idx)} {IndexFieldName(idx)}");
    }

    static private void AppendAutoIncrementAndIndexAssignments(StringBuilder sb, TableModel table) {
        foreach (var aif in table.AutoIncrementFields) sb.AppendLine($"        this.{Camel(aif.FieldName)}Counter = {Camel(aif.FieldName)}Counter;");
        foreach (var idx in table.Indexes) sb.AppendLine($"        this.{IndexFieldName(idx)} = {IndexFieldName(idx)};");
    }

    static private void EmitIterMethod(StringBuilder sb, TableModel table, string ownerSimpleName) {
        var row = table.RowTypeFullName;
        sb.AppendLine($"    public QuerySet<{row}> Iter() {{");
        sb.AppendLine("        using var live = primaryIndex.GetOffsetsIter();");
        sb.AppendLine("        var buffer = live.Buffer();");
        sb.AppendLine($"        if (orphanOffsets is null || orphanOffsets.Count == 0) return new QuerySet<{row}>(storage, live, rowMutator);");
        sb.AppendLine("        using var builder = StackArrayPoolContainerBuilder<int>.Create(buffer.Length);");
        sb.AppendLine("        foreach (var offset in buffer)");
        sb.AppendLine("            if (!orphanOffsets.Contains(offset)) builder.Add(offset);");
        sb.AppendLine($"        return new QuerySet<{row}>(storage, builder.Build().Unwrap(), rowMutator);");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static private string RowMutatorName(TableModel table, string ownerSimpleName) => $"{ownerSimpleName}{table.Accessor}RowMutator";
    static private string PrimaryIndexOpsClassName(TableModel table, string ownerSimpleName) => $"{ownerSimpleName}{table.Accessor}PrimaryIndexOps";
    static private string IndexCollectionName(TableModel table, string ownerSimpleName) => $"{ownerSimpleName}{table.Accessor}IndexCollection";
    static private string IndexAccessorClassName(TableModel table, IndexModel idx, string ownerSimpleName) => $"{ownerSimpleName}{table.Accessor}{idx.AccessorName}IndexOps";

    static private void EmitIndexAccessorClass(
        StringBuilder sb, string className, string row, string mutatorName, string indexType,
        string indexField, string keyType, string parameters, string keyExpr, bool isUnique, bool isRanged) {
        sb.AppendLine($"public sealed class {className} {{");
        sb.AppendLine($"    private readonly DenseArray<{row}> storage;");
        sb.AppendLine($"    private readonly {mutatorName} rowMutator;");
        sb.AppendLine($"    private readonly {indexType} {indexField};");
        sb.AppendLine();
        sb.AppendLine($"    public {className}(DenseArray<{row}> storage, {mutatorName} rowMutator, {indexType} {indexField}) {{");
        sb.AppendLine("        this.storage = storage;");
        sb.AppendLine("        this.rowMutator = rowMutator;");
        sb.AppendLine($"        this.{indexField} = {indexField};");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine(isUnique
            ? $"    public QuerySingle<{row}> Find({parameters}) => new(storage, {indexField}.GetOffset({keyExpr}), rowMutator);"
            : $"    public QuerySet<{row}> Find({parameters}) => new(storage, {indexField}.GetOffsets({keyExpr}), rowMutator);");
        sb.AppendLine($"    public QuerySet<{row}> Iter() => new(storage, {indexField}.GetOffsetsIter(), rowMutator);");
        sb.AppendLine($"    public QuerySet<{row}> Except({parameters}) => new(storage, {indexField}.GetOffsetsExcept({keyExpr}), rowMutator);");
        if (isRanged) {
            sb.AppendLine($"    public QuerySet<{row}> Range({keyType} from, {keyType} to) => new(storage, {indexField}.GetOffsetsRange(from, to), rowMutator);");
            sb.AppendLine($"    public QuerySet<{row}> Gt({keyType} value) => new(storage, {indexField}.GetOffsetsGt(value), rowMutator);");
            sb.AppendLine($"    public QuerySet<{row}> Gte({keyType} value) => new(storage, {indexField}.GetOffsetsGte(value), rowMutator);");
            sb.AppendLine($"    public QuerySet<{row}> Lt({keyType} value) => new(storage, {indexField}.GetOffsetsLt(value), rowMutator);");
            sb.AppendLine($"    public QuerySet<{row}> Lte({keyType} value) => new(storage, {indexField}.GetOffsetsLte(value), rowMutator);");
        }
        sb.AppendLine("}");
    }

    static private void EmitIndexWiringFields(StringBuilder sb, TableModel table, string ownerSimpleName) {
        sb.AppendLine($"    private readonly {RowMutatorName(table, ownerSimpleName)} rowMutator;");
        sb.AppendLine($"    public {PrimaryIndexOpsClassName(table, ownerSimpleName)} Primary {{ get; }}");
        if (table.Indexes.Length == 0) return;
        sb.AppendLine($"    public {IndexCollectionName(table, ownerSimpleName)} Idx {{ get; }}");
    }

    static private void EmitIndexWiringConstruction(StringBuilder sb, TableModel table, string ownerSimpleName) {
        sb.AppendLine($"        rowMutator = new {RowMutatorName(table, ownerSimpleName)}(this);");
        sb.AppendLine($"        Primary = new {PrimaryIndexOpsClassName(table, ownerSimpleName)}(storage, rowMutator, primaryIndex);");
        if (table.Indexes.Length == 0) return;
        sb.Append($"        Idx = new {IndexCollectionName(table, ownerSimpleName)}(storage, rowMutator");
        foreach (var idx in table.Indexes) sb.Append($", {IndexFieldName(idx)}");
        sb.AppendLine(");");
    }

    static private void EmitIndexWiringTypes(StringBuilder sb, TableModel table, string ownerSimpleName) {
        var opsName = $"{ownerSimpleName}{table.Accessor}Ops";
        var row = table.RowTypeFullName;
        var mutatorName = RowMutatorName(table, ownerSimpleName);
        var collectionName = IndexCollectionName(table, ownerSimpleName);

        sb.AppendLine();
        sb.AppendLine($"public sealed class {mutatorName} : IRowMutator<{row}> {{");
        sb.AppendLine($"    private readonly {opsName} table;");
        sb.AppendLine($"    public {mutatorName}({opsName} table) {{ this.table = table; }}");
        sb.AppendLine($"    public void Update({row} original, {row} newRow) => table.Update(original.{table.PrimaryKeyName}, newRow);");
        sb.AppendLine($"    public void Delete({row} row) => table.Delete(row.{table.PrimaryKeyName});");
        sb.AppendLine($"    public {row} WithSamePrimaryKey({row} original, {row} newRow) => newRow with {{ {table.PrimaryKeyName} = original.{table.PrimaryKeyName} }};");
        sb.AppendLine("}");

        sb.AppendLine();

        EmitIndexAccessorClass(
            sb,
            PrimaryIndexOpsClassName(table, ownerSimpleName),
            row,
            mutatorName,
            PrimaryIndexType(table),
            "primaryIndex",
            table.PrimaryKeyTypeFullName,
            $"{table.PrimaryKeyTypeFullName} id",
            "id",
            isUnique: true,
            isRanged: IsRangedIndex(table.PrimaryKeyKind, Uniqueness.Unique));

        if (table.Indexes.Length == 0) return;

        foreach (var idx in table.Indexes) {
            EmitIndexAccessorClass(
                sb,
                IndexAccessorClassName(table, idx, ownerSimpleName),
                row,
                mutatorName,
                ConcreteIndexType(idx),
                IndexFieldName(idx),
                KeyType(idx),
                string.Join(", ", idx.Fields.Select(f => $"{f.FieldTypeFullName} {Camel(f.FieldName)}")),
                idx.Fields.Length == 1
                    ? Camel(idx.Fields[0].FieldName)
                    : $"({string.Join(", ", idx.Fields.Select(f => Camel(f.FieldName)))})",
                idx.Uniqueness == Uniqueness.Unique,
                IsRangedIndex(idx));
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine($"public sealed class {collectionName} {{");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"    public {IndexAccessorClassName(table, idx, ownerSimpleName)} {idx.AccessorName} {{ get; }}");
        sb.AppendLine();
        sb.Append($"    public {collectionName}(DenseArray<{row}> storage, {mutatorName} rowMutator");
        foreach (var idx in table.Indexes) sb.Append($", {ConcreteIndexType(idx)} {IndexFieldName(idx)}");
        sb.AppendLine(") {");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"        {idx.AccessorName} = new {IndexAccessorClassName(table, idx, ownerSimpleName)}(storage, rowMutator, {IndexFieldName(idx)});");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    static private void EmitStagingMethods(StringBuilder sb, TableModel table) {
        var row = table.RowTypeFullName;
        var key = table.PrimaryKeyTypeFullName;
        sb.AppendLine($"    public void Insert({row} row) {{");
        foreach (var aif in table.AutoIncrementFields) {
            var comparison = aif.IsSigned ? "<= 0" : "== 0";
            sb.AppendLine($"        if (row.{aif.FieldName} {comparison})");
            sb.AppendLine($"            row = row with {{ {aif.FieldName} = ({aif.FieldTypeFullName}){Camel(aif.FieldName)}Counter.Next() }};");
        }
        sb.AppendLine($"        changes.Add(new(ChangeKind.Insert, row.{table.PrimaryKeyName}, row));");
        sb.AppendLine("        Dirty = true;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    public void Update({key} id, {row} newRow) {{");
        sb.AppendLine("        changes.Add(new(ChangeKind.Update, id, newRow));");
        sb.AppendLine("        Dirty = true; ");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    public void Delete({key} id) {{");
        sb.AppendLine("        changes.Add(new(ChangeKind.Delete, id, default));");
        sb.AppendLine("        Dirty = true;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static private void EmitUndoPreamble(StringBuilder sb, TableModel table) {
        sb.AppendLine($"        AppliedInOperation = true;");
        sb.AppendLine($"        appliedLsn = lsn;");
        sb.AppendLine($"        preApplyCount = storage.Count;");
        sb.AppendLine($"        undoJournalCount = 0;");
        sb.AppendLine($"        orphanOffsets = null;");
        sb.AppendLine($"        undoJournal ??= ArrayPool<UndoRecord<{table.RowTypeFullName}>>.Shared.Rent(Math.Max(changes.Count, 1));");
    }

    static private void EmitUndoCatch(StringBuilder sb, TableModel table) {
        sb.AppendLine($"        }}");
    }
    
    static private void EmitUndoMethod(StringBuilder sb, TableModel table) {
        sb.AppendLine();
        sb.AppendLine($"    private void RecordUndo(UndoKind kind, int offset, {table.RowTypeFullName} oldRow) {{");
        sb.AppendLine($"        undoJournalCount++;");
        sb.AppendLine($"        if (undoJournal is null || undoJournal.Length < undoJournalCount) {{");
        sb.AppendLine($"            var grown = ArrayPool<UndoRecord<{table.RowTypeFullName}>>.Shared.Rent(Math.Max(undoJournalCount * 2, 4));");
        sb.AppendLine($"            if (undoJournal is not null) ArrayPool<UndoRecord<{table.RowTypeFullName}>>.Shared.Return(undoJournal);");
        sb.AppendLine($"            undoJournal = grown;");
        sb.AppendLine($"        }}");
        sb.AppendLine($"        undoJournal[undoJournalCount - 1] = new UndoRecord<{table.RowTypeFullName}>(kind, offset, oldRow);");
        sb.AppendLine($"    }}");
        sb.AppendLine();
        sb.AppendLine($"    public void ReleaseUndoJournal() {{");
        sb.AppendLine($"        if (undoJournal is null) return;");
        sb.AppendLine($"        ArrayPool<UndoRecord<{table.RowTypeFullName}>>.Shared.Return(undoJournal);");
        sb.AppendLine($"        AppliedInOperation = false;");
        sb.AppendLine($"        undoJournal = null;");
        sb.AppendLine($"        undoJournalCount = 0;");
        sb.AppendLine($"    }}");
        sb.AppendLine();
        sb.AppendLine($"    public bool RevertUndo() {{");
        sb.AppendLine($"        try {{");
        sb.AppendLine("            #if DEBUG");
        sb.AppendLine("            if (TestOnlyRevertFault is not null) TestOnlyRevertFault();");
        sb.AppendLine("            #endif");
        sb.AppendLine($"            for (var i = undoJournalCount - 1; i >= 0; i--) {{");
        sb.AppendLine($"                var step = undoJournal![i];");
        sb.AppendLine($"                switch (step.Kind) {{");
        sb.AppendLine($"                    case UndoKind.Update: {{");
        sb.AppendLine($"                        var applied = storage.Get(step.Offset);");
        sb.AppendLine($"                        storage.Set(step.Offset, step.Row);");
        foreach (var idx in table.Indexes) {
            var undoDeleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("applied", idx) : $"{KeyExpr("applied", idx)}, step.Offset";
            sb.AppendLine($"                        if (!{KeyExpr("applied", idx)}.Equals({KeyExpr("step.Row", idx)})) {{");
            sb.AppendLine($"                            {IndexFieldName(idx)}.Delete({undoDeleteArgs});");
            sb.AppendLine($"                            {IndexFieldName(idx)}.Insert({KeyExpr("step.Row", idx)}, step.Offset);");
            sb.AppendLine($"                        }}");
        }
        sb.AppendLine($"                        break;");
        sb.AppendLine($"                    }}");
        sb.AppendLine($"                    default: {{");
        sb.AppendLine("                        orphanOffsets?.Remove(step.Offset);");
        sb.AppendLine($"                        storage.Set(step.Offset, step.Row);");
        sb.AppendLine($"                        primaryIndex.Insert(step.Row.{table.PrimaryKeyName}, step.Offset);");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"                        {IndexFieldName(idx)}.Insert({KeyExpr("step.Row", idx)}, step.Offset);");
        sb.AppendLine($"                        break;");
        sb.AppendLine($"                    }}");
        sb.AppendLine($"                }}");
        sb.AppendLine($"            }}");
        sb.AppendLine("");
        sb.AppendLine("            for (var i = preApplyCount; i < storage.Count; i++) {");
        sb.AppendLine($"                var inserted = storage.Get(i);");
        sb.AppendLine($"                primaryIndex.Delete(inserted.{table.PrimaryKeyName});");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"                {IndexFieldName(idx)}.Delete({UndoInsertDeleteArgs(idx)});");
        sb.AppendLine("            }");
        sb.AppendLine("");
        if (table.RingBufferCapacity > 0) sb.AppendLine($"            ring.RevertFrom(appliedLsn);");
        sb.AppendLine($"            storage.Truncate(preApplyCount);");
        sb.AppendLine($"            changes.Clear();");
        sb.AppendLine($"            Dirty = false;");
        sb.AppendLine($"            ReleaseUndoJournal();");
        sb.AppendLine($"            return true;");
        sb.AppendLine($"        }}");
        sb.AppendLine($"        catch {{");
        sb.AppendLine($"            ReleaseUndoJournal();");
        sb.AppendLine($"            return false;");
        sb.AppendLine($"        }}");
        sb.AppendLine($"    }}");
        sb.AppendLine();
    }
    static private void EmitValidateMethod(StringBuilder sb, TableModel table) {
        var uniqueIndexes = table.Indexes.Where(i => i.Uniqueness == Uniqueness.Unique).ToImmutableArray();
        sb.AppendLine("    public bool Validate() {");
        sb.AppendLine("        var span = CollectionsMarshal.AsSpan(changes);");
        sb.AppendLine("        var insertCount = 0;");
        sb.AppendLine("        foreach (ref readonly var c in span) { if (c.Kind == ChangeKind.Insert) insertCount++; }");
        sb.AppendLine("        if (insertCount > 0) storage.EnsureCapacity(storage.Count + insertCount);");
        sb.AppendLine("        for (var i = 0; i < span.Length; i++) {");
        sb.AppendLine("            ref readonly var c = ref span[i];");
        sb.AppendLine("            if (c.Kind == ChangeKind.Delete) continue;");
        sb.AppendLine();
        sb.AppendLine("            if (c.Kind == ChangeKind.Insert) {");
        sb.AppendLine("                var existsAlready = false;");
        sb.AppendLine("                var determined = false;");
        sb.AppendLine("                for (var j = i - 1; j >= 0 && !determined; j--) {");
        sb.AppendLine("                    if (!span[j].Key.Equals(c.Key)) continue;");
        sb.AppendLine("                    existsAlready = span[j].Kind != ChangeKind.Delete;");
        sb.AppendLine("                    determined = true;");
        sb.AppendLine("                }");
        sb.AppendLine("                if (!determined) existsAlready = primaryIndex.GetOffset(c.Key).IsOk();");
        sb.AppendLine("                if (existsAlready) { lastError = DbError.DuplicateKey(); return false; }");
        foreach (var idx in uniqueIndexes) {
            sb.AppendLine("                {");
            sb.AppendLine($"                    var checkResult = {IndexFieldName(idx)}.GetOffset({KeyExpr("c.Row", idx)});");
            sb.AppendLine("                    if (checkResult.IsOk()) { lastError = DbError.DuplicateKey(); return false; }");
            sb.AppendLine("                }");
        }
        EmitCustomValidateChecks(sb, table);
        sb.AppendLine("            } else {");
        sb.AppendLine($"                if (!c.Row.{table.PrimaryKeyName}.Equals(c.Key)) {{ lastError = DbError.PrimaryKeyImmutable(); return false; }}");
        sb.AppendLine("                var updateExistsAlready = false;");
        sb.AppendLine("                var updateDetermined = false;");
        sb.AppendLine("                for (var j = i - 1; j >= 0 && !updateDetermined; j--) {");
        sb.AppendLine("                    if (!span[j].Key.Equals(c.Key)) continue;");
        sb.AppendLine("                    updateExistsAlready = span[j].Kind != ChangeKind.Delete;");
        sb.AppendLine("                    updateDetermined = true;");
        sb.AppendLine("                }");
        sb.AppendLine("                if (!updateDetermined) updateExistsAlready = primaryIndex.GetOffset(c.Key).IsOk();");
        sb.AppendLine("                if (!updateExistsAlready) { lastError = DbError.IndexKeyNotFound(); return false; }");
        if (uniqueIndexes.Length > 0) {
            sb.AppendLine("                var offsetResult = primaryIndex.GetOffset(c.Key);");
            sb.AppendLine("                if (offsetResult.IsOk()) {");
            sb.AppendLine("                    var selfOffset = offsetResult.Unwrap();");
            foreach (var idx in uniqueIndexes) {
                sb.AppendLine("                    {");
                sb.AppendLine($"                        var checkResult = {IndexFieldName(idx)}.GetOffset({KeyExpr("c.Row", idx)});");
                sb.AppendLine("                        if (checkResult.IsOk() && checkResult.Unwrap() != selfOffset) { lastError = DbError.DuplicateKey(); return false; }");
                sb.AppendLine("                    }");
            }
            sb.AppendLine("                }");
        }
        EmitCustomValidateChecks(sb, table);
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        return true;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static private void EmitDiscardMethod(StringBuilder sb) {
        sb.AppendLine("    public void Discard() {");
        sb.AppendLine("        changes.Clear();");
        sb.AppendLine("        Dirty = false;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static private void EmitMigrationWrappers(StringBuilder sb, TableModel table) {
        foreach (var migration in table.MigrationMethods) {
            sb.AppendLine($"    internal static {migration.ReturnTypeFullName} MigrateFromRevision{migration.FromRevision}({migration.ParamTypeFullName} old) => {table.RowTypeFullName}.{migration.MethodName}(old);");
        }
        if (table.MigrationMethods.Length > 0) sb.AppendLine();
    }

    static private string FrozenSchemaOpsFullName(string paramTypeFullName) {
        var stripped = paramTypeFullName.StartsWith("global::") ? paramTypeFullName.Substring(8) : paramTypeFullName;
        var lastDot = stripped.LastIndexOf('.');
        var ns = lastDot < 0 ? null : stripped.Substring(0, lastDot);
        var simpleName = lastDot < 0 ? stripped : stripped.Substring(lastDot + 1);
        return ns is null ? $"{simpleName}FrozenSchemaOps" : $"{ns}.{simpleName}FrozenSchemaOps";
    }

    static private void EmitMigrationChain(StringBuilder sb, TableModel table) {
        var sortedHops = table.MigrationMethods.OrderBy(m => m.FromRevision).ToImmutableArray();
        var tip = sortedHops.Length == 0 ? 0 : sortedHops[sortedHops.Length - 1].FromRevision + 1;
        var row = table.RowTypeFullName;

        sb.AppendLine($"    internal static {row} MigrateToCurrentRevision(int fromRevision, byte[] rowBytes) {{");
        sb.AppendLine($"        if (fromRevision >= {tip}) return DeserializeRow(rowBytes);");
        if (sortedHops.Length > 0) {
            sb.AppendLine("        switch (fromRevision) {");
            for (var i = 0; i < sortedHops.Length; i++) {
                var startRevision = sortedHops[i].FromRevision;
                var expr = $"{FrozenSchemaOpsFullName(sortedHops[i].ParamTypeFullName)}.DeserializeRow(rowBytes)";
                for (var j = i; j < sortedHops.Length; j++) expr = $"MigrateFromRevision{sortedHops[j].FromRevision}({expr})";
                sb.AppendLine($"            case {startRevision}: return {expr};");
            }
            sb.AppendLine("        }");
        }
        sb.AppendLine($"        throw new InvalidOperationException($\"No registered migration chain for {row} starting at revision {{fromRevision}}.\");");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static private void EmitClientMigrationChain(StringBuilder sb, TableModel table, ClientProtocolKind clientProtocol) {
        if (clientProtocol == ClientProtocolKind.Raw) return;

        var formatName = clientProtocol == ClientProtocolKind.VersionedMemoryPack ? "VersionedMemoryPack" : "MessagePack";
        var sortedHops = table.MigrationMethods.OrderBy(m => m.FromRevision).ToImmutableArray();
        var tip = sortedHops.Length == 0 ? 0 : sortedHops[sortedHops.Length - 1].FromRevision + 1;
        var row = table.RowTypeFullName;

        sb.AppendLine($"    internal static {row} MigrateClientBytesToCurrentRevision(int fromRevision, byte[] clientBytes) {{");
        sb.AppendLine($"        if (fromRevision >= {tip}) return Deserialize{formatName}(clientBytes);");
        if (sortedHops.Length > 0) {
            sb.AppendLine("        switch (fromRevision) {");
            for (var i = 0; i < sortedHops.Length; i++) {
                var startRevision = sortedHops[i].FromRevision;
                var expr = $"{FrozenSchemaOpsFullName(sortedHops[i].ParamTypeFullName)}.Deserialize{formatName}(clientBytes)";
                for (var j = i; j < sortedHops.Length; j++) expr = $"MigrateFromRevision{sortedHops[j].FromRevision}({expr})";
                sb.AppendLine($"            case {startRevision}: return {expr};");
            }
            sb.AppendLine("        }");
        }
        sb.AppendLine($"        throw new InvalidOperationException($\"No registered client migration chain for {row} starting at revision {{fromRevision}}.\");");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    // CompatAdapter's downgrade direction - the reverse of EmitClientMigrationChain, and unlike it, always
    // emitted regardless of protocol (Raw clients need downgraded bytes too, just Raw-encoded ones - there's
    // no pre-existing "downgrade" pipeline for Raw to fall back on the way upgrade falls back to
    // MigrateToCurrentRevision). An incomplete chain is expected (not every transform is invertible) -
    // CanDowngradeTo lets a caller check first; DowngradeToRevisionBytes throws only if that check was
    // skipped or wrong.
    static private void EmitDowngradeChain(StringBuilder sb, TableModel table, ClientProtocolKind clientProtocol) {
        var migrationHops = table.MigrationMethods.OrderBy(m => m.FromRevision).ToImmutableArray();
        var tip = migrationHops.Length == 0 ? 0 : migrationHops[migrationHops.Length - 1].FromRevision + 1;
        var row = table.RowTypeFullName;
        var serializeMethodName = clientProtocol switch {
            ClientProtocolKind.VersionedMemoryPack => "SerializeVersionedMemoryPack",
            ClientProtocolKind.MessagePack => "SerializeMessagePack",
            _ => "SerializeRow",
        };

        var downgradesByToRevision = table.DowngradeMethods.ToDictionary(d => d.ToRevision);
        var reachable = ImmutableArray.CreateBuilder<int>();
        for (var revision = tip - 1; downgradesByToRevision.ContainsKey(revision); revision--) reachable.Add(revision);

        sb.AppendLine($"    private static readonly HashSet<int> DowngradableToRevisions = new HashSet<int> {{ {string.Join(", ", reachable)} }};");
        sb.AppendLine($"    internal static bool CanDowngradeTo(int toRevision) => toRevision >= {tip} || DowngradableToRevisions.Contains(toRevision);");
        sb.AppendLine();

        sb.AppendLine($"    internal static byte[] DowngradeToRevisionBytes(int toRevision, {row} current) {{");
        sb.AppendLine($"        if (toRevision >= {tip}) return {serializeMethodName}(current);");
        if (reachable.Count > 0) {
            sb.AppendLine("        switch (toRevision) {");
            foreach (var target in reachable) {
                var expr = "current";
                for (var r = tip - 1; r >= target; r--) expr = $"{row}.{downgradesByToRevision[r].MethodName}({expr})";
                var targetOpsName = FrozenSchemaOpsFullName(downgradesByToRevision[target].ReturnTypeFullName);
                sb.AppendLine($"            case {target}: return {targetOpsName}.{serializeMethodName}({expr});");
            }
            sb.AppendLine("        }");
        }
        sb.AppendLine($"        throw new InvalidOperationException($\"No registered downgrade chain for {row} down to revision {{toRevision}} - check CanDowngradeTo first.\");");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    // Row-type-scoped sibling of EmitDatabase's IsGenerationInvalid - duplicated per table exactly like
    // Revision/RevisionHistory/MigrateFromRevision{N} already are, when one row type backs several tables
    // (no new "one emission per row-type-symbol" infrastructure needed - confirmed with the user).
    static private void EmitIsRevisionInvalid(StringBuilder sb, TableModel table) {
        sb.AppendLine($"    private static readonly HashSet<int> InvalidRevisions = new HashSet<int> {{ {string.Join(", ", table.InvalidRevisions)} }};");
        sb.AppendLine("    public static bool IsRevisionInvalid(int revision) => InvalidRevisions.Contains(revision);");
        sb.AppendLine();
    }

    static private void EmitRevisionAtGeneration(StringBuilder sb, List<RevisionHistoryEntry> revisionHistory) {
        sb.AppendLine("    private static readonly (int Generation, int Revision)[] RevisionHistory = new (int, int)[] {");
        foreach (var entry in revisionHistory)
            sb.AppendLine($"        ({entry.Generation}, {entry.Revision}),");
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    internal static int RevisionAtGeneration(int generation) {");
        sb.AppendLine("        var revision = 0;");
        sb.AppendLine("        foreach (var entry in RevisionHistory) {");
        sb.AppendLine("            if (entry.Generation > generation) break;");
        sb.AppendLine("            revision = entry.Revision;");
        sb.AppendLine("        }");
        sb.AppendLine("        return revision;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static private void EmitCustomValidateChecks(StringBuilder sb, TableModel table) {
        foreach (var methodName in table.ValidateMethodNames) {
            sb.AppendLine("                {");
            sb.AppendLine($"                    var customError = {table.RowTypeFullName}.{methodName}(c.Row);");
            sb.AppendLine("                    if (customError is not null) { lastError = customError.Value; return false; }");
            sb.AppendLine("                }");
        }
    }

    static private void EmitRawSerializer(StringBuilder sb, TableModel table) {
        var row = table.RowTypeFullName;
        var key = table.PrimaryKeyTypeFullName;
        var pkField = table.Fields.First(f => f.FieldName == table.PrimaryKeyName);

        sb.AppendLine($"    internal static byte[] SerializeRow({row} row) {{");
        sb.AppendLine("        var bufferWriter = new PooledBufferWriter(256);");
        sb.AppendLine("        var writerState = MemoryPackWriterOptionalStatePool.Rent(null);");
        sb.AppendLine("        var writer = new MemoryPackWriter<PooledBufferWriter>(ref bufferWriter, writerState);");
        foreach (var f in table.Fields) SchemaWalk.EmitWriteField(sb, f, $"row.{f.FieldName}");
        sb.AppendLine("        writer.Flush();");
        sb.AppendLine("        var result = bufferWriter.WrittenSpan.ToArray();");
        sb.AppendLine("        bufferWriter.Dispose();");
        sb.AppendLine("        return result;");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    internal static {row} DeserializeRow(byte[] bytes) {{");
        sb.AppendLine("        var readerState = MemoryPackReaderOptionalStatePool.Rent(null);");
        sb.AppendLine("        var reader = new MemoryPackReader(bytes, readerState);");
        foreach (var f in table.Fields) SchemaWalk.EmitReadField(sb, f);
        sb.AppendLine("        reader.Dispose();");
        sb.Append($"        return new {row}(");
        sb.Append(string.Join(", ", table.Fields.Select(f => $"{Camel(f.FieldName)}Value")));
        sb.AppendLine(");");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    internal static byte[] SerializeKey({key} key) {{");
        sb.AppendLine("        var bufferWriter = new PooledBufferWriter(32);");
        sb.AppendLine("        var writerState = MemoryPackWriterOptionalStatePool.Rent(null);");
        sb.AppendLine("        var writer = new MemoryPackWriter<PooledBufferWriter>(ref bufferWriter, writerState);");
        SchemaWalk.EmitWriteField(sb, pkField, "key");
        sb.AppendLine("        writer.Flush();");
        sb.AppendLine("        var result = bufferWriter.WrittenSpan.ToArray();");
        sb.AppendLine("        bufferWriter.Dispose();");
        sb.AppendLine("        return result;");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    internal static {key} DeserializeKey(byte[] bytes) {{");
        sb.AppendLine("        var readerState = MemoryPackReaderOptionalStatePool.Rent(null);");
        sb.AppendLine("        var reader = new MemoryPackReader(bytes, readerState);");
        SchemaWalk.EmitReadField(sb, pkField);
        sb.AppendLine("        reader.Dispose();");
        sb.AppendLine($"        return {Camel(pkField.FieldName)}Value;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static private string EmitInstantOpsClass(TableModel table, string ownerSimpleName, ClientProtocolKind clientProtocol) {
        var sb = new StringBuilder();
        var row = table.RowTypeFullName;
        var key = table.PrimaryKeyTypeFullName;
        var opsName = $"{ownerSimpleName}{table.Accessor}Ops";
        var primaryIndexType = PrimaryIndexType(table);

        EmitOpsClassHeader(sb, table, isPersistent: false);

        sb.AppendLine($"public sealed class {opsName} {{");
        sb.AppendLine($"    private const uint TableId = {ComputeTableId(table.Accessor)}u;");
        EmitStorageAndIndexFields(sb, table, primaryIndexType);
        if (table.RingBufferCapacity > 0) sb.AppendLine("    private readonly ChangeRingBuffer ring;");
        EmitChangeTrackingFields(sb, key, row);
        EmitIndexWiringFields(sb, table, ownerSimpleName);

        sb.AppendLine($"    public {opsName}");
        sb.AppendLine("    (");
        sb.AppendLine($"        DenseArray<{row}> storage");
        sb.AppendLine($"        ,{primaryIndexType} primaryIndex");
        if (table.RingBufferCapacity > 0) sb.AppendLine($"        ,ChangeRingBuffer ring");
        AppendAutoIncrementAndIndexParams(sb, table);
        sb.AppendLine("    ) {");
        sb.AppendLine("        this.storage = storage;");
        sb.AppendLine("        this.primaryIndex = primaryIndex;");
        if (table.RingBufferCapacity > 0) sb.AppendLine("        this.ring = ring;");
        AppendAutoIncrementAndIndexAssignments(sb, table);
        EmitIndexWiringConstruction(sb, table, ownerSimpleName);
        sb.AppendLine("    }");
        if (table.RingBufferCapacity > 0) {
            sb.AppendLine("");
            sb.AppendLine("    public Result<ArrayPoolContainer<RingEntry>> TryGetChangesSince(ulong lsn) => ring.TryGetChangesSince(lsn);");
            sb.AppendLine("");
        }
        sb.AppendLine();

        EmitIterMethod(sb, table, ownerSimpleName);
        EmitSweepMethod(sb, table);
        EmitStagingMethods(sb, table);
        EmitRawSerializer(sb, table);
        SchemaWalk.EmitProtocolWrapperMethods(sb, table.RowTypeFullName, clientProtocol);
        EmitValidateMethod(sb, table);
        EmitDiscardMethod(sb);
        EmitInstantApply(sb, table);

        sb.AppendLine("}");
        EmitIndexWiringTypes(sb, table, ownerSimpleName);
        return sb.ToString();
    }

    static private void EmitRingBufferRecordCall(StringBuilder sb, TableModel table, string changeKindName, string keyExpr, string rowExpr) {
        if (table.RingBufferCapacity <= 0) return;
        sb.AppendLine($"                    ring.Record(TableId, ChangeKind.{changeKindName}, lsn, {keyExpr}, {rowExpr});");
    }

    static private void EmitInstantApply(StringBuilder sb, TableModel table) {
        sb.AppendLine("    public void Apply(ulong lsn) {");
        EmitUndoPreamble(sb, table);
        sb.AppendLine($"        var changeIndex = -1;");
        sb.AppendLine("        foreach (ref readonly var c in CollectionsMarshal.AsSpan(changes)) {");
        sb.AppendLine($"            changeIndex++;");
        sb.AppendLine($"            #if DEBUG");
        sb.AppendLine($"            if (TestOnlyApplyFault is not null) TestOnlyApplyFault(changeIndex);");
        sb.AppendLine($"            #endif");
        sb.AppendLine("            switch (c.Kind) {");
        sb.AppendLine("                case ChangeKind.Insert: {");
        sb.AppendLine($"                    var pk = c.Row.{table.PrimaryKeyName};");
        sb.AppendLine("                    if (primaryIndex.GetOffset(pk).IsOk()) { lastError = DbError.DuplicateKey(); break; }");
        sb.AppendLine("                    var offset = storage.Insert(c.Row);");
        sb.AppendLine("                    primaryIndex.Insert(pk, offset);");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"                    {IndexFieldName(idx)}.Insert({KeyExpr("c.Row", idx)}, offset);");
        EmitRingBufferRecordCall(sb, table, "Insert", "SerializeKey(pk)", "SerializeRow(c.Row)");
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("                case ChangeKind.Update: {");
        sb.AppendLine("                    var offsetResult = primaryIndex.GetOffset(c.Key);");
        sb.AppendLine("                    if (offsetResult.IsError()) { lastError = offsetResult.GetError(); break; }");
        sb.AppendLine("                    var offset = offsetResult.Unwrap();");
        sb.AppendLine("                    var oldRow = storage.Get(offset);");
        sb.AppendLine($"                    RecordUndo(UndoKind.Update, offset, oldRow);");
        sb.AppendLine("                    storage.Set(offset, c.Row);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("oldRow", idx) : $"{KeyExpr("oldRow", idx)}, offset";
            sb.AppendLine($"                    if (!{KeyExpr("oldRow", idx)}.Equals({KeyExpr("c.Row", idx)})) {{");
            sb.AppendLine($"                        {IndexFieldName(idx)}.Delete({deleteArgs});");
            sb.AppendLine($"                        {IndexFieldName(idx)}.Insert({KeyExpr("c.Row", idx)}, offset);");
            sb.AppendLine("                    }");
        }
        EmitRingBufferRecordCall(sb, table, "Update", "SerializeKey(c.Key)", "SerializeRow(c.Row)");
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("                default: {");
        sb.AppendLine("                    var offsetResult = primaryIndex.GetOffset(c.Key);");
        sb.AppendLine("                    if (offsetResult.IsError()) { lastError = offsetResult.GetError(); break; }");
        sb.AppendLine("                    var offset = offsetResult.Unwrap();");
        sb.AppendLine("                    var oldRow = storage.Get(offset);");
        sb.AppendLine($"                    RecordUndo(UndoKind.Delete, offset, oldRow);");
        sb.AppendLine("                    primaryIndex.Delete(c.Key);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("oldRow", idx) : $"{KeyExpr("oldRow", idx)}, offset";
            sb.AppendLine($"                    {IndexFieldName(idx)}.Delete({deleteArgs});");
        }
        sb.AppendLine($"                    (orphanOffsets ??= new()).Add(offset);");
        EmitRingBufferRecordCall(sb, table, "Delete", "SerializeKey(c.Key)", "null");
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        EmitUndoCatch(sb, table);
        sb.AppendLine("        changes.Clear();");
        sb.AppendLine("        Dirty = false;");
        sb.AppendLine("    }");
        EmitUndoMethod(sb, table);
    }

    static private void EmitSweepMethod(StringBuilder sb, TableModel table) {
        sb.AppendLine("    public void SweepDeleted() {");
        sb.AppendLine("        if (orphanOffsets is null || orphanOffsets.Count == 0) return;");
        sb.AppendLine("        var orphans = orphanOffsets;");
        sb.AppendLine("        var rented = ArrayPool<int>.Shared.Rent(orphans.Count);");
        sb.AppendLine("        try {");
        sb.AppendLine("            var targets = rented.AsSpan(0, orphans.Count);");
        sb.AppendLine("            var n = 0;");
        sb.AppendLine("            foreach (var offset in orphans) targets[n++] = offset;");
        sb.AppendLine("            orphans.Clear();");
        sb.AppendLine("            using var relocations = storage.DeleteMany(targets);");
        sb.AppendLine("            foreach (var relocation in relocations) {");
        sb.AppendLine($"                var relocatedPk = relocation.Row.{table.PrimaryKeyName};");
        sb.AppendLine("                primaryIndex.Delete(relocatedPk);");
        sb.AppendLine("                primaryIndex.Insert(relocatedPk, relocation.NewOffset);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("relocation.Row", idx) : $"{KeyExpr("relocation.Row", idx)}, relocation.OldOffset";
            sb.AppendLine($"                {IndexFieldName(idx)}.Delete({deleteArgs});");
            sb.AppendLine($"                {IndexFieldName(idx)}.Insert({KeyExpr("relocation.Row", idx)}, relocation.NewOffset);");
        }
        sb.AppendLine("            }");
        sb.AppendLine("        } finally {");
        sb.AppendLine("            ArrayPool<int>.Shared.Return(rented);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static private string EmitPersistentOpsClass(TableModel table, string ownerSimpleName, List<RevisionHistoryEntry> revisionHistory, ClientProtocolKind clientProtocol) {
        var sb = new StringBuilder();
        var row = table.RowTypeFullName;
        var key = table.PrimaryKeyTypeFullName;
        var opsName = $"{ownerSimpleName}{table.Accessor}Ops";
        var primaryIndexType = PrimaryIndexType(table);

        EmitOpsClassHeader(sb, table, isPersistent: true);

        sb.AppendLine($"public sealed class {opsName} {{");
        sb.AppendLine($"    private const uint TableId = {ComputeTableId(table.Accessor)}u;");
        EmitStorageAndIndexFields(sb, table, primaryIndexType);
        sb.AppendLine($"    private readonly ColdTable<{key}, {row}> coldTable;");
        sb.AppendLine("    private readonly ColdStore cold;");
        if (table.RingBufferCapacity > 0) sb.AppendLine("    private readonly ChangeRingBuffer ring;");
        EmitChangeTrackingFields(sb, key, row);
        EmitEvictableField(sb, table);
        EmitIndexWiringFields(sb, table, ownerSimpleName);

        sb.AppendLine($"    public {opsName}");
        sb.AppendLine("    (");
        sb.AppendLine($"        DenseArray<{row}> storage");
        sb.AppendLine($"        ,{primaryIndexType} primaryIndex");
        sb.AppendLine($"        ,ColdTable<{key}, {row}> coldTable");
        sb.AppendLine($"        ,ColdStore cold");
        if (table.RingBufferCapacity > 0) sb.AppendLine($"        ,ChangeRingBuffer ring");
        AppendAutoIncrementAndIndexParams(sb, table);
        sb.AppendLine("    ) {");
        sb.AppendLine("        this.storage = storage;");
        sb.AppendLine("        this.primaryIndex = primaryIndex;");
        sb.AppendLine("        this.coldTable = coldTable;");
        sb.AppendLine("        this.cold = cold;");
        if (table.RingBufferCapacity > 0) sb.AppendLine("        this.ring = ring;");
        AppendAutoIncrementAndIndexAssignments(sb, table);
        EmitIndexWiringConstruction(sb, table, ownerSimpleName);
        if (table.Evictable) sb.AppendLine("        cold.RegisterEvictionDrop(TableId, TryGetCurrentRowBytesForEviction, EvictDrop);");
        sb.AppendLine("    }");
        if (table.RingBufferCapacity > 0) {
            sb.AppendLine("");
            sb.AppendLine("    public Result<ArrayPoolContainer<RingEntry>> TryGetChangesSince(ulong lsn) => ring.TryGetChangesSince(lsn);");
            sb.AppendLine("");
        }
        sb.AppendLine();

        EmitIterMethod(sb, table, ownerSimpleName);
        EmitSweepMethod(sb, table);
        EmitStagingMethods(sb, table);
        EmitRawSerializer(sb, table);
        SchemaWalk.EmitProtocolWrapperMethods(sb, table.RowTypeFullName, clientProtocol);
        EmitMigrationWrappers(sb, table);
        EmitMigrationChain(sb, table);
        EmitClientMigrationChain(sb, table, clientProtocol);
        EmitDowngradeChain(sb, table, clientProtocol);
        EmitIsRevisionInvalid(sb, table);
        EmitRevisionAtGeneration(sb, revisionHistory);
        EmitValidateMethod(sb, table);
        EmitDiscardMethod(sb);
        EmitPersistentApply(sb, table);
        EmitBulkLoadMethods(sb, table);
        EmitReplayApply(sb, table);
        if (table.Evictable) EmitStorageAccessor(sb, table, opsName);

        sb.AppendLine("}");
        EmitIndexWiringTypes(sb, table, ownerSimpleName);
        return sb.ToString();
    }

    static private void EmitPersistentApply(StringBuilder sb, TableModel table) {
        sb.AppendLine("    public void Apply(ulong lsn) {");
        EmitUndoPreamble(sb, table);
        sb.AppendLine($"        var changeIndex = -1;");
        sb.AppendLine("        foreach (ref readonly var c in CollectionsMarshal.AsSpan(changes)) {");
        sb.AppendLine($"            changeIndex++;");
        sb.AppendLine($"            #if DEBUG");
        sb.AppendLine($"            if (TestOnlyApplyFault is not null) TestOnlyApplyFault(changeIndex);");
        sb.AppendLine($"            #endif");
        sb.AppendLine("            switch (c.Kind) {");
        sb.AppendLine("                case ChangeKind.Insert: {");
        sb.AppendLine("                    if (!cold.IsScopeActive) { lastError = DbError.NoActiveTransaction(); break; }");
        sb.AppendLine($"                    var pk = c.Row.{table.PrimaryKeyName};");
        sb.AppendLine("                    if (primaryIndex.GetOffset(pk).IsOk()) { lastError = DbError.DuplicateKey(); break; }");
        sb.AppendLine("                    var offset = storage.Insert(c.Row);");
        sb.AppendLine("                    primaryIndex.Insert(pk, offset);");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"                    {IndexFieldName(idx)}.Insert({KeyExpr("c.Row", idx)}, offset);");
        sb.AppendLine("                    var insertKeyBytes = SerializeKey(pk);");
        sb.AppendLine("                    var insertRowBytes = SerializeRow(c.Row);");
        sb.AppendLine("                    cold.Stage(TableId, ChangeKind.Insert, insertKeyBytes, insertRowBytes);");
        EmitRingBufferRecordCall(sb, table, "Insert", "insertKeyBytes", "insertRowBytes");
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("                case ChangeKind.Update: {");
        sb.AppendLine("                    if (!cold.IsScopeActive) { lastError = DbError.NoActiveTransaction(); break; }");
        sb.AppendLine("                    var offsetResult = primaryIndex.GetOffset(c.Key);");
        sb.AppendLine("                    if (offsetResult.IsError()) { lastError = offsetResult.GetError(); break; }");
        sb.AppendLine("                    var offset = offsetResult.Unwrap();");
        sb.AppendLine("                    var oldRow = storage.Get(offset);");
        sb.AppendLine($"                    RecordUndo(UndoKind.Update, offset, oldRow);");
        sb.AppendLine("                    storage.Set(offset, c.Row);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("oldRow", idx) : $"{KeyExpr("oldRow", idx)}, offset";
            sb.AppendLine($"                    if (!{KeyExpr("oldRow", idx)}.Equals({KeyExpr("c.Row", idx)})) {{");
            sb.AppendLine($"                        {IndexFieldName(idx)}.Delete({deleteArgs});");
            sb.AppendLine($"                        {IndexFieldName(idx)}.Insert({KeyExpr("c.Row", idx)}, offset);");
            sb.AppendLine("                    }");
        }
        sb.AppendLine("                    var updateKeyBytes = SerializeKey(c.Key);");
        sb.AppendLine("                    var updateRowBytes = SerializeRow(c.Row);");
        sb.AppendLine("                    cold.Stage(TableId, ChangeKind.Update, updateKeyBytes, updateRowBytes);");
        EmitRingBufferRecordCall(sb, table, "Update", "updateKeyBytes", "updateRowBytes");
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("                default: {");
        sb.AppendLine("                    if (!cold.IsScopeActive) { lastError = DbError.NoActiveTransaction(); break; }");
        sb.AppendLine("                    var offsetResult = primaryIndex.GetOffset(c.Key);");
        sb.AppendLine("                    if (offsetResult.IsError()) { lastError = offsetResult.GetError(); break; }");
        sb.AppendLine("                    var offset = offsetResult.Unwrap();");
        sb.AppendLine("                    var oldRow = storage.Get(offset);");
        sb.AppendLine($"                    RecordUndo(UndoKind.Delete, offset, oldRow);");
        sb.AppendLine("                    primaryIndex.Delete(c.Key);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("oldRow", idx) : $"{KeyExpr("oldRow", idx)}, offset";
            sb.AppendLine($"                    {IndexFieldName(idx)}.Delete({deleteArgs});");
        }
        sb.AppendLine($"                    (orphanOffsets ??= new()).Add(offset);");
        sb.AppendLine("                    var deleteKeyBytes = SerializeKey(c.Key);");
        sb.AppendLine("                    cold.Stage(TableId, ChangeKind.Delete, deleteKeyBytes, null);");
        EmitRingBufferRecordCall(sb, table, "Delete", "deleteKeyBytes", "null");
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        EmitUndoCatch(sb, table);
        sb.AppendLine("        changes.Clear();");
        sb.AppendLine("        Dirty = false;");
        sb.AppendLine("    }");
        EmitUndoMethod(sb, table);
    }

    static private void EmitReplayApply(StringBuilder sb, TableModel table) {
        sb.AppendLine();
        sb.AppendLine("    public void ReplayApply(ChangeKind kind, byte[] keyBytes, byte[]? rowBytes) {");
        sb.AppendLine("        switch (kind) {");
        sb.AppendLine("            case ChangeKind.Insert: {");
        sb.AppendLine("                var row = DeserializeRow(rowBytes!);");
        sb.AppendLine($"                var pk = row.{table.PrimaryKeyName};");
        sb.AppendLine("                if (primaryIndex.GetOffset(pk).IsOk()) break;");
        sb.AppendLine("                var offset = storage.Insert(row);");
        sb.AppendLine("                primaryIndex.Insert(pk, offset);");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"                {IndexFieldName(idx)}.Insert({KeyExpr("row", idx)}, offset);");
        sb.AppendLine("                break;");
        sb.AppendLine("            }");
        sb.AppendLine("            case ChangeKind.Update: {");
        sb.AppendLine("                var key = DeserializeKey(keyBytes);");
        sb.AppendLine("                var row = DeserializeRow(rowBytes!);");
        sb.AppendLine("                var offsetResult = primaryIndex.GetOffset(key);");
        sb.AppendLine("                if (offsetResult.IsError()) break;");
        sb.AppendLine("                var offset = offsetResult.Unwrap();");
        sb.AppendLine("                var oldRow = storage.Get(offset);");
        sb.AppendLine("                storage.Set(offset, row);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("oldRow", idx) : $"{KeyExpr("oldRow", idx)}, offset";
            sb.AppendLine($"                if (!{KeyExpr("oldRow", idx)}.Equals({KeyExpr("row", idx)})) {{");
            sb.AppendLine($"                    {IndexFieldName(idx)}.Delete({deleteArgs});");
            sb.AppendLine($"                    {IndexFieldName(idx)}.Insert({KeyExpr("row", idx)}, offset);");
            sb.AppendLine("                }");
        }
        sb.AppendLine("                break;");
        sb.AppendLine("            }");
        sb.AppendLine("            default: {");
        sb.AppendLine("                var key = DeserializeKey(keyBytes);");
        sb.AppendLine("                var offsetResult = primaryIndex.GetOffset(key);");
        sb.AppendLine("                if (offsetResult.IsError()) break;");
        sb.AppendLine("                var offset = offsetResult.Unwrap();");
        sb.AppendLine("                var oldRow = storage.Get(offset);");
        sb.AppendLine("                var lastOffset = storage.LastOffset;");
        sb.AppendLine("                var swapped = storage.Delete(offset);");
        sb.AppendLine("                primaryIndex.Delete(key);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("oldRow", idx) : $"{KeyExpr("oldRow", idx)}, offset";
            sb.AppendLine($"                {IndexFieldName(idx)}.Delete({deleteArgs});");
        }
        sb.AppendLine("                if (swapped is { } swappedRow) {");
        sb.AppendLine($"                    var swappedPk = swappedRow.{table.PrimaryKeyName};");
        sb.AppendLine("                    primaryIndex.Delete(swappedPk);");
        sb.AppendLine("                    primaryIndex.Insert(swappedPk, offset);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("swappedRow", idx) : $"{KeyExpr("swappedRow", idx)}, lastOffset";
            sb.AppendLine($"                    {IndexFieldName(idx)}.Delete({deleteArgs});");
            sb.AppendLine($"                    {IndexFieldName(idx)}.Insert({KeyExpr("swappedRow", idx)}, offset);");
        }
        sb.AppendLine("                }");
        sb.AppendLine("                break;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    static private void EmitBulkLoadMethods(StringBuilder sb, TableModel table) {
        var row = table.RowTypeFullName;

        sb.AppendLine();
        sb.AppendLine($"    public void BulkLoadFromCold() {{");
        sb.AppendLine($"        var bulkRows = new System.Collections.Generic.List<{row}>();");
        sb.AppendLine("        foreach (var pair in cold.ScanAll(coldTable)) bulkRows.Add(pair.Row);");
        sb.AppendLine("        var bulkCount = bulkRows.Count;");
        sb.AppendLine("        if (bulkCount == 0) return;");
        sb.AppendLine();
        sb.AppendLine("        storage.EnsureCapacity(bulkCount);");
        sb.AppendLine("        var bulkOffsets = new int[bulkCount];");
        sb.AppendLine("        for (var i = 0; i < bulkCount; i++) bulkOffsets[i] = storage.Insert(bulkRows[i]);");
        sb.AppendLine();

        if (table.PrimaryKeyKind == IndexKind.BTree) {
            sb.AppendLine($"        var bulkPrimaryKeys = new {table.PrimaryKeyTypeFullName}[bulkCount];");
            sb.AppendLine($"        for (var i = 0; i < bulkCount; i++) bulkPrimaryKeys[i] = bulkRows[i].{table.PrimaryKeyName};");
            sb.AppendLine("        primaryIndex.BulkLoadFrom(bulkPrimaryKeys, bulkOffsets);");
        } else {
            sb.AppendLine($"        for (var i = 0; i < bulkCount; i++) primaryIndex.Insert(bulkRows[i].{table.PrimaryKeyName}, bulkOffsets[i]);");
        }
        sb.AppendLine();

        for (var n = 0; n < table.Indexes.Length; n++) {
            var idx = table.Indexes[n];
            var keyExpr = KeyExpr("bulkRows[i]", idx);
            var name = IndexFieldName(idx);

            if (idx.Kind == IndexKind.BTree) {
                sb.AppendLine($"        var bulkKeys{n} = new {KeyType(idx)}[bulkCount];");
                sb.AppendLine($"        for (var i = 0; i < bulkCount; i++) bulkKeys{n}[i] = {keyExpr};");
                sb.AppendLine($"        {name}.BulkLoadFrom(bulkKeys{n}, bulkOffsets);");
            } else {
                sb.AppendLine($"        for (var i = 0; i < bulkCount; i++) {name}.Insert({keyExpr}, bulkOffsets[i]);");
            }
            sb.AppendLine();
        }

        foreach (var aif in table.AutoIncrementFields) {
            var max = $"bulkMax{Camel(aif.FieldName)}";
            sb.AppendLine($"        var {max} = long.MinValue;");
            sb.AppendLine($"        for (var i = 0; i < bulkCount; i++) {{ var v = (long)bulkRows[i].{aif.FieldName}; if (v > {max}) {max} = v; }}");
            sb.AppendLine($"        if ({max} != long.MinValue) {Camel(aif.FieldName)}Counter.Seed({max} + 1);");
            sb.AppendLine();
        }

        sb.AppendLine("    }");
    }

    static private void EmitStorageAccessor(StringBuilder sb, TableModel table, string opsName) {
        var row = table.RowTypeFullName;
        var key = table.PrimaryKeyTypeFullName;
        sb.AppendLine();
        sb.AppendLine("    public StorageAccessor Storage => new(this);");
        sb.AppendLine($"    public readonly struct StorageAccessor({opsName} ops) {{");
        sb.AppendLine($"        public Result Load({key} id) => ops.LoadInternal(id);");
        sb.AppendLine($"        public Result<{row}> Peek({key} id) => ops.cold.Peek(ops.coldTable, id);");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    private Result LoadInternal({key} id) {{");
        sb.AppendLine("        if (primaryIndex.GetOffset(id).IsOk()) return Result.Ok();");
        sb.AppendLine("        if (!cold.IsScopeActive) return Result.Error(DbError.NoActiveTransaction());");
        sb.AppendLine("        var coldResult = cold.Peek(coldTable, id);");
        sb.AppendLine("        if (coldResult.IsError()) return coldResult.Void();");
        sb.AppendLine("        var row = coldResult.Unwrap();");
        sb.AppendLine("        var offset = storage.Insert(row);");
        sb.AppendLine("        primaryIndex.Insert(id, offset);");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"        {IndexFieldName(idx)}.Insert({KeyExpr("row", idx)}, offset);");
        sb.AppendLine("        return Result.Ok();");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    public Result Evict({key} id) {{");
        sb.AppendLine("        cold.StageEviction(TableId, SerializeKey(id));");
        sb.AppendLine("        return Result.Ok();");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    public byte[]? TryGetCurrentRowBytesForEviction(byte[] keyBytes) {");
        sb.AppendLine("        var id = DeserializeKey(keyBytes);");
        sb.AppendLine("        var offsetResult = primaryIndex.GetOffset(id);");
        sb.AppendLine("        if (offsetResult.IsError()) return null;");
        sb.AppendLine("        var row = storage.Get(offsetResult.Unwrap());");
        sb.AppendLine("        return SerializeRow(row);");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    public void EvictDrop(byte[] keyBytes) {");
        sb.AppendLine("        var id = DeserializeKey(keyBytes);");
        sb.AppendLine("        var offsetResult = primaryIndex.GetOffset(id);");
        sb.AppendLine("        if (offsetResult.IsError()) return;");
        sb.AppendLine("        var offset = offsetResult.Unwrap();");
        sb.AppendLine("        var row = storage.Get(offset);");
        sb.AppendLine("        var lastOffset = storage.LastOffset;");
        sb.AppendLine("        var swapped = storage.Delete(offset);");
        sb.AppendLine("        primaryIndex.Delete(id);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("row", idx) : $"{KeyExpr("row", idx)}, offset";
            sb.AppendLine($"        {IndexFieldName(idx)}.Delete({deleteArgs});");
        }
        sb.AppendLine("        if (swapped is { } swappedRow) {");
        sb.AppendLine($"            var swappedPk = swappedRow.{table.PrimaryKeyName};");
        sb.AppendLine("            primaryIndex.Delete(swappedPk);");
        sb.AppendLine("            primaryIndex.Insert(swappedPk, offset);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("swappedRow", idx) : $"{KeyExpr("swappedRow", idx)}, lastOffset";
            sb.AppendLine($"            {IndexFieldName(idx)}.Delete({deleteArgs});");
            sb.AppendLine($"            {IndexFieldName(idx)}.Insert({KeyExpr("swappedRow", idx)}, offset);");
        }
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    static private string EmitDatabase(DatabaseModel database, ImmutableArray<TableModel> tables, DatabaseContractDescriptor? descriptor) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using RhinoDB.Core;");
        sb.AppendLine("using RhinoDB.Core.Exceptions;");
        sb.AppendLine("using RhinoDB.Lib.Changes;");
        sb.AppendLine("using RhinoDB.Lib.Cold;");
        sb.AppendLine("using RhinoDB.Lib.Durability;");
        sb.AppendLine("using RhinoDB.Lib.Execution;");
        sb.AppendLine("using RhinoDB.Lib.Indexing;");
        sb.AppendLine("using RhinoDB.Lib.Storage;");
        sb.AppendLine("using RhinoDB.Lib.Tables;");
        sb.AppendLine();
        if (database.Namespace is not null) {
            sb.AppendLine($"namespace {database.Namespace};");
            sb.AppendLine();
        }

        var txName = $"{database.SimpleName}Transaction";
        var txInstantTables = tables.Where(t => t.Kind == TableKind.Instant).ToImmutableArray();

        sb.AppendLine($"public sealed class {txName} : ITransaction {{");
        foreach (var table in tables)
            sb.AppendLine($"    private readonly {database.SimpleName}{table.Accessor}Ops {Camel(table.Accessor)}Ops;");
        foreach (var table in tables.Where(t => t.Kind == TableKind.Persistent))
            sb.AppendLine($"    public {database.SimpleName}{table.Accessor}Ops {table.Accessor} => {Camel(table.Accessor)}Ops;");
        if (txInstantTables.Length > 0) sb.AppendLine("    public InstantAccessor Instant { get; }");
        sb.AppendLine("    private readonly LsnSequence lsnSequence;");
        sb.AppendLine("    public ulong? LastLsn { get; private set; }");
        sb.AppendLine();
        sb.Append($"    public {txName}(");
        sb.Append(string.Join(", ", tables.Select(t => $"{database.SimpleName}{t.Accessor}Ops {Camel(t.Accessor)}")));
        sb.Append(tables.Length > 0 ? ", " : "");
        sb.Append("LsnSequence lsnSequence");
        sb.AppendLine(") {");
        foreach (var table in tables)
            sb.AppendLine($"        {Camel(table.Accessor)}Ops = {Camel(table.Accessor)};");
        if (txInstantTables.Length > 0)
            sb.AppendLine($"        Instant = new InstantAccessor({string.Join(", ", txInstantTables.Select(t => $"{Camel(t.Accessor)}Ops"))});");
        sb.AppendLine("        this.lsnSequence = lsnSequence;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public Result Apply() {");
        foreach (var table in tables)
            sb.AppendLine($"        if ({Camel(table.Accessor)}Ops.Dirty && !{Camel(table.Accessor)}Ops.Validate()) return Result.Error({Camel(table.Accessor)}Ops.LastError);");
        var anyDirtyExpr = tables.Length > 0 ? string.Join(" || ", tables.Select(t => $"{Camel(t.Accessor)}Ops.Dirty")) : "false";
        sb.AppendLine($"        var anyDirty = {anyDirtyExpr};");
        sb.AppendLine("        LastLsn = anyDirty ? lsnSequence.Next() : null;");
        sb.AppendLine("        try {");
        foreach (var table in tables)
            sb.AppendLine($"            if ({Camel(table.Accessor)}Ops.Dirty) {Camel(table.Accessor)}Ops.Apply(LastLsn ?? 0UL);");
        foreach (var table in tables)
            sb.AppendLine($"            {Camel(table.Accessor)}Ops.ReleaseUndoJournal();");
        sb.AppendLine("        }");
        sb.AppendLine("        catch (Exception ex) {");
        sb.AppendLine("            var reverted = true;");
        foreach (var table in tables.Reverse()) {
            sb.AppendLine($"            if ({Camel(table.Accessor)}Ops.AppliedInOperation && !{Camel(table.Accessor)}Ops.RevertUndo()) {{");
            sb.AppendLine("                reverted = false;");
            sb.AppendLine("            }");
        }
        sb.AppendLine("            if (!reverted) throw new ApplyFailedException(ex);");
        sb.AppendLine("            return Result.Error(DbError.ApplyFailedButRevertedSuccessfully(ex));");
        sb.AppendLine("        }");
        sb.AppendLine("        return Result.Ok();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public int PendingStorageOrphanCount {");
        sb.AppendLine("        get {");
        sb.AppendLine("            var total = 0;");
        foreach (var table in tables)
            sb.AppendLine($"            total += {Camel(table.Accessor)}Ops.PendingStorageOrphanCount;");
        sb.AppendLine("            return total;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public void SweepDeleted() {");
        foreach (var table in tables)
            sb.AppendLine($"        {Camel(table.Accessor)}Ops.SweepDeleted();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public void Discard() {");
        foreach (var table in tables)
            sb.AppendLine($"        if ({Camel(table.Accessor)}Ops.Dirty) {Camel(table.Accessor)}Ops.Discard();");
        sb.AppendLine("    }");
        if (txInstantTables.Length > 0) {
            sb.AppendLine();
            sb.AppendLine("    public readonly struct InstantAccessor(" + string.Join(", ", txInstantTables.Select(t => $"{database.SimpleName}{t.Accessor}Ops {Camel(t.Accessor)}Ops")) + ") {");
            foreach (var table in txInstantTables)
                sb.AppendLine($"        public {database.SimpleName}{table.Accessor}Ops {table.Accessor} => {Camel(table.Accessor)}Ops;");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
        sb.AppendLine();

        var instantTables = tables.Where(t => t.Kind == TableKind.Instant).ToImmutableArray();
        var persistentTables = tables.Where(t => t.Kind == TableKind.Persistent).ToImmutableArray();

        var databaseDescriptor = descriptor?.Databases.FirstOrDefault(d => d.FullName == database.FullName);
        var gBinary = databaseDescriptor?.Generation ?? 0;
        var retainedFromGeneration = databaseDescriptor?.RetainedFromGeneration ?? 0;

        sb.AppendLine($"public partial class {database.SimpleName} {{");
        sb.AppendLine($"    public const int G_binary = {gBinary};");
        sb.AppendLine($"    public const int RetainedFromGeneration = {retainedFromGeneration};");
        sb.AppendLine($"    private static readonly HashSet<int> InvalidGenerations = new HashSet<int> {{ {string.Join(", ", database.InvalidGenerations)} }};");
        sb.AppendLine("    public static bool IsGenerationInvalid(int generation) => InvalidGenerations.Contains(generation);");
        sb.AppendLine();
        foreach (var table in instantTables) {
            sb.AppendLine($"    private readonly DenseArray<{table.RowTypeFullName}> {Camel(table.Accessor)}Storage = new(chunkSize: {table.ChunkSize});");
            sb.AppendLine($"    private readonly {PrimaryIndexType(table)} {Camel(table.Accessor)}PrimaryIndex = new();");
            foreach (var aif in table.AutoIncrementFields)
                sb.AppendLine($"    private readonly AutoIncrementCounter {Camel(table.Accessor)}{aif.FieldName}Counter = new();");
            foreach (var idx in table.Indexes) {
                var fieldName = $"{Camel(table.Accessor)}{idx.AccessorName}Index";
                sb.AppendLine($"    private readonly {ConcreteIndexType(idx)} {fieldName} = new();");
            }
        }
        foreach (var table in persistentTables) {
            sb.AppendLine($"    private readonly DenseArray<{table.RowTypeFullName}> {Camel(table.Accessor)}Storage = new(chunkSize: {table.ChunkSize});");
            sb.AppendLine($"    private readonly {PrimaryIndexType(table)} {Camel(table.Accessor)}PrimaryIndex = new();");
            sb.AppendLine($"    private readonly ColdTable<{table.PrimaryKeyTypeFullName}, {table.RowTypeFullName}> {Camel(table.Accessor)}ColdTable;");
            foreach (var aif in table.AutoIncrementFields)
                sb.AppendLine($"    private readonly AutoIncrementCounter {Camel(table.Accessor)}{aif.FieldName}Counter = new();");
            foreach (var idx in table.Indexes) {
                var fieldName = $"{Camel(table.Accessor)}{idx.AccessorName}Index";
                sb.AppendLine($"    private readonly {ConcreteIndexType(idx)} {fieldName} = new();");
            }
        }

        if (persistentTables.Length > 0) sb.AppendLine("    private readonly ColdStore cold;");
        foreach (var table in tables.Where(t => t.RingBufferCapacity > 0))
            sb.AppendLine($"    private readonly ChangeRingBuffer {Camel(table.Accessor)}Ring;");
        foreach (var table in tables)
            sb.AppendLine($"    private readonly {database.SimpleName}{table.Accessor}Ops {Camel(table.Accessor)}Ops;");
        sb.AppendLine($"    private readonly {txName} cachedTransaction;");
        sb.AppendLine();

        var ctorSignature = persistentTables.Length > 0
            ? $"    public {database.SimpleName}(ColdStore cold) : base(cold) {{"
            : $"    public {database.SimpleName}() {{";
        sb.AppendLine(ctorSignature);
        if (persistentTables.Length > 0) {
            sb.AppendLine("        this.cold = cold;");
            foreach (var table in persistentTables) {
                var opsName = $"{database.SimpleName}{table.Accessor}Ops";
                sb.AppendLine($"        {Camel(table.Accessor)}ColdTable = cold.OpenTable<{table.PrimaryKeyTypeFullName}, {table.RowTypeFullName}>(\"{table.Accessor}\", {opsName}.SerializeKey, {opsName}.DeserializeKey, {opsName}.DeserializeRow);");
            }
        }
        foreach (var table in tables.Where(t => t.RingBufferCapacity > 0))
            sb.AppendLine($"        {Camel(table.Accessor)}Ring = new ChangeRingBuffer({table.RingBufferCapacity});");
        foreach (var table in tables) {
            var args = new List<string> {
                $"{Camel(table.Accessor)}Storage",
                $"{Camel(table.Accessor)}PrimaryIndex",
            };
            if (table.Kind == TableKind.Persistent) {
                args.Add($"{Camel(table.Accessor)}ColdTable");
                args.Add("cold");
            }
            if (table.RingBufferCapacity > 0) args.Add($"{Camel(table.Accessor)}Ring");
            foreach (var aif in table.AutoIncrementFields) args.Add($"{Camel(table.Accessor)}{aif.FieldName}Counter");
            foreach (var idx in table.Indexes) args.Add($"{Camel(table.Accessor)}{idx.AccessorName}Index");
            sb.AppendLine($"        {Camel(table.Accessor)}Ops = new {database.SimpleName}{table.Accessor}Ops({string.Join(", ", args)});");
        }
        sb.AppendLine($"        var lsnSequence = {(persistentTables.Length > 0 ? "new LsnSequence(cold.RecoveredLsn)" : "new LsnSequence(0)")};");
        sb.Append($"        cachedTransaction = new {txName}(");
        sb.Append(string.Join(", ", tables.Select(t => $"{Camel(t.Accessor)}Ops")));
        sb.Append(tables.Length > 0 ? ", " : "");
        sb.Append("lsnSequence");
        sb.AppendLine(");");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    protected override {txName} CreateTransaction() => cachedTransaction;");

        if (persistentTables.Length > 0) sb.AppendLine($"    public {txName} CreateLoaderTransaction() => CreateTransaction();");

        if (persistentTables.Length > 0) {
            sb.AppendLine();
            sb.AppendLine("    public void ReplayChange(WalChange change, int generation) {");
            sb.AppendLine("        switch (change.TableId) {");
            foreach (var table in persistentTables) {
                var opsName = $"{database.SimpleName}{table.Accessor}Ops";
                sb.AppendLine($"            case {ComputeTableId(table.Accessor)}u: {{");
                sb.AppendLine($"                var fromRevision = {opsName}.RevisionAtGeneration(generation);");
                sb.AppendLine("                var oldRowBytes = change.Row;");
                sb.AppendLine($"                var rowBytes = oldRowBytes is null ? null : {opsName}.SerializeRow({opsName}.MigrateToCurrentRevision(fromRevision, oldRowBytes));");
                sb.AppendLine($"                {Camel(table.Accessor)}Ops.ReplayApply(change.Kind, change.Key, rowBytes);");
                sb.AppendLine("                break;");
                sb.AppendLine("            }");
            }
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }

        if (persistentTables.Length > 0) {
            sb.AppendLine();
            sb.AppendLine("    public Result<int> CurrentGeneration() => cold.ReadGeneration();");
        }

        if (persistentTables.Length > 0) {
            var orphanedTables = descriptor?.Tables
                .Where(t => t.DatabaseFullName == database.FullName && t.RemovedAtGeneration is not null && !tables.Any(live => live.Accessor == t.Accessor))
                .ToImmutableArray() ?? ImmutableArray<TableDescriptor>.Empty;

            sb.AppendLine();
            sb.AppendLine("    public Result RunMigration() {");
            sb.AppendLine("        var currentGenerationResult = cold.ReadGeneration();");
            sb.AppendLine("        if (currentGenerationResult.IsError()) return currentGenerationResult.Void();");
            sb.AppendLine("        var currentGeneration = currentGenerationResult.Unwrap();");
            sb.AppendLine();
            sb.AppendLine("        var tableRewrites = new List<(string TableName, Func<byte[], byte[], (byte[] Key, byte[] Row)> Transform)>();");
            foreach (var table in persistentTables) {
                var opsName = $"{database.SimpleName}{table.Accessor}Ops";
                var fromRevisionVar = $"{Camel(table.Accessor)}FromRevision";
                sb.AppendLine($"        var {fromRevisionVar} = {opsName}.RevisionAtGeneration(currentGeneration);");
                sb.AppendLine($"        tableRewrites.Add((\"{table.Accessor}\", (keyBytes, rowBytes) => {{");
                sb.AppendLine($"            var migratedRow = {opsName}.MigrateToCurrentRevision({fromRevisionVar}, rowBytes);");
                sb.AppendLine($"            return ({opsName}.SerializeKey(migratedRow.{table.PrimaryKeyName}), {opsName}.SerializeRow(migratedRow));");
                sb.AppendLine("        }));");
            }
            sb.AppendLine();
            sb.AppendLine("        var orphanTablesToDrop = new List<string>();");
            foreach (var orphan in orphanedTables)
                sb.AppendLine($"        if ({orphan.RemovedAtGeneration} < currentGeneration) orphanTablesToDrop.Add(\"{orphan.Accessor}\");");
            sb.AppendLine();
            sb.AppendLine("        return cold.RunMigration(G_binary, tableRewrites, orphanTablesToDrop);");
            sb.AppendLine("    }");

            sb.AppendLine();
            sb.AppendLine("    public Result MigrateWalArchive() {");
            sb.AppendLine("        var currentGenerationResult = cold.ReadGeneration();");
            sb.AppendLine("        if (currentGenerationResult.IsError()) return currentGenerationResult.Void();");
            sb.AppendLine("        var targetGeneration = currentGenerationResult.Unwrap();");
            sb.AppendLine();
            sb.AppendLine("        var transformResult = WalArchive.MigrateSegments(cold.DirectoryPath, targetGeneration, (tableId, fromGeneration, kind, key, row) => {");
            sb.AppendLine("            switch (tableId) {");
            foreach (var table in persistentTables) {
                var opsName = $"{database.SimpleName}{table.Accessor}Ops";
                sb.AppendLine($"                case {ComputeTableId(table.Accessor)}u: {{");
                sb.AppendLine("                    if (kind == ChangeKind.Delete) return (key, row);");
                sb.AppendLine($"                    var fromRevision = {opsName}.RevisionAtGeneration(fromGeneration);");
                sb.AppendLine($"                    var migratedRow = {opsName}.MigrateToCurrentRevision(fromRevision, row!);");
                sb.AppendLine($"                    return ({opsName}.SerializeKey(migratedRow.{table.PrimaryKeyName}), {opsName}.SerializeRow(migratedRow));");
                sb.AppendLine("                }");
            }
            sb.AppendLine("                default: return null;");
            sb.AppendLine("            }");
            sb.AppendLine("        });");
            sb.AppendLine("        if (transformResult.IsError()) return transformResult;");
            sb.AppendLine();
            sb.AppendLine("        return cold.WriteRetainedFromGeneration(targetGeneration);");
            sb.AppendLine("    }");
        }

        sb.AppendLine("}");

        if (persistentTables.Length > 0) EmitLoader(sb, database, persistentTables);

        return sb.ToString();
    }

    static private void EmitLoader(StringBuilder sb, DatabaseModel database, ImmutableArray<TableModel> persistentTables) {
        var loaderName = $"{database.SimpleName}Loader";
        var nonEvictable = persistentTables.Where(t => !t.Evictable).ToImmutableArray();

        sb.AppendLine();
        sb.AppendLine($"public class {loaderName} {{");
        sb.AppendLine($"    public virtual Task LoadAsync({database.SimpleName} db) {{");
        sb.AppendLine("        var tx = db.CreateLoaderTransaction();");
        sb.AppendLine("        var tasks = new List<Task>();");
        foreach (var table in nonEvictable)
            sb.AppendLine($"        tasks.Add(Task.Run(() => tx.{table.Accessor}.BulkLoadFromCold()));");
        sb.AppendLine("        return Task.WhenAll(tasks);");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    public virtual Result LoadFromGenesis({database.SimpleName} db, ColdStore cold, ulong? upToLsn = null, Action<DecodedWalEntry>? onEntryApplied = null) {{");
        sb.AppendLine("        var oldestGenerationResult = WalArchive.ReadOldestRetainedGeneration(cold.DirectoryPath);");
        sb.AppendLine("        if (oldestGenerationResult.IsError()) return oldestGenerationResult.Void();");
        sb.AppendLine($"        if (oldestGenerationResult.Unwrap().TryGet(out var oldestGeneration) && oldestGeneration < {database.SimpleName}.RetainedFromGeneration)");
        sb.AppendLine("            return Result.Error(DbError.ArchiveOlderThanRetentionFloor());");
        sb.AppendLine();
        sb.AppendLine("        var historyResult = WalArchive.ReadHistory(cold.DirectoryPath, cold.PendingWalTail, cold.WalGeneration);");
        sb.AppendLine("        if (historyResult.IsError()) return historyResult.Void();");
        sb.AppendLine();
        sb.AppendLine("        foreach (var (entry, generation) in historyResult.Unwrap()) {");
        sb.AppendLine("            if (upToLsn is { } stop && entry.Lsn > stop) break;");
        sb.AppendLine("            foreach (var change in entry.Changes) db.ReplayChange(change, generation);");
        sb.AppendLine("            onEntryApplied?.Invoke(entry);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return Result.Ok();");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    static internal string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
    
    static private bool IsRangedIndex(IndexModel indexModel) {
        return IsRangedIndex(indexModel.Kind, indexModel.Uniqueness);
    }
    
    static private bool IsRangedIndex(IndexKind kind, Uniqueness _) {
        return kind is IndexKind.BTree;
    }
    
    static private bool IsMultiReturnButNotRangedIndex(IndexModel indexModel) {
        return IsMultiReturnButNotRangedIndex(indexModel.Kind, indexModel.Uniqueness);
    }
    
    static private bool IsMultiReturnButNotRangedIndex(IndexKind kind, Uniqueness uniqueness) {
        return (kind, uniqueness) switch {
            (IndexKind.Hash, Uniqueness.NonUnique) => true,
            _ => false
        };
    }

    private sealed class TableModel
    (
        string rowTypeFullName,
        string? rowNamespace,
        TableKind kind,
        string ownerDatabaseFullName,
        string primaryKeyName,
        string primaryKeyTypeFullName,
        IndexKind primaryKeyKind,
        string accessor,
        int chunkSize,
        bool evictable,
        int ringBufferCapacity,
        ImmutableArray<AutoIncrementFieldModel> autoIncrementFields,
        ImmutableArray<IndexModel> indexes,
        ImmutableArray<string> validateMethodNames,
        ImmutableArray<MigrationMethodModel> migrationMethods,
        ImmutableArray<DowngradeMethodModel> downgradeMethods,
        ImmutableArray<int> invalidRevisions,
        List<FieldDescriptor> descriptorFields,
        ImmutableArray<RowFieldModel> fields
    ) {
        public string RowTypeFullName { get; } = rowTypeFullName;
        public string? RowNamespace { get; } = rowNamespace;
        public TableKind Kind { get; } = kind;
        public string OwnerDatabaseFullName { get; } = ownerDatabaseFullName;
        public string PrimaryKeyName { get; } = primaryKeyName;
        public string PrimaryKeyTypeFullName { get; } = primaryKeyTypeFullName;
        public IndexKind PrimaryKeyKind { get; } = primaryKeyKind;
        public string Accessor { get; } = accessor;
        public int ChunkSize { get; } = chunkSize;
        public bool Evictable { get; } = evictable;
        public int RingBufferCapacity { get; } = ringBufferCapacity;
        public ImmutableArray<AutoIncrementFieldModel> AutoIncrementFields { get; } = autoIncrementFields;
        public ImmutableArray<IndexModel> Indexes { get; } = indexes;
        public ImmutableArray<string> ValidateMethodNames { get; } = validateMethodNames;
        public ImmutableArray<MigrationMethodModel> MigrationMethods { get; } = migrationMethods;
        public ImmutableArray<DowngradeMethodModel> DowngradeMethods { get; } = downgradeMethods;
        public ImmutableArray<int> InvalidRevisions { get; } = invalidRevisions;
        public List<FieldDescriptor> DescriptorFields { get; } = descriptorFields;
        public ImmutableArray<RowFieldModel> Fields { get; } = fields;
    }

    private sealed class MigrationMethodModel(int fromRevision, string methodName, string paramTypeFullName, string returnTypeFullName) {
        public int FromRevision { get; } = fromRevision;
        public string MethodName { get; } = methodName;
        public string ParamTypeFullName { get; } = paramTypeFullName;
        public string ReturnTypeFullName { get; } = returnTypeFullName;
    }

    private sealed class DowngradeMethodModel(int toRevision, string methodName, string paramTypeFullName, string returnTypeFullName) {
        public int ToRevision { get; } = toRevision;
        public string MethodName { get; } = methodName;
        public string ParamTypeFullName { get; } = paramTypeFullName;
        public string ReturnTypeFullName { get; } = returnTypeFullName;
    }

    private sealed class AutoIncrementFieldModel(string fieldName, string fieldTypeFullName, bool isSigned) {
        public string FieldName { get; } = fieldName;
        public string FieldTypeFullName { get; } = fieldTypeFullName;
        public bool IsSigned { get; } = isSigned;
    }

    private sealed class IndexModel(string accessorName, IndexKind kind, Uniqueness uniqueness, ImmutableArray<IndexFieldModel> fields) {
        public string AccessorName { get; } = accessorName;
        public IndexKind Kind { get; } = kind;
        public Uniqueness Uniqueness { get; } = uniqueness;
        public ImmutableArray<IndexFieldModel> Fields { get; } = fields;
    }

    private sealed class IndexFieldModel(string fieldName, string fieldTypeFullName) {
        public string FieldName { get; } = fieldName;
        public string FieldTypeFullName { get; } = fieldTypeFullName;
    }

}
