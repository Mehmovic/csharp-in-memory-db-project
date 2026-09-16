using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RhinoDB.Generators;

[Generator]
public sealed class TableGenerator : IIncrementalGenerator {
    private const string TableAttributeFullName = "RhinoDB.Core.Tables.TableAttribute";
    private const string DatabaseAttributeFullName = "RhinoDB.Core.Tables.DatabaseAttribute";
    private const string PrimaryKeyAttributeFullName = "RhinoDB.Core.Tables.PrimaryKeyAttribute";
    private const string AutoIncrementAttributeFullName = "RhinoDB.Core.Tables.AutoIncrementAttribute";
    private const string IndexAttributeFullName = "RhinoDB.Core.Tables.IndexAttribute";
    private const string ValidateAttributeFullName = "RhinoDB.Core.Tables.ValidateAttribute";
    private const string DbErrorFullName = "RhinoDB.Core.DbError";

    static private readonly DiagnosticDescriptor MissingPrimaryKeyDiagnostic = new(
        "RHINO001",
        "Table row missing [PrimaryKey]",
        "Row type '{0}' is [Table]-attributed but declares no [PrimaryKey] parameter",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor EmptyAccessorDiagnostic = new(
        "RHINO002",
        "Empty Accessor name",
        "{0} has an explicit Accessor that is an empty string - omit Accessor for the default name, or give it a real one",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor CompositeIndexKindMismatchDiagnostic = new(
        "RHINO003",
        "Composite index fields disagree on Kind/Uniqueness",
        "Fields sharing Accessor '{0}' on '{1}' must all declare the same IndexKind and Uniqueness",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor CompositeIndexTooManyFieldsDiagnostic = new(
        "RHINO004",
        "Composite index has too many fields",
        "Composite index '{0}' on '{1}' has {2} fields sharing one Accessor - at most 3 are supported",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor DuplicateOrderDiagnostic = new(
        "RHINO005",
        "Duplicate explicit Order in composite index",
        "Composite index '{0}' on '{1}' has two or more fields with the same explicit Order value",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor InvalidAutoIncrementTypeDiagnostic = new(
        "RHINO007",
        "AutoIncrement field must be an incrementable unmanaged integer type",
        "'{0}.{1}' is [AutoIncrement] but its type isn't one of sbyte/byte/short/ushort/int/uint/long/ulong - " + "AutoIncrement needs a type the system can generate a new value for",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor EvictableOnInstantKindDiagnostic = new(
        "RHINO008",
        "Evictable has no effect on Instant-kind tables",
        "'{0}' is TableKind.Instant and sets Evictable = true - Instant-kind tables have no cold storage " + "to evict to/from at all. Remove Evictable or use TableKind.Persistent.",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor InvalidValidateMethodSignatureDiagnostic = new(
        "RHINO009",
        "Invalid [Validate] method signature",
        "'{0}.{1}' is [Validate] but must be an at-least-internal static method shaped 'static DbError? {1}({0} row)' - "
        + "generated code calls it from a sibling class in the same assembly, so it can't be private",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor TableIdCollisionDiagnostic = new(
        "RHINO011",
        "Table id hash collision within one database",
        "{0} has two tables whose Accessors hash to the same tableId: {1} and {2} - rename one Accessor, since the tableId (FNV-1a of the Accessor) identifies tables in the WAL and in cold storage",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    static private readonly DiagnosticDescriptor DuplicateAccessorDiagnostic = new(
        "RHINO010",
        "Duplicate table Accessor within one database",
        "'{0}' has two or more tables using Accessor '{1}' - each table's Accessor must be unique within its owning database",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var tableResults = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                TableAttributeFullName,
                predicate: static (node, _) => node is StructDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (ctx, _) => ToTableModels(ctx)
            )
            .SelectMany(static (results, _) => results);

        context.RegisterSourceOutput(
            tableResults,
            static (spc, result) => {
                foreach (var diagnostic in result.Diagnostics) spc.ReportDiagnostic(diagnostic);
            }
        );

        var tables = tableResults.Collect();

        var databases = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                DatabaseAttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => ToDatabaseModel(ctx)
            );

        var combined = databases.Combine(tables);
        context.RegisterSourceOutput(combined, static (spc, pair) => Emit(spc, pair.Left, pair.Right));
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

    static private bool BoolNamedArg(AttributeData attr, string name) {
        foreach (var kv in attr.NamedArguments)
            if (kv.Key == name)
                return (bool)kv.Value.Value!;
        return false;
    }

    static private Location Loc(AttributeData attr) => attr.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? Location.None;

    static private bool? ClassifyAutoIncrementType(ITypeSymbol type) => type.SpecialType switch {
        SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64 => true,
        SpecialType.System_Byte or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64 => false,
        _ => null,
    };

    static private ImmutableArray<(TableModel? Model, ImmutableArray<Diagnostic> Diagnostics)> ToTableModels(GeneratorAttributeSyntaxContext ctx) {
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

        var primaryKeyAttribute = primaryKeyParam.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == PrimaryKeyAttributeFullName);
        var primaryKeyKind = primaryKeyAttribute.ConstructorArguments.Length > 0
            ? (IndexKind)(int)primaryKeyAttribute.ConstructorArguments[0].Value!
            : IndexKind.Hash;
        var (pkAccessorProvided, pkAccessorValue) = StringNamedArg(primaryKeyAttribute, "Accessor");
        if (pkAccessorProvided && pkAccessorValue == "")
            rowDiagnostics.Add(Diagnostic.Create(EmptyAccessorDiagnostic, Loc(primaryKeyAttribute), $"[PrimaryKey] on '{rowType.Name}.{primaryKeyParam.Name}'"));
        var primaryKeyAccessor = pkAccessorProvided && pkAccessorValue != "" ? pkAccessorValue! : "Get";

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
            .Where(x => x.Param.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == IndexAttributeFullName))
            .Select(x => {
                    var indexAttribute = x.Param.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == IndexAttributeFullName);
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
                }
            )
            .ToImmutableArray();

        var indexes = indexedParams
            .GroupBy(x => x.Accessor)
            .Select(g => {
                    var group = g.ToImmutableArray();

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

        if (rowDiagnostics.Count > 0) return ImmutableArray.Create<(TableModel?, ImmutableArray<Diagnostic>)>((null, rowDiagnostics.ToImmutable()));

        var results = ImmutableArray.CreateBuilder<(TableModel? Model, ImmutableArray<Diagnostic> Diagnostics)>();
        foreach (var attribute in ctx.Attributes) {
            var attrDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

            var kind = (TableKind)(int)attribute.ConstructorArguments[0].Value!;
            var databaseType = (INamedTypeSymbol)attribute.ConstructorArguments[1].Value!;

            var (tableAccessorProvided, tableAccessorValue) = StringNamedArg(attribute, "Accessor");
            if (tableAccessorProvided && tableAccessorValue == "")
                attrDiagnostics.Add(Diagnostic.Create(EmptyAccessorDiagnostic, Loc(attribute), $"[Table] on '{rowType.Name}'"));
            var tableAccessor = tableAccessorProvided && tableAccessorValue != "" ? tableAccessorValue! : rowType.Name;

            var (chunkSizeProvided, chunkSizeValue) = IntNamedArg(attribute, "ChunkSize");
            var chunkSize = chunkSizeProvided ? chunkSizeValue : 4096;

            var evictable = BoolNamedArg(attribute, "Evictable");
            if (evictable && kind == TableKind.Instant)
                attrDiagnostics.Add(Diagnostic.Create(EvictableOnInstantKindDiagnostic, Loc(attribute), rowType.Name));

            if (attrDiagnostics.Count > 0) {
                results.Add((null, attrDiagnostics.ToImmutable()));
                continue;
            }

            var model = new TableModel(
                rowType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                rowType.ContainingNamespace.IsGlobalNamespace ? null : rowType.ContainingNamespace.ToDisplayString(),
                kind,
                databaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                primaryKeyParam.Name,
                primaryKeyParam.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                primaryKeyKind,
                primaryKeyAccessor,
                tableAccessor,
                chunkSize,
                evictable,
                autoIncrementFields.ToImmutable(),
                indexes,
                validateMethodNames.ToImmutable()
            );
            results.Add((model, ImmutableArray<Diagnostic>.Empty));
        }
        return results.ToImmutable();
    }

    static private DatabaseModel ToDatabaseModel(GeneratorAttributeSyntaxContext ctx) {
        var databaseType = (INamedTypeSymbol)ctx.TargetSymbol;
        return new DatabaseModel(
            databaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            databaseType.Name,
            databaseType.ContainingNamespace.IsGlobalNamespace ? null : databaseType.ContainingNamespace.ToDisplayString()
        );
    }

    static private void Emit(SourceProductionContext context, DatabaseModel database, ImmutableArray<(TableModel? Model, ImmutableArray<Diagnostic> Diagnostics)> allResults) {
        var tables = allResults
            .Select(r => r.Model)
            .Where(m => m is not null)
            .Select(m => m!)
            .Where(t => t.OwnerDatabaseFullName == database.FullName)
            .ToImmutableArray();

        var duplicateAccessors = tables.GroupBy(t => t.Accessor).Where(g => g.Count() > 1).Select(g => g.Key).ToImmutableArray();
        if (duplicateAccessors.Length > 0) {
            foreach (var accessor in duplicateAccessors)
                context.ReportDiagnostic(Diagnostic.Create(DuplicateAccessorDiagnostic, Location.None, database.SimpleName, accessor));
            return;
        }

        var collidingTableIds = tables.GroupBy(t => ComputeTableId(t.Accessor)).Where(g => g.Count() > 1).ToImmutableArray();
        if (collidingTableIds.Length > 0) {
            foreach (var group in collidingTableIds) {
                var colliding = group.Select(t => t.Accessor).ToImmutableArray();
                context.ReportDiagnostic(Diagnostic.Create(TableIdCollisionDiagnostic, Location.None, database.SimpleName, colliding[0], colliding[1]));
            }
            return;
        }
        
        foreach (var table in tables)
            context.AddSource($"{database.SimpleName}{table.Accessor}Ops.g.cs", EmitOpsClass(table, database.SimpleName));

        context.AddSource($"{database.SimpleName}.g.cs", EmitDatabase(database, tables));
    }

    static private string PrimaryIndexType(TableModel table) {
        var keyType = table.PrimaryKeyTypeFullName;
        return table.PrimaryKeyKind switch {
            IndexKind.Hash => $"HashIndex<{keyType}>",
            IndexKind.BTree => $"BTreeIndex<{keyType}>",
            IndexKind.RedBlackTree =>$"RedBlackTreeIndex<{keyType}>",
            _ => throw new Exception("Invalid index kind")
        };
    }

    static private string ConcreteIndexType(IndexModel idx) {
        var keyType = KeyType(idx);
        return (idx.Kind, idx.Uniqueness) switch {
            (IndexKind.Hash, Uniqueness.Unique) => $"HashIndex<{keyType}>",
            (IndexKind.Hash, Uniqueness.NonUnique) => $"NonUniqueHashIndex<{keyType}>",

            (IndexKind.BTree, Uniqueness.Unique) => $"BTreeIndex<{keyType}>",
            (IndexKind.BTree, Uniqueness.NonUnique) => $"NonUniqueBTreeIndex<{keyType}>",

            (IndexKind.RedBlackTree, Uniqueness.Unique) => $"RedBlackTreeIndex<{keyType}>",
            (IndexKind.RedBlackTree, Uniqueness.NonUnique) => $"NonUniqueRedBlackTreeIndex<{keyType}>",
            
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

    static private uint ComputeTableId(string accessor) {
        return Encoding.UTF8.GetBytes(accessor)
            .Aggregate(2166136261u, (current, b) => (current ^ b) * 16777619u);
    }

    static private string EmitOpsClass(TableModel table, string ownerSimpleName) =>
        table.Kind == TableKind.Persistent ? EmitPersistentOpsClass(table, ownerSimpleName) : EmitInstantOpsClass(table, ownerSimpleName);

    static private void EmitOpsClassHeader(StringBuilder sb, TableModel table, bool isPersistent) {
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("using RhinoDB.Core;");
        if (isPersistent) {
            sb.AppendLine("using MemoryPack;");
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
        if (table.Indexes.Any(ShouldCreateOffsetBuffer))
            sb.AppendLine("    private readonly List<int> offsetBuffer = [];");

        return;
        static bool ShouldCreateOffsetBuffer(IndexModel indexModel) {
            return indexModel.Uniqueness != Uniqueness.Unique ||
                   indexModel.Kind is IndexKind.BTree or IndexKind.RedBlackTree;
        }
    }

    static private void EmitChangeTrackingFields(StringBuilder sb, string key, string row) {
        sb.AppendLine($"    private readonly List<Change<{key}, {row}>> changes = [];");
        sb.AppendLine("    public bool Dirty { get; private set; }");
        sb.AppendLine("    private DbError lastError;");
        sb.AppendLine("    internal DbError LastError => lastError;");
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

    static private void EmitGetMethod(StringBuilder sb, TableModel table) {
        var row = table.RowTypeFullName;
        var key = table.PrimaryKeyTypeFullName;
        sb.AppendLine($"    public Result<{row}> {table.PrimaryKeyAccessor}({key} id) {{");
        sb.AppendLine("        var span = CollectionsMarshal.AsSpan(changes);");
        sb.AppendLine("        for (var i = span.Length - 1; i >= 0; i--) {");
        sb.AppendLine("            ref readonly var c = ref span[i];");
        sb.AppendLine("            if (!c.Key.Equals(id)) continue;");
        sb.AppendLine($"            return c.Kind == ChangeKind.Delete");
        sb.AppendLine($"                ? Result<{row}>.Error(DbError.IndexKeyNotFound())");
        sb.AppendLine($"                : Result<{row}>.Ok(c.Row);");
        sb.AppendLine("        }");
        sb.AppendLine("        var offsetResult = primaryIndex.GetOffset(id);");
        sb.AppendLine("        if (offsetResult.IsError()) return offsetResult.Void();");
        sb.AppendLine("        return storage.Get(offsetResult.Unwrap());");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static private void EmitIterMethod(StringBuilder sb, TableModel table) {
        sb.AppendLine($"    public IEnumerable<{table.RowTypeFullName}> Iter() {{");
        sb.AppendLine("        for (var i = 0; i < storage.Count; i++) yield return storage.Get(i);");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static private void EmitSecondaryIndexAccessors(StringBuilder sb, TableModel table) {
        var row = table.RowTypeFullName;
        foreach (var idx in table.Indexes) {
            var parameters = string.Join(", ", idx.Fields.Select(f => $"{f.FieldTypeFullName} {Camel(f.FieldName)}"));
            var keyExpr = idx.Fields.Length == 1
                ? Camel(idx.Fields[0].FieldName)
                : $"({string.Join(", ", idx.Fields.Select(f => Camel(f.FieldName)))})";
            var rowKeyExpr = KeyExpr("c.Row", idx);
            if (idx.Uniqueness == Uniqueness.Unique) {
                sb.AppendLine($"    public Result<{row}> {idx.AccessorName}({parameters}) {{");
                sb.AppendLine("        if (Dirty) {");
                sb.AppendLine("            var span = CollectionsMarshal.AsSpan(changes);");
                sb.AppendLine("            for (var i = span.Length - 1; i >= 0; i--) {");
                sb.AppendLine("                ref readonly var c = ref span[i];");
                sb.AppendLine("                var supersededByLater = false;");
                sb.AppendLine("                for (var j = span.Length - 1; j > i; j--) { if (span[j].Key.Equals(c.Key)) { supersededByLater = true; break; } }");
                sb.AppendLine("                if (supersededByLater) continue;");
                sb.AppendLine($"                if (c.Kind != ChangeKind.Delete && {rowKeyExpr}.Equals({keyExpr})) return Result<{row}>.Ok(c.Row);");
                sb.AppendLine("            }");
                sb.AppendLine($"            var overlayOffsetResult = {IndexFieldName(idx)}.GetOffset({keyExpr});");
                sb.AppendLine("            if (overlayOffsetResult.IsError()) return overlayOffsetResult.Void();");
                sb.AppendLine("            var overlayCandidate = storage.Get(overlayOffsetResult.Unwrap());");
                sb.AppendLine($"            var overlayCandidateKey = overlayCandidate.{table.PrimaryKeyName};");
                sb.AppendLine("            for (var i = 0; i < span.Length; i++) {");
                sb.AppendLine("                if (span[i].Key.Equals(overlayCandidateKey))");
                sb.AppendLine($"                    return Result<{row}>.Error(DbError.IndexKeyNotFound());");
                sb.AppendLine("            }");
                sb.AppendLine("            return overlayCandidate;");
                sb.AppendLine("        }");
                sb.AppendLine($"        var offsetResult = {IndexFieldName(idx)}.GetOffset({keyExpr});");
                sb.AppendLine("        if (offsetResult.IsError()) return offsetResult.Void();");
                sb.AppendLine("        return storage.Get(offsetResult.Unwrap());");
                sb.AppendLine("    }");
            } else {
                sb.AppendLine($"    public List<{row}> {idx.AccessorName}({parameters}) {{");
                sb.AppendLine("        if (Dirty) {");
                sb.AppendLine($"            var result = new List<{row}>();");
                sb.AppendLine("            var span = CollectionsMarshal.AsSpan(changes);");
                sb.AppendLine("            for (var i = span.Length - 1; i >= 0; i--) {");
                sb.AppendLine("                ref readonly var c = ref span[i];");
                sb.AppendLine("                var supersededByLater = false;");
                sb.AppendLine("                for (var j = span.Length - 1; j > i; j--) { if (span[j].Key.Equals(c.Key)) { supersededByLater = true; break; } }");
                sb.AppendLine("                if (supersededByLater) continue;");
                sb.AppendLine($"                if (c.Kind != ChangeKind.Delete && {rowKeyExpr}.Equals({keyExpr})) result.Add(c.Row);");
                sb.AppendLine("            }");
                sb.AppendLine("            offsetBuffer.Clear();");
                sb.AppendLine($"            {IndexFieldName(idx)}.GetOffsets({keyExpr}, offsetBuffer);");
                sb.AppendLine("            foreach (var offset in offsetBuffer) {");
                sb.AppendLine("                var candidate = storage.Get(offset);");
                sb.AppendLine($"                var candidateKey = candidate.{table.PrimaryKeyName};");
                sb.AppendLine("                var touchedByBatch = false;");
                sb.AppendLine("                for (var i = 0; i < span.Length; i++) { if (span[i].Key.Equals(candidateKey)) { touchedByBatch = true; break; } }");
                sb.AppendLine("                if (!touchedByBatch) result.Add(candidate);");
                sb.AppendLine("            }");
                sb.AppendLine("            return result;");
                sb.AppendLine("        }");
                sb.AppendLine("        offsetBuffer.Clear();");
                sb.AppendLine($"        {IndexFieldName(idx)}.GetOffsets({keyExpr}, offsetBuffer);");
                sb.AppendLine($"        var realResult = new List<{row}>(offsetBuffer.Count);");
                sb.AppendLine("        foreach (var offset in offsetBuffer) realResult.Add(storage.Get(offset));");
                sb.AppendLine("        return realResult;");
                sb.AppendLine("    }");
            }
        }
        if (table.Indexes.Length > 0) sb.AppendLine();
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
        sb.AppendLine($"    public void Update({key} id, {row} newRow) {{");
        sb.AppendLine("        changes.Add(new(ChangeKind.Update, id, newRow));");
        sb.AppendLine("        Dirty = true; ");
        sb.AppendLine("    }");
        sb.AppendLine($"    public void Delete({key} id) {{");
        sb.AppendLine($"        var current = {table.PrimaryKeyAccessor}(id);");
        sb.AppendLine("        if (!current.IsOk()) return;");
        sb.AppendLine("        changes.Add(new(ChangeKind.Delete, id, current.Unwrap()));");
        sb.AppendLine("        Dirty = true;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static private void EmitValidateMethod(StringBuilder sb, TableModel table) {
        var uniqueIndexes = table.Indexes.Where(i => i.Uniqueness == Uniqueness.Unique).ToImmutableArray();
        sb.AppendLine("    internal bool Validate() {");
        sb.AppendLine("        var span = CollectionsMarshal.AsSpan(changes);");
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
        sb.AppendLine("    internal void Discard() {");
        sb.AppendLine("        changes.Clear();");
        sb.AppendLine("        Dirty = false;");
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

    static private string EmitInstantOpsClass(TableModel table, string ownerSimpleName) {
        var sb = new StringBuilder();
        var row = table.RowTypeFullName;
        var key = table.PrimaryKeyTypeFullName;
        var opsName = $"{ownerSimpleName}{table.Accessor}Ops";
        var primaryIndexType = PrimaryIndexType(table);

        EmitOpsClassHeader(sb, table, isPersistent: false);

        sb.AppendLine($"public sealed class {opsName} {{");
        EmitStorageAndIndexFields(sb, table, primaryIndexType);
        EmitChangeTrackingFields(sb, key, row);

        sb.AppendLine($"    public {opsName}");
        sb.AppendLine("    (");
        sb.AppendLine($"        DenseArray<{row}> storage");
        sb.AppendLine($"        ,{primaryIndexType} primaryIndex");
        AppendAutoIncrementAndIndexParams(sb, table);
        sb.AppendLine("    ) {");
        sb.AppendLine("        this.storage = storage;");
        sb.AppendLine("        this.primaryIndex = primaryIndex;");
        AppendAutoIncrementAndIndexAssignments(sb, table);
        sb.AppendLine("    }");
        sb.AppendLine();

        EmitGetMethod(sb, table);
        EmitIterMethod(sb, table);
        EmitSecondaryIndexAccessors(sb, table);
        EmitStagingMethods(sb, table);
        EmitValidateMethod(sb, table);
        EmitDiscardMethod(sb);
        EmitInstantApply(sb, table);

        sb.AppendLine("}");
        return sb.ToString();
    }

    static private void EmitInstantApply(StringBuilder sb, TableModel table) {
        sb.AppendLine("    internal void Apply() {");
        sb.AppendLine("        foreach (ref readonly var c in CollectionsMarshal.AsSpan(changes)) {");
        sb.AppendLine("            switch (c.Kind) {");
        sb.AppendLine("                case ChangeKind.Insert: {");
        sb.AppendLine($"                    var pk = c.Row.{table.PrimaryKeyName};");
        sb.AppendLine("                    if (primaryIndex.GetOffset(pk).IsOk()) { lastError = DbError.DuplicateKey(); break; }");
        sb.AppendLine("                    var offset = storage.Insert(c.Row);");
        sb.AppendLine("                    primaryIndex.Insert(pk, offset);");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"                    {IndexFieldName(idx)}.Insert({KeyExpr("c.Row", idx)}, offset);");
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("                case ChangeKind.Update: {");
        sb.AppendLine("                    var offsetResult = primaryIndex.GetOffset(c.Key);");
        sb.AppendLine("                    if (offsetResult.IsError()) { lastError = offsetResult.GetError(); break; }");
        sb.AppendLine("                    var offset = offsetResult.Unwrap();");
        sb.AppendLine("                    var oldRow = storage.Get(offset);");
        sb.AppendLine("                    storage.Set(offset, c.Row);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("oldRow", idx) : $"{KeyExpr("oldRow", idx)}, offset";
            sb.AppendLine($"                    if (!{KeyExpr("oldRow", idx)}.Equals({KeyExpr("c.Row", idx)})) {{");
            sb.AppendLine($"                        {IndexFieldName(idx)}.Delete({deleteArgs});");
            sb.AppendLine($"                        {IndexFieldName(idx)}.Insert({KeyExpr("c.Row", idx)}, offset);");
            sb.AppendLine("                    }");
        }
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("                default: {");
        sb.AppendLine("                    var offsetResult = primaryIndex.GetOffset(c.Key);");
        sb.AppendLine("                    if (offsetResult.IsError()) { lastError = offsetResult.GetError(); break; }");
        sb.AppendLine("                    var offset = offsetResult.Unwrap();");
        sb.AppendLine("                    var lastOffset = storage.LastOffset;");
        sb.AppendLine("                    var swapped = storage.Delete(offset);");
        sb.AppendLine("                    primaryIndex.Delete(c.Key);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("c.Row", idx) : $"{KeyExpr("c.Row", idx)}, offset";
            sb.AppendLine($"                    {IndexFieldName(idx)}.Delete({deleteArgs});");
        }
        sb.AppendLine("                    if (swapped is { } swappedRow) {");
        sb.AppendLine($"                        var swappedPk = swappedRow.{table.PrimaryKeyName};");
        sb.AppendLine("                        primaryIndex.Delete(swappedPk);");
        sb.AppendLine("                        primaryIndex.Insert(swappedPk, offset);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("swappedRow", idx) : $"{KeyExpr("swappedRow", idx)}, lastOffset";
            sb.AppendLine($"                        {IndexFieldName(idx)}.Delete({deleteArgs});");
            sb.AppendLine($"                        {IndexFieldName(idx)}.Insert({KeyExpr("swappedRow", idx)}, offset);");
        }
        sb.AppendLine("                    }");
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        changes.Clear();");
        sb.AppendLine("        Dirty = false;");
        sb.AppendLine("    }");
    }

    static private string EmitPersistentOpsClass(TableModel table, string ownerSimpleName) {
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
        EmitChangeTrackingFields(sb, key, row);
        EmitEvictableField(sb, table);

        sb.AppendLine($"    public {opsName}");
        sb.AppendLine("    (");
        sb.AppendLine($"        DenseArray<{row}> storage");
        sb.AppendLine($"        ,{primaryIndexType} primaryIndex");
        sb.AppendLine($"        ,ColdTable<{key}, {row}> coldTable");
        sb.AppendLine($"        ,ColdStore cold");
        AppendAutoIncrementAndIndexParams(sb, table);
        sb.AppendLine("    ) {");
        sb.AppendLine("        this.storage = storage;");
        sb.AppendLine("        this.primaryIndex = primaryIndex;");
        sb.AppendLine("        this.coldTable = coldTable;");
        sb.AppendLine("        this.cold = cold;");
        AppendAutoIncrementAndIndexAssignments(sb, table);
        if (table.Evictable) sb.AppendLine("        cold.RegisterEvictionDrop(TableId, TryGetCurrentRowBytesForEviction, EvictDrop);");
        sb.AppendLine("    }");
        sb.AppendLine();

        EmitGetMethod(sb, table);
        EmitIterMethod(sb, table);
        EmitSecondaryIndexAccessors(sb, table);
        EmitStagingMethods(sb, table);
        EmitValidateMethod(sb, table);
        EmitDiscardMethod(sb);
        EmitPersistentApply(sb, table);
        EmitBulkLoadMethods(sb, table);
        if (table.Evictable) EmitStorageAccessor(sb, table, opsName);

        sb.AppendLine("}");
        return sb.ToString();
    }

    static private void EmitPersistentApply(StringBuilder sb, TableModel table) {
        sb.AppendLine("    internal void Apply() {");
        sb.AppendLine("        foreach (ref readonly var c in CollectionsMarshal.AsSpan(changes)) {");
        sb.AppendLine("            switch (c.Kind) {");
        sb.AppendLine("                case ChangeKind.Insert: {");
        sb.AppendLine("                    if (!cold.IsScopeActive) { lastError = DbError.NoActiveTransaction(); break; }");
        sb.AppendLine($"                    var pk = c.Row.{table.PrimaryKeyName};");
        sb.AppendLine("                    if (primaryIndex.GetOffset(pk).IsOk()) { lastError = DbError.DuplicateKey(); break; }");
        sb.AppendLine("                    var offset = storage.Insert(c.Row);");
        sb.AppendLine("                    primaryIndex.Insert(pk, offset);");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"                    {IndexFieldName(idx)}.Insert({KeyExpr("c.Row", idx)}, offset);");
        sb.AppendLine("                    cold.Stage(TableId, ChangeKind.Insert, MemoryPackSerializer.Serialize(pk), MemoryPackSerializer.Serialize(c.Row));");
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("                case ChangeKind.Update: {");
        sb.AppendLine("                    if (!cold.IsScopeActive) { lastError = DbError.NoActiveTransaction(); break; }");
        sb.AppendLine("                    var offsetResult = primaryIndex.GetOffset(c.Key);");
        sb.AppendLine("                    if (offsetResult.IsError()) { lastError = offsetResult.GetError(); break; }");
        sb.AppendLine("                    var offset = offsetResult.Unwrap();");
        sb.AppendLine("                    var oldRow = storage.Get(offset);");
        sb.AppendLine("                    storage.Set(offset, c.Row);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("oldRow", idx) : $"{KeyExpr("oldRow", idx)}, offset";
            sb.AppendLine($"                    if (!{KeyExpr("oldRow", idx)}.Equals({KeyExpr("c.Row", idx)})) {{");
            sb.AppendLine($"                        {IndexFieldName(idx)}.Delete({deleteArgs});");
            sb.AppendLine($"                        {IndexFieldName(idx)}.Insert({KeyExpr("c.Row", idx)}, offset);");
            sb.AppendLine("                    }");
        }
        sb.AppendLine("                    cold.Stage(TableId, ChangeKind.Update, MemoryPackSerializer.Serialize(c.Key), MemoryPackSerializer.Serialize(c.Row));");
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("                default: {");
        sb.AppendLine("                    if (!cold.IsScopeActive) { lastError = DbError.NoActiveTransaction(); break; }");
        sb.AppendLine("                    var offsetResult = primaryIndex.GetOffset(c.Key);");
        sb.AppendLine("                    if (offsetResult.IsError()) { lastError = offsetResult.GetError(); break; }");
        sb.AppendLine("                    var offset = offsetResult.Unwrap();");
        sb.AppendLine("                    var lastOffset = storage.LastOffset;");
        sb.AppendLine("                    var swapped = storage.Delete(offset);");
        sb.AppendLine("                    primaryIndex.Delete(c.Key);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("c.Row", idx) : $"{KeyExpr("c.Row", idx)}, offset";
            sb.AppendLine($"                    {IndexFieldName(idx)}.Delete({deleteArgs});");
        }
        sb.AppendLine("                    if (swapped is { } swappedRow) {");
        sb.AppendLine($"                        var swappedPk = swappedRow.{table.PrimaryKeyName};");
        sb.AppendLine("                        primaryIndex.Delete(swappedPk);");
        sb.AppendLine("                        primaryIndex.Insert(swappedPk, offset);");
        foreach (var idx in table.Indexes) {
            var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("swappedRow", idx) : $"{KeyExpr("swappedRow", idx)}, lastOffset";
            sb.AppendLine($"                        {IndexFieldName(idx)}.Delete({deleteArgs});");
            sb.AppendLine($"                        {IndexFieldName(idx)}.Insert({KeyExpr("swappedRow", idx)}, offset);");
        }
        sb.AppendLine("                    }");
        sb.AppendLine("                    cold.Stage(TableId, ChangeKind.Delete, MemoryPackSerializer.Serialize(c.Key), null);");
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        changes.Clear();");
        sb.AppendLine("        Dirty = false;");
        sb.AppendLine("    }");
    }

    static private void EmitBulkLoadMethods(StringBuilder sb, TableModel table) {
        var row = table.RowTypeFullName;
        sb.AppendLine();
        sb.AppendLine($"    internal void LoadRow({row} row) {{");
        sb.AppendLine($"        var pk = row.{table.PrimaryKeyName};");
        sb.AppendLine("        var offset = storage.Insert(row);");
        sb.AppendLine("        primaryIndex.Insert(pk, offset);");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"        {IndexFieldName(idx)}.Insert({KeyExpr("row", idx)}, offset);");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    internal void BulkLoadFromCold() {");
        sb.AppendLine("        foreach (var pair in cold.ScanAll(coldTable)) LoadRow(pair.Row);");
        sb.AppendLine("    }");
    }

    static private void EmitStorageAccessor(StringBuilder sb, TableModel table, string opsName) {
        var row = table.RowTypeFullName;
        var key = table.PrimaryKeyTypeFullName;
        sb.AppendLine();
        sb.AppendLine("    public StorageAccessor Storage => new(this);");
        sb.AppendLine($"    public readonly struct StorageAccessor({opsName} ops) {{");
        sb.AppendLine($"        public Result Load({key} id) => ops.LoadInternal(id);");
        sb.AppendLine($"        public Result Evict({key} id) => ops.Evict(id);");
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
        sb.AppendLine("        cold.StageEviction(TableId, MemoryPackSerializer.Serialize(id));");
        sb.AppendLine("        return Result.Ok();");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    internal byte[]? TryGetCurrentRowBytesForEviction(byte[] keyBytes) {");
        sb.AppendLine($"        var id = MemoryPackSerializer.Deserialize<{key}>(keyBytes)!;");
        sb.AppendLine("        var offsetResult = primaryIndex.GetOffset(id);");
        sb.AppendLine("        if (offsetResult.IsError()) return null;");
        sb.AppendLine("        var row = storage.Get(offsetResult.Unwrap());");
        sb.AppendLine("        return MemoryPackSerializer.Serialize(row);");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    internal void EvictDrop(byte[] keyBytes) {");
        sb.AppendLine($"        var id = MemoryPackSerializer.Deserialize<{key}>(keyBytes)!;");
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

    static private string EmitDatabase(DatabaseModel database, ImmutableArray<TableModel> tables) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using RhinoDB.Core;");
        sb.AppendLine("using RhinoDB.Lib.Cold;");
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

        sb.AppendLine($"public sealed class {txName} : ITransaction {{");
        foreach (var table in tables)
            sb.AppendLine($"    public readonly {database.SimpleName}{table.Accessor}Ops {table.Accessor};");
        sb.AppendLine();
        sb.Append($"    internal {txName}(");
        sb.Append(string.Join(", ", tables.Select(t => $"{database.SimpleName}{t.Accessor}Ops {Camel(t.Accessor)}")));
        sb.AppendLine(") {");
        foreach (var table in tables)
            sb.AppendLine($"        {table.Accessor} = {Camel(table.Accessor)};");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public Result Apply() {");
        foreach (var table in tables)
            sb.AppendLine($"        if ({table.Accessor}.Dirty && !{table.Accessor}.Validate()) return Result.Error({table.Accessor}.LastError);");
        foreach (var table in tables)
            sb.AppendLine($"        if ({table.Accessor}.Dirty) {table.Accessor}.Apply();");
        sb.AppendLine("        return Result.Ok();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public void Discard() {");
        foreach (var table in tables)
            sb.AppendLine($"        if ({table.Accessor}.Dirty) {table.Accessor}.Discard();");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        var instantTables = tables.Where(t => t.Kind == TableKind.Instant).ToImmutableArray();
        var persistentTables = tables.Where(t => t.Kind == TableKind.Persistent).ToImmutableArray();

        sb.AppendLine($"public partial class {database.SimpleName} {{");
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
            foreach (var table in persistentTables)
                sb.AppendLine($"        {Camel(table.Accessor)}ColdTable = cold.OpenTable<{table.PrimaryKeyTypeFullName}, {table.RowTypeFullName}>(\"{table.Accessor}\");");
        }
        foreach (var table in tables) {
            var args = new List<string> {
                $"{Camel(table.Accessor)}Storage",
                $"{Camel(table.Accessor)}PrimaryIndex",
            };
            if (table.Kind == TableKind.Persistent) {
                args.Add($"{Camel(table.Accessor)}ColdTable");
                args.Add("cold");
            }
            foreach (var aif in table.AutoIncrementFields) args.Add($"{Camel(table.Accessor)}{aif.FieldName}Counter");
            foreach (var idx in table.Indexes) args.Add($"{Camel(table.Accessor)}{idx.AccessorName}Index");
            sb.AppendLine($"        {Camel(table.Accessor)}Ops = new {database.SimpleName}{table.Accessor}Ops({string.Join(", ", args)});");
        }
        sb.Append($"        cachedTransaction = new {txName}(");
        sb.Append(string.Join(", ", tables.Select(t => $"{Camel(t.Accessor)}Ops")));
        sb.AppendLine(");");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    protected override {txName} CreateTransaction() => cachedTransaction;");

        if (persistentTables.Length > 0) sb.AppendLine($"    internal {txName} CreateLoaderTransaction() => CreateTransaction();");
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
        sb.AppendLine("}");
    }

    static private string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    private sealed class TableModel
    (
        string rowTypeFullName,
        string? rowNamespace,
        TableKind kind,
        string ownerDatabaseFullName,
        string primaryKeyName,
        string primaryKeyTypeFullName,
        IndexKind primaryKeyKind,
        string primaryKeyAccessor,
        string accessor,
        int chunkSize,
        bool evictable,
        ImmutableArray<AutoIncrementFieldModel> autoIncrementFields,
        ImmutableArray<IndexModel> indexes,
        ImmutableArray<string> validateMethodNames
    ) {
        public string RowTypeFullName { get; } = rowTypeFullName;
        public string? RowNamespace { get; } = rowNamespace;
        public TableKind Kind { get; } = kind;
        public string OwnerDatabaseFullName { get; } = ownerDatabaseFullName;
        public string PrimaryKeyName { get; } = primaryKeyName;
        public string PrimaryKeyTypeFullName { get; } = primaryKeyTypeFullName;
        public IndexKind PrimaryKeyKind { get; } = primaryKeyKind;
        public string PrimaryKeyAccessor { get; } = primaryKeyAccessor;
        public string Accessor { get; } = accessor;
        public int ChunkSize { get; } = chunkSize;
        public bool Evictable { get; } = evictable;
        public ImmutableArray<AutoIncrementFieldModel> AutoIncrementFields { get; } = autoIncrementFields;
        public ImmutableArray<IndexModel> Indexes { get; } = indexes;
        public ImmutableArray<string> ValidateMethodNames { get; } = validateMethodNames;
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

    private sealed class DatabaseModel(string fullName, string simpleName, string? @namespace) {
        public string FullName { get; } = fullName;
        public string SimpleName { get; } = simpleName;
        public string? Namespace { get; } = @namespace;
    }

    private enum TableKind { Instant, Persistent }
    private enum IndexKind { Hash, BTree, RedBlackTree  }
    private enum Uniqueness { Unique, NonUnique }
}
