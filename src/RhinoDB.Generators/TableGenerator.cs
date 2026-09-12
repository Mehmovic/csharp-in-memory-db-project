using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RhinoDB.Generators;

// Generated code for EVERY table - Instant or Persistent kind - owns its
// physical storage directly: a DenseArray<TRow> field plus a concrete
// primary-index field (HashIndex<TKey>/OrderedIndex<TKey>) on the {Db}
// class, driven directly by the generated Ops class, with no engine class
// in between. This used to be true only for Instant-kind tables -
// Persistent-kind ones drove a hand-written PersistentTable<TKey,TRow>
// composing ColdStore/ColdTable, because those cold-storage primitives were
// `internal` to RhinoDB.Lib and generated code (living in the consuming
// assembly) couldn't reach them. That barrier is gone: ColdStore now
// exposes a narrow public surface purpose-built for this - `OpenTable`,
// `Put`/`Get`/`Delete`/`Peek<TKey,TRow>(ColdTable<TKey,TRow>, ...)` - and
// `ColdTable<TKey,TRow>` itself is a public opaque handle type. Notably,
// `RhinoDB.Native.Transaction` and ColdStore's own txn-lifecycle machinery
// (`EnsureWriteTxn`/`BeginScope`/`EndScope`) stay entirely internal -
// generated code never manages a write transaction's lifecycle itself, only
// checks `ColdStore.IsScopeActive` (now public) before writing, exactly the
// guard PersistentTable used to enforce internally. `PersistentTable<TKey,TRow>`
// itself is gone from the codebase entirely - the same "hand-prove a shape,
// then delete the hand-written scaffold once codegen supersedes it"
// discipline already applied to `Table<TKey,TRow>`.
//
// Practical effect: a Persistent-kind Ops class's fields/constructor/Get/
// secondary-index accessors are now IDENTICAL in shape to an Instant-kind
// one (same `storage`/`primaryIndex` fields, same read logic - reads still
// never touch cold storage, decision 1) - `isPersistent` only changes two
// things: (1) two extra fields (`coldTable`, `cold`) and their constructor
// wiring, and (2) `Apply()`'s Insert/Update/Delete cases do one extra
// `cold.Put`/`cold.Delete` call after the same in-memory mutation Instant
// kind does, guarded by an explicit `cold.IsScopeActive` check at the top of
// the case - mirroring PersistentTable's original check-before-mutating
// order exactly (memory still isn't rolled back if the cold write itself
// fails afterward - a pre-existing, documented limitation, not new here).
// `.Storage.Load/Evict/Peek` (Persistent-kind only) read/write `storage`/
// `primaryIndex`/every secondary index field directly, the same way
// Apply()'s Delete case already does its own swap-remove handling.
//
// Every rule this generator relies on is a diagnostic, not an assumption or
// a crash: ToTableModel never throws on malformed input (a missing
// [PrimaryKey], an empty Accessor, a composite index that disagrees with
// itself) - it reports a real compile error and that one table is skipped
// (diagnostics non-empty => Model is null), so one broken table doesn't
// take down the whole compilation and the author sees an actual message,
// not a generator stack trace.
[Generator]
public sealed class TableGenerator : IIncrementalGenerator {
    private const string TableAttributeFullName = "RhinoDB.Core.Tables.TableAttribute";
    private const string DatabaseAttributeFullName = "RhinoDB.Core.Tables.DatabaseAttribute";
    private const string PrimaryKeyAttributeFullName = "RhinoDB.Core.Tables.PrimaryKeyAttribute";
    private const string AutoIncrementAttributeFullName = "RhinoDB.Core.Tables.AutoIncrementAttribute";
    private const string IndexAttributeFullName = "RhinoDB.Core.Tables.IndexAttribute";

    static private readonly DiagnosticDescriptor MissingPrimaryKeyDiagnostic = new(
        "RHINO001", "Table row missing [PrimaryKey]",
        "Row type '{0}' is [Table]-attributed but declares no [PrimaryKey] parameter",
        "RhinoDB.Generators", DiagnosticSeverity.Error, isEnabledByDefault: true);

    static private readonly DiagnosticDescriptor EmptyAccessorDiagnostic = new(
        "RHINO002", "Empty Accessor name",
        "{0} has an explicit Accessor that is an empty string - omit Accessor for the default name, or give it a real one",
        "RhinoDB.Generators", DiagnosticSeverity.Error, isEnabledByDefault: true);

    static private readonly DiagnosticDescriptor CompositeIndexKindMismatchDiagnostic = new(
        "RHINO003", "Composite index fields disagree on Kind/Uniqueness",
        "Fields sharing Accessor '{0}' on '{1}' must all declare the same IndexKind and Uniqueness",
        "RhinoDB.Generators", DiagnosticSeverity.Error, isEnabledByDefault: true);

    static private readonly DiagnosticDescriptor CompositeIndexTooManyFieldsDiagnostic = new(
        "RHINO004", "Composite index has too many fields",
        "Composite index '{0}' on '{1}' has {2} fields sharing one Accessor - at most 3 are supported",
        "RhinoDB.Generators", DiagnosticSeverity.Error, isEnabledByDefault: true);

    static private readonly DiagnosticDescriptor DuplicateOrderDiagnostic = new(
        "RHINO005", "Duplicate explicit Order in composite index",
        "Composite index '{0}' on '{1}' has two or more fields with the same explicit Order value",
        "RhinoDB.Generators", DiagnosticSeverity.Error, isEnabledByDefault: true);

    static private readonly DiagnosticDescriptor InvalidAutoIncrementTypeDiagnostic = new(
        "RHINO007", "AutoIncrement field must be an incrementable unmanaged integer type",
        "'{0}.{1}' is [AutoIncrement] but its type isn't one of sbyte/byte/short/ushort/int/uint/long/ulong - " +
        "AutoIncrement needs a type the system can generate a new value for",
        "RhinoDB.Generators", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var tableResults = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                TableAttributeFullName,
                // `record struct Foo(...)` parses as RecordDeclarationSyntax
                // (RecordStructDeclaration kind), not StructDeclarationSyntax -
                // a plain `struct Foo { }` is the only thing that IS the latter.
                predicate: static (node, _) => node is StructDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (ctx, _) => ToTableModel(ctx));

        // Registered on the per-table (uncollected) pipeline, separately
        // from the codegen output below - each table's own diagnostics are
        // reported exactly once regardless of how many [Database] classes
        // exist (Emit below runs once per database, over the same collected
        // table list, which would otherwise re-report every diagnostic once
        // per database).
        context.RegisterSourceOutput(tableResults, static (spc, result) => {
            foreach (var diagnostic in result.Diagnostics) spc.ReportDiagnostic(diagnostic);
        });

        var tables = tableResults.Collect();

        var databases = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                DatabaseAttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => ToDatabaseModel(ctx));

        var combined = databases.Combine(tables);
        context.RegisterSourceOutput(combined, static (spc, pair) => Emit(spc, pair.Left, pair.Right));
    }

    static private (bool Provided, string? Value) StringNamedArg(AttributeData attr, string name) {
        foreach (var kv in attr.NamedArguments)
            if (kv.Key == name) return (true, (string?)kv.Value.Value);
        return (false, null);
    }

    static private (bool Provided, int Value) IntNamedArg(AttributeData attr, string name) {
        foreach (var kv in attr.NamedArguments)
            if (kv.Key == name) return (true, (int)kv.Value.Value!);
        return (false, 0);
    }

    static private Location Loc(AttributeData attr) => attr.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? Location.None;

    static private bool? ClassifyAutoIncrementType(ITypeSymbol type) => type.SpecialType switch {
        SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64 => true,
        SpecialType.System_Byte or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64 => false,
        _ => null,
    };

    static private (TableModel? Model, ImmutableArray<Diagnostic> Diagnostics) ToTableModel(GeneratorAttributeSyntaxContext ctx) {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var rowType = (INamedTypeSymbol)ctx.TargetSymbol;
        var attribute = ctx.Attributes[0];

        var kind = (TableKind)(int)attribute.ConstructorArguments[0].Value!;
        var databaseType = (INamedTypeSymbol)attribute.ConstructorArguments[1].Value!;

        var (tableAccessorProvided, tableAccessorValue) = StringNamedArg(attribute, "Accessor");
        if (tableAccessorProvided && tableAccessorValue == "")
            diagnostics.Add(Diagnostic.Create(EmptyAccessorDiagnostic, Loc(attribute), $"[Table] on '{rowType.Name}'"));
        var tableAccessor = tableAccessorProvided && tableAccessorValue != "" ? tableAccessorValue! : $"{rowType.Name}s";

        var (chunkSizeProvided, chunkSizeValue) = IntNamedArg(attribute, "ChunkSize");
        var chunkSize = chunkSizeProvided ? chunkSizeValue : 4096;

        // The primary (positional) constructor - not the copy constructor a
        // record struct also has (single parameter of the row's own type).
        var primaryCtor = rowType.InstanceConstructors.FirstOrDefault(c =>
            c.Parameters.Length > 0 &&
            !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, rowType)));

        var primaryKeyParam = primaryCtor?.Parameters.FirstOrDefault(p =>
            p.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == PrimaryKeyAttributeFullName));
        if (primaryCtor is null || primaryKeyParam is null) {
            diagnostics.Add(Diagnostic.Create(MissingPrimaryKeyDiagnostic, ctx.TargetNode.GetLocation(), rowType.Name));
            return (null, diagnostics.ToImmutable());
        }

        var primaryKeyAttribute = primaryKeyParam.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == PrimaryKeyAttributeFullName);
        var primaryKeyKind = primaryKeyAttribute.ConstructorArguments.Length > 0
            ? (IndexKind)(int)primaryKeyAttribute.ConstructorArguments[0].Value!
            : IndexKind.Hash;
        var (pkAccessorProvided, pkAccessorValue) = StringNamedArg(primaryKeyAttribute, "Accessor");
        if (pkAccessorProvided && pkAccessorValue == "")
            diagnostics.Add(Diagnostic.Create(EmptyAccessorDiagnostic, Loc(primaryKeyAttribute), $"[PrimaryKey] on '{rowType.Name}.{primaryKeyParam.Name}'"));
        var primaryKeyAccessor = pkAccessorProvided && pkAccessorValue != "" ? pkAccessorValue! : "Get";

        var autoIncrementFields = ImmutableArray.CreateBuilder<AutoIncrementFieldModel>();
        foreach (var p in primaryCtor.Parameters) {
            var autoIncrementAttribute = p.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AutoIncrementAttributeFullName);
            if (autoIncrementAttribute is null) continue;

            var signed = ClassifyAutoIncrementType(p.Type);
            if (signed is null) {
                diagnostics.Add(Diagnostic.Create(InvalidAutoIncrementTypeDiagnostic, Loc(autoIncrementAttribute), rowType.Name, p.Name));
                continue;
            }

            autoIncrementFields.Add(new AutoIncrementFieldModel(p.Name, p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), signed.Value));
        }

        // One entry per [Index]-attributed parameter, carrying its
        // declaration position (the "order fields sort by, by default"
        // Order falls back to) before any grouping happens.
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
                    diagnostics.Add(Diagnostic.Create(EmptyAccessorDiagnostic, Loc(indexAttribute), $"[Index] on '{rowType.Name}.{x.Param.Name}'"));
                var accessor = accessorProvided && accessorValue != "" ? accessorValue! : x.Param.Name;
                var order = indexAttribute.NamedArguments
                    .Where(kv => kv.Key == "Order")
                    .Select(kv => (int)kv.Value.Value!)
                    .DefaultIfEmpty(-1)
                    .First();
                return (x.Param, x.DeclIndex, Kind: indexKind, Uniqueness: uniqueness, Accessor: accessor, Order: order, Attr: indexAttribute);
            })
            .ToImmutableArray();

        // Grouped by Accessor: 2-3 fields sharing one Accessor form a single
        // composite index over them, ordered by Order (falling back to
        // declaration position) - a lone field is just a 1-field "composite".
        // GroupBy preserves first-occurrence order, so index declaration
        // order in generated output tracks row field declaration order.
        var indexes = indexedParams
            .GroupBy(x => x.Accessor)
            .Select(g => {
                var group = g.ToImmutableArray();

                if (group.Length > 3) {
                    diagnostics.Add(Diagnostic.Create(CompositeIndexTooManyFieldsDiagnostic, Loc(group[0].Attr), g.Key, rowType.Name, group.Length));
                    return null;
                }

                if (group.Any(x => x.Kind != group[0].Kind || x.Uniqueness != group[0].Uniqueness)) {
                    diagnostics.Add(Diagnostic.Create(CompositeIndexKindMismatchDiagnostic, Loc(group[0].Attr), g.Key, rowType.Name));
                    return null;
                }

                var explicitOrders = group.Where(x => x.Order != -1).Select(x => x.Order).ToImmutableArray();
                if (explicitOrders.Length != explicitOrders.Distinct().Count()) {
                    diagnostics.Add(Diagnostic.Create(DuplicateOrderDiagnostic, Loc(group[0].Attr), g.Key, rowType.Name));
                    return null;
                }

                var ordered = group.OrderBy(x => x.Order == -1 ? x.DeclIndex : x.Order).ToImmutableArray();
                var fields = ordered
                    .Select(x => new IndexFieldModel(x.Param.Name, x.Param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
                    .ToImmutableArray();
                return new IndexModel(g.Key, ordered[0].Kind, ordered[0].Uniqueness, fields);
            })
            .Where(m => m is not null)
            .Select(m => m!)
            .ToImmutableArray();

        if (diagnostics.Count > 0) return (null, diagnostics.ToImmutable());

        var model = new TableModel(
            rowType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            rowType.Name,
            rowType.ContainingNamespace.IsGlobalNamespace ? null : rowType.ContainingNamespace.ToDisplayString(),
            kind,
            databaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            primaryKeyParam.Name,
            primaryKeyParam.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            primaryKeyKind,
            primaryKeyAccessor,
            tableAccessor,
            chunkSize,
            autoIncrementFields.ToImmutable(),
            indexes);
        return (model, ImmutableArray<Diagnostic>.Empty);
    }

    static private DatabaseModel ToDatabaseModel(GeneratorAttributeSyntaxContext ctx) {
        var databaseType = (INamedTypeSymbol)ctx.TargetSymbol;
        return new DatabaseModel(
            databaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            databaseType.Name,
            databaseType.ContainingNamespace.IsGlobalNamespace ? null : databaseType.ContainingNamespace.ToDisplayString());
    }

    static private void Emit(SourceProductionContext context, DatabaseModel database, ImmutableArray<(TableModel? Model, ImmutableArray<Diagnostic> Diagnostics)> allResults) {
        var tables = allResults
            .Select(r => r.Model)
            .Where(m => m is not null)
            .Select(m => m!)
            .Where(t => t.OwnerDatabaseFullName == database.FullName)
            .ToImmutableArray();

        foreach (var table in tables)
            context.AddSource($"{table.RowTypeName}Ops.g.cs", EmitOpsClass(table));

        context.AddSource($"{database.SimpleName}.g.cs", EmitDatabase(database, tables));
    }

    // The concrete primary-index class for a table - HashIndex/OrderedIndex
    // are always Unique (a primary key can't be anything else).
    static private string PrimaryIndexType(TableModel table) =>
        table.PrimaryKeyKind == IndexKind.RedBlackOrdered
            ? $"OrderedIndex<{table.PrimaryKeyTypeFullName}>"
            : $"HashIndex<{table.PrimaryKeyTypeFullName}>";

    // The concrete index class for a given (Kind, Uniqueness) pair - the one
    // place this mapping is decided, used for both the {Db} class's field
    // declarations and the Ops class's constructor parameter types.
    static private string ConcreteIndexType(IndexModel idx) {
        var keyType = KeyType(idx);
        return (idx.Kind, idx.Uniqueness) switch {
            (IndexKind.Hash, Uniqueness.Unique) => $"HashIndex<{keyType}>",
            (IndexKind.RedBlackOrdered, Uniqueness.Unique) => $"OrderedIndex<{keyType}>",
            (IndexKind.Hash, Uniqueness.NonUnique) => $"NonUniqueHashSetIndex<{keyType}>",
            _ => $"NonUniqueOrderedIndex<{keyType}>",
        };
    }

    // A lone field's own type, or a named ValueTuple type for a composite
    // index - named so the tuple stays self-documenting even though
    // generated code mostly builds/consumes it positionally.
    static private string KeyType(IndexModel idx) =>
        idx.Fields.Length == 1
            ? idx.Fields[0].FieldTypeFullName
            : $"({string.Join(", ", idx.Fields.Select(f => $"{f.FieldTypeFullName} {f.FieldName}"))})";

    // The key expression to read off a row variable for this index - a
    // single member access, or a positional tuple literal for a composite.
    static private string KeyExpr(string rowVar, IndexModel idx) =>
        idx.Fields.Length == 1
            ? $"{rowVar}.{idx.Fields[0].FieldName}"
            : $"({string.Join(", ", idx.Fields.Select(f => $"{rowVar}.{f.FieldName}"))})";

    static private string IndexFieldName(IndexModel idx) => $"{Camel(idx.AccessorName)}Index";

    static private string EmitOpsClass(TableModel table) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("using RhinoDB.Core;");
        sb.AppendLine("using RhinoDB.Lib.Cold;");
        sb.AppendLine("using RhinoDB.Lib.Indexing;");
        sb.AppendLine("using RhinoDB.Lib.Storage;");
        sb.AppendLine("using RhinoDB.Lib.Tables;");
        sb.AppendLine();
        if (table.RowNamespace is not null) {
            sb.AppendLine($"namespace {table.RowNamespace};");
            sb.AppendLine();
        }

        var row = table.RowTypeFullName;
        var key = table.PrimaryKeyTypeFullName;
        var opsName = $"{table.RowTypeName}Ops";
        var autoIncrementFields = table.AutoIncrementFields;
        var uniqueIndexes = table.Indexes.Where(i => i.Uniqueness == Uniqueness.Unique).ToImmutableArray();
        var pk = table.PrimaryKeyAccessor;
        var isPersistent = table.Kind == TableKind.Persistent;
        var primaryIndexType = PrimaryIndexType(table);

        // storage/primaryIndex are unconditional now - Persistent kind
        // inlines the exact same DenseArray/index fields Instant kind
        // always has, plus a ColdTable/ColdStore pair for the cold write-
        // through Apply() does after the same in-memory mutation.
        sb.AppendLine($"public sealed class {opsName} {{");
        sb.AppendLine($"    private readonly DenseArray<{row}> storage;");
        sb.AppendLine($"    private readonly {primaryIndexType} primaryIndex;");
        if (isPersistent) {
            sb.AppendLine($"    private readonly ColdTable<{key}, {row}> coldTable;");
            sb.AppendLine("    private readonly ColdStore cold;");
        }
        foreach (var aif in autoIncrementFields)
            sb.AppendLine($"    private readonly AutoIncrementCounter {Camel(aif.FieldName)}Counter;");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"    private readonly {ConcreteIndexType(idx)} {IndexFieldName(idx)};");
        sb.AppendLine($"    private readonly List<Change<{key}, {row}>> changes = [];");
        sb.AppendLine("    public bool Dirty { get; private set; }");
        sb.AppendLine("    private DbError lastError;");
        sb.AppendLine("    internal DbError LastError => lastError;");
        sb.AppendLine();

        // Constructor: storage, primaryIndex, then [coldTable, cold] only
        // for Persistent kind, then one AutoIncrementCounter per
        // [AutoIncrement] field, then one concrete-typed parameter per index -
        // position-matched 1:1 against EmitDatabase's call site, both
        // generated from the same TableModel.AutoIncrementFields/Indexes order.
        sb.Append($"    public {opsName}(DenseArray<{row}> storage, {primaryIndexType} primaryIndex");
        if (isPersistent) sb.Append($", ColdTable<{key}, {row}> coldTable, ColdStore cold");
        foreach (var aif in autoIncrementFields) sb.Append($", AutoIncrementCounter {Camel(aif.FieldName)}Counter");
        foreach (var idx in table.Indexes) sb.Append($", {ConcreteIndexType(idx)} {IndexFieldName(idx)}");
        sb.AppendLine(") {");
        sb.AppendLine("        this.storage = storage;");
        sb.AppendLine("        this.primaryIndex = primaryIndex;");
        if (isPersistent) {
            sb.AppendLine("        this.coldTable = coldTable;");
            sb.AppendLine("        this.cold = cold;");
        }
        foreach (var aif in autoIncrementFields) sb.AppendLine($"        this.{Camel(aif.FieldName)}Counter = {Camel(aif.FieldName)}Counter;");
        foreach (var idx in table.Indexes) sb.AppendLine($"        this.{IndexFieldName(idx)} = {IndexFieldName(idx)};");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Read-your-own-writes: most-recent-first scan over this operation's
        // own staged changes before falling through to real storage - the
        // same algorithm ChangeSetTests.cs already proved, inlined per table.
        // Span + `ref readonly` avoids a struct copy per candidate examined
        // (a Change carries a full TRow) - see
        // Docs/01-performance-principles.md §1/§2 and
        // Docs/02-architecture.md § Transactions. Named "Get" by default;
        // [PrimaryKey(Accessor = "...")] renames it. Never touches cold
        // storage even for Persistent kind - Get/GetByOffset are memory-only
        // by design (see Cold storage decision 1).
        sb.AppendLine($"    public Result<{row}> {pk}({key} id) {{");
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

        // Sequential enumeration of every row currently in memory, in
        // DenseArray offset order - real storage only, deliberately NOT
        // overlay-aware (unlike Get, a full scan merging in this
        // operation's own staged-but-unapplied changes would need to skip
        // deleted rows and dedupe updated ones; not built until a real need
        // shows up, matching this project's "declared explicitly, don't
        // build ahead of a proven need" posture elsewhere). Lazy (`yield
        // return`) since a full-table walk is already the explicitly-slow
        // escape hatch, not a hot-path accessor.
        sb.AppendLine($"    public IEnumerable<{row}> Iter() {{");
        sb.AppendLine("        for (var i = 0; i < storage.Count; i++) yield return storage.Get(i);");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Secondary-index read accessors - overlay-aware (Milestone 4): when
        // Dirty, this operation's own staged-but-not-yet-applied changes are
        // consulted before falling back to the real index/storage, the same
        // read-your-own-writes guarantee the primary Get() accessor already
        // has. The Dirty check keeps the common case (no staged writes on
        // this table yet) exactly as cheap as before this milestone - the
        // overlay scan only runs when there's actually something to overlay.
        //
        // Algorithm (backward scan, mirroring Validate()'s duplicate-key
        // check): for each staged change, walk forward from it to see if a
        // LATER entry in the batch already claims the same primary key - if
        // so, this entry is stale within the batch itself and is skipped;
        // otherwise it's that key's final staged state for this batch. A
        // final state whose row's index-field value matches the target is
        // an overlay hit, returned immediately (it doesn't matter what real
        // storage/the real index currently say, this operation's own write
        // wins). If nothing in the batch matches, fall back to the real
        // index/storage - but the real hit might now be stale (its primary
        // key was touched by this batch and its field no longer matches, or
        // it was deleted), so a real hit is only trusted if its primary key
        // was never touched by the batch at all.
        //
        // Works identically for Persistent kind - `storage` is a direct
        // field either way now.
        foreach (var idx in table.Indexes) {
            var parameters = string.Join(", ", idx.Fields.Select(f => $"{f.FieldTypeFullName} {Camel(f.FieldName)}"));
            var keyExpr = idx.Fields.Length == 1
                ? Camel(idx.Fields[0].FieldName)
                : $"({string.Join(", ", idx.Fields.Select(f => Camel(f.FieldName)))})";
            var rowKeyExpr = KeyExpr("c.Row", idx);
            string ReadByOffset(string offsetExpr) => $"storage.Get({offsetExpr})";
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
                sb.AppendLine($"            var overlayCandidate = {ReadByOffset("overlayOffsetResult.Unwrap()")};");
                sb.AppendLine($"            var overlayCandidateKey = overlayCandidate.{table.PrimaryKeyName};");
                sb.AppendLine("            for (var i = 0; i < span.Length; i++) {");
                sb.AppendLine("                if (span[i].Key.Equals(overlayCandidateKey))");
                sb.AppendLine($"                    return Result<{row}>.Error(DbError.IndexKeyNotFound());");
                sb.AppendLine("            }");
                sb.AppendLine("            return overlayCandidate;");
                sb.AppendLine("        }");
                sb.AppendLine($"        var offsetResult = {IndexFieldName(idx)}.GetOffset({keyExpr});");
                sb.AppendLine("        if (offsetResult.IsError()) return offsetResult.Void();");
                sb.AppendLine($"        return {ReadByOffset("offsetResult.Unwrap()")};");
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
                sb.AppendLine($"            var overlayOffsets = {IndexFieldName(idx)}.GetOffsets({keyExpr});");
                sb.AppendLine("            foreach (var offset in overlayOffsets) {");
                sb.AppendLine($"                var candidate = {ReadByOffset("offset")};");
                sb.AppendLine($"                var candidateKey = candidate.{table.PrimaryKeyName};");
                sb.AppendLine("                var touchedByBatch = false;");
                sb.AppendLine("                for (var i = 0; i < span.Length; i++) { if (span[i].Key.Equals(candidateKey)) { touchedByBatch = true; break; } }");
                sb.AppendLine("                if (!touchedByBatch) result.Add(candidate);");
                sb.AppendLine("            }");
                sb.AppendLine("            return result;");
                sb.AppendLine("        }");
                sb.AppendLine($"        var offsets = {IndexFieldName(idx)}.GetOffsets({keyExpr});");
                sb.AppendLine($"        var realResult = new List<{row}>(offsets.Count);");
                sb.AppendLine($"        foreach (var offset in offsets) realResult.Add({ReadByOffset("offset")});");
                sb.AppendLine("        return realResult;");
                sb.AppendLine("    }");
            }
        }
        if (table.Indexes.Length > 0) sb.AppendLine();

        sb.AppendLine($"    public void Insert({row} row) {{");
        foreach (var aif in autoIncrementFields) {
            var comparison = aif.IsSigned ? "<= 0" : "== 0";
            sb.AppendLine($"        if (row.{aif.FieldName} {comparison})");
            sb.AppendLine($"            row = row with {{ {aif.FieldName} = ({aif.FieldTypeFullName}){Camel(aif.FieldName)}Counter.Next() }};");
        }
        sb.AppendLine($"        changes.Add(new(ChangeKind.Insert, row.{table.PrimaryKeyName}, row));");
        sb.AppendLine("        Dirty = true;");
        sb.AppendLine("    }");
        sb.AppendLine($"    public void Update({key} id, {row} newRow) {{ changes.Add(new(ChangeKind.Update, id, newRow)); Dirty = true; }}");
        sb.AppendLine($"    public void Delete({key} id) {{");
        sb.AppendLine($"        var current = {pk}(id);");
        sb.AppendLine("        if (!current.IsOk()) return;");
        sb.AppendLine("        changes.Add(new(ChangeKind.Delete, id, current.Unwrap()));");
        sb.AppendLine("        Dirty = true;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Real pre-apply validation: nothing mutates during this pass, so a
        // later table's failure (see the generated Transaction.Apply() below)
        // never leaves this table's already-staged changes applied - the
        // free cross-table atomicity Milestone 2 exists to prove. Checked
        // per staged change, in order, directly against each concrete
        // index's own GetOffset:
        //  - Insert: does the key already exist "as of just before this
        //    entry"? Scans backward through EARLIER entries in this same
        //    batch first (an intervening Delete clears it, an intervening
        //    Insert/Update means it's still live) - falls back to real
        //    committed storage only if no prior entry in this batch mentions
        //    the key at all. Without the batch-local scan, a same-batch
        //    "Delete then re-Insert the same key" would be wrongly flagged
        //    as a duplicate (the row is still physically committed until
        //    Apply() actually runs); without ALSO checking, a same-batch
        //    double-Insert of the same key would wrongly pass (each half
        //    individually looks fresh against committed storage) and
        //    silently corrupt at Apply() time instead of failing loudly here.
        //    "Committed storage" here means memory only (never cold),
        //    matching decision 1 - true for both kinds, since primaryIndex
        //    is a direct field either way now.
        //  - Update: primary-key immutability, then existence (the same
        //    batch-local-then-committed scan Insert's duplicate check uses,
        //    inverted - an Update whose key was never inserted or committed
        //    anywhere reports IndexKeyNotFound here instead of silently
        //    no-op'ing at Apply() time. Found via a real gap: Apply()'s own
        //    per-case `break` on a failed offset lookup never propagates to
        //    the caller - Validate() passing is what makes Apply() a "can't
        //    fail" replay, so an unchecked Update-of-nonexistent-key used to
        //    slip through Validate() and then silently do nothing at Apply()
        //    time, reporting Result.Ok() for an update that never happened),
        //    then unique-index conflicts using the row's real physical
        //    offset *if it already has one* (skipped entirely whenever a
        //    table simply has no unique secondary index at all). A key with
        //    no offset yet (inserted earlier in this same batch, not yet
        //    applied - already known to exist via the batch-local scan
        //    above) is left to Apply()'s own in-order replay to resolve the
        //    unique-index check correctly, same as before this validation
        //    existed - Apply() replays in staged order, so by the time this
        //    Update replays, the earlier Insert has already physically landed.
        //  - Delete: nothing to check - existence was already resolved
        //    against the overlay at stage time (see Delete above).
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
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        return true;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Apply: unconditionally inlines the same swap-remove handling
        // Table<TKey,TRow> used to (DenseArray.Delete can relocate the
        // physically-last row into the freed slot, and every index -
        // primary and secondary - must repoint to follow it) for BOTH
        // kinds now, since storage/primaryIndex are direct fields either
        // way. Persistent kind adds exactly one extra step per case: a
        // `cold.Put`/`cold.Delete` call after the same in-memory mutation,
        // guarded by an explicit `cold.IsScopeActive` check at the top of
        // the case (mirroring PersistentTable's original check-before-
        // mutating order - a cold-write failure does NOT roll back the
        // already-applied in-memory mutation, a pre-existing, documented
        // limitation carried over unchanged, not introduced here). Update
        // only touches an index whose own field(s) actually changed -
        // compares oldRow's key expression against the new row's via
        // .Equals() (a single field access, or structural ValueTuple
        // equality for a composite index) and skips the Delete+Insert pair
        // entirely when they're equal, since the index's existing mapping
        // already points at the right offset. Unlike an early, buggy
        // attempt at this same optimization noted in Docs/03-roadmap.md
        // Stage 3 (which apparently skipped re-registration under the wrong
        // condition and left a stale entry behind - a real self-collision
        // risk: reverting a field back to a value it held earlier could
        // then collide with its own uncleaned stale entry), this always
        // deletes the OLD mapping whenever the value differs, before
        // inserting the new one - never conditionally skips the delete
        // alone.
        sb.AppendLine("    internal void Apply() {");
        sb.AppendLine("        foreach (ref readonly var c in CollectionsMarshal.AsSpan(changes)) {");
        sb.AppendLine("            switch (c.Kind) {");
        sb.AppendLine("                case ChangeKind.Insert: {");
        if (isPersistent) sb.AppendLine("                    if (!cold.IsScopeActive) { lastError = DbError.NoActiveTransaction(); break; }");
        sb.AppendLine($"                    var pk = c.Row.{table.PrimaryKeyName};");
        sb.AppendLine("                    if (primaryIndex.GetOffset(pk).IsOk()) { lastError = DbError.DuplicateKey(); break; }");
        sb.AppendLine("                    var offset = storage.Insert(c.Row);");
        sb.AppendLine("                    primaryIndex.Insert(pk, offset);");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"                    {IndexFieldName(idx)}.Insert({KeyExpr("c.Row", idx)}, offset);");
        if (isPersistent) {
            sb.AppendLine("                    var coldPutResult = cold.Put(coldTable, pk, c.Row);");
            sb.AppendLine("                    if (coldPutResult.IsError()) { lastError = coldPutResult.GetError(); break; }");
        }
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("                case ChangeKind.Update: {");
        if (isPersistent) sb.AppendLine("                    if (!cold.IsScopeActive) { lastError = DbError.NoActiveTransaction(); break; }");
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
        if (isPersistent) {
            sb.AppendLine("                    var coldPutResult = cold.Put(coldTable, c.Key, c.Row);");
            sb.AppendLine("                    if (coldPutResult.IsError()) { lastError = coldPutResult.GetError(); break; }");
        }
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("                default: {");
        if (isPersistent) sb.AppendLine("                    if (!cold.IsScopeActive) { lastError = DbError.NoActiveTransaction(); break; }");
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
        if (isPersistent) {
            sb.AppendLine("                    var coldDeleteResult = cold.Delete(coldTable, c.Key);");
            sb.AppendLine("                    if (coldDeleteResult.IsError()) { lastError = coldDeleteResult.GetError(); break; }");
        }
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        changes.Clear();");
        sb.AppendLine("        Dirty = false;");
        sb.AppendLine("    }");

        // .Storage groups the Persistent-kind-only Load/Evict/Peek accessors -
        // these read/write `storage`/`primaryIndex`/every secondary index
        // field directly (no engine class in between anymore), never the
        // change log, so they need no staging/validation of their own.
        // Peek is a pure pass-through (it never mutates memory - see the
        // Peek-bypasses-Run design note in Docs/02-architecture.md § Cold
        // storage) - Load/Evict route through private Ops methods since
        // they mutate storage AND every secondary index field.
        if (isPersistent) {
            sb.AppendLine();
            sb.AppendLine("    public StorageAccessor Storage => new(this);");
            sb.AppendLine($"    public readonly struct StorageAccessor({opsName} ops) {{");
            sb.AppendLine($"        public Result Load({key} id) => ops.LoadInternal(id);");
            sb.AppendLine($"        public Result Evict({key} id) => ops.EvictInternal(id);");
            sb.AppendLine($"        public Result<{row}> Peek({key} id) => ops.cold.Peek(ops.coldTable, id);");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Idempotent: a row already in memory needs no cold round trip
            // at all - checked first so this never touches cold storage
            // (or requires an active scope) for a no-op call, and so a
            // repeat Load never double-inserts into a secondary index.
            sb.AppendLine($"    private Result LoadInternal({key} id) {{");
            sb.AppendLine("        if (primaryIndex.GetOffset(id).IsOk()) return Result.Ok();");
            sb.AppendLine("        if (!cold.IsScopeActive) return Result.Error(DbError.NoActiveTransaction());");
            sb.AppendLine("        var coldResult = cold.Get(coldTable, id);");
            sb.AppendLine("        if (coldResult.IsError()) return coldResult.Void();");
            sb.AppendLine("        var row = coldResult.Unwrap();");
            sb.AppendLine("        var offset = storage.Insert(row);");
            sb.AppendLine("        primaryIndex.Insert(id, offset);");
            foreach (var idx in table.Indexes)
                sb.AppendLine($"        {IndexFieldName(idx)}.Insert({KeyExpr("row", idx)}, offset);");
            sb.AppendLine("        return Result.Ok();");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Removes the row's own secondary-index entries and, if
            // DenseArray.Delete relocated another row to fill the freed
            // slot, repoints that row's entries too - the same swap-remove
            // handling Apply()'s Delete case does, just without any cold-
            // storage interaction (Evict never touches cold storage - the
            // durable copy stays, see Cold storage decision 2). Idempotent:
            // a row not currently in memory is treated as already evicted.
            sb.AppendLine($"    private Result EvictInternal({key} id) {{");
            sb.AppendLine("        var offsetResult = primaryIndex.GetOffset(id);");
            sb.AppendLine("        if (offsetResult.IsError())");
            sb.AppendLine("            return offsetResult.GetError().Kind == ErrorKind.IndexKeyNotFound ? Result.Ok() : offsetResult.Void();");
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
            sb.AppendLine("        return Result.Ok();");
            sb.AppendLine("    }");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    static private string EmitDatabase(DatabaseModel database, ImmutableArray<TableModel> tables) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
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
            sb.AppendLine($"    public readonly {table.RowTypeName}Ops {table.Accessor};");
        sb.AppendLine();
        sb.Append($"    internal {txName}(");
        sb.Append(string.Join(", ", tables.Select(t => $"{t.RowTypeName}Ops {Camel(t.Accessor)}")));
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
        sb.AppendLine("}");
        sb.AppendLine();

        var instantTables = tables.Where(t => t.Kind == TableKind.Instant).ToImmutableArray();
        var persistentTables = tables.Where(t => t.Kind == TableKind.Persistent).ToImmutableArray();

        sb.AppendLine($"public partial class {database.SimpleName} {{");
        // Instant-kind fields are self-contained `new ConcreteType()`
        // initializers with no reference to any sibling field.
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
        // Persistent-kind fields are the SAME DenseArray/primary-index
        // shape Instant-kind has, plus a ColdTable field. Unlike
        // storage/primaryIndex, ColdTable can't be a field initializer's
        // `new(...)` - opening it needs a real ColdStore, only available
        // once the base DbContext<TTx>(ColdStore) constructor has already
        // run (a field initializer can't reference even an inherited
        // instance property - the same CS0236 category hit during
        // Milestone 2's interface retirement) - so it's assigned in the
        // constructor body below instead, using the constructor's own
        // `cold` parameter directly (never `this.Cold`, sidestepping the
        // question of exactly when that inherited property becomes safe to
        // read). The shared `cold` field itself (one per database, not one
        // per table) is what every Persistent-kind Ops instance uses for
        // its own IsScopeActive check / Put/Get/Delete/Peek calls.
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
        sb.AppendLine();

        if (persistentTables.Length > 0) {
            sb.AppendLine($"    public {database.SimpleName}(ColdStore cold) : base(cold) {{");
            sb.AppendLine("        this.cold = cold;");
            foreach (var table in persistentTables)
                sb.AppendLine($"        {Camel(table.Accessor)}ColdTable = cold.OpenTable<{table.PrimaryKeyTypeFullName}, {table.RowTypeFullName}>(\"{table.Accessor}\");");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.Append($"    protected override {txName} CreateTransaction() => new {txName}(");
        sb.Append(string.Join(", ", tables.Select(t => {
            var args = new System.Collections.Generic.List<string> {
                $"{Camel(t.Accessor)}Storage",
                $"{Camel(t.Accessor)}PrimaryIndex",
            };
            if (t.Kind == TableKind.Persistent) {
                args.Add($"{Camel(t.Accessor)}ColdTable");
                args.Add("cold");
            }
            foreach (var aif in t.AutoIncrementFields) args.Add($"{Camel(t.Accessor)}{aif.FieldName}Counter");
            foreach (var idx in t.Indexes) args.Add($"{Camel(t.Accessor)}{idx.AccessorName}Index");
            return $"new {t.RowTypeName}Ops({string.Join(", ", args)})";
        })));
        sb.AppendLine(");");
        sb.AppendLine("}");

        return sb.ToString();
    }

    static private string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    private sealed class TableModel(
        string rowTypeFullName, string rowTypeName, string? rowNamespace,
        TableKind kind, string ownerDatabaseFullName,
        string primaryKeyName, string primaryKeyTypeFullName, IndexKind primaryKeyKind,
        string primaryKeyAccessor, string accessor, int chunkSize,
        ImmutableArray<AutoIncrementFieldModel> autoIncrementFields,
        ImmutableArray<IndexModel> indexes) {
        public string RowTypeFullName { get; } = rowTypeFullName;
        public string RowTypeName { get; } = rowTypeName;
        public string? RowNamespace { get; } = rowNamespace;
        public TableKind Kind { get; } = kind;
        public string OwnerDatabaseFullName { get; } = ownerDatabaseFullName;
        public string PrimaryKeyName { get; } = primaryKeyName;
        public string PrimaryKeyTypeFullName { get; } = primaryKeyTypeFullName;
        public IndexKind PrimaryKeyKind { get; } = primaryKeyKind;

        // The generated primary-key read method's name (default "Get") and
        // the generated property exposing this table's Ops on the
        // database's Transaction (default "{RowTypeName}s") - see
        // PrimaryKeyAttribute.Accessor / TableAttribute.Accessor.
        public string PrimaryKeyAccessor { get; } = primaryKeyAccessor;
        public string Accessor { get; } = accessor;

        // The generated DenseArray<TRow> storage field's chunk size - see
        // TableAttribute.ChunkSize (default 4096).
        public int ChunkSize { get; } = chunkSize;

        public ImmutableArray<AutoIncrementFieldModel> AutoIncrementFields { get; } = autoIncrementFields;
        public ImmutableArray<IndexModel> Indexes { get; } = indexes;
    }

    private sealed class AutoIncrementFieldModel(string fieldName, string fieldTypeFullName, bool isSigned) {
        public string FieldName { get; } = fieldName;
        public string FieldTypeFullName { get; } = fieldTypeFullName;
        public bool IsSigned { get; } = isSigned;
    }

    // AccessorName is the generated method/field name for this index - the
    // shared Accessor value for a composite index, or a lone field's own
    // name (see IndexAttribute.Accessor). Fields is 1 entry for a plain
    // index, 2-3 for a composite one, already sorted by Order/declaration
    // position.
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

    // Mirrors RhinoDB.Core.Tables.IndexKind/Uniqueness - kept in sync by
    // hand (this project doesn't reference RhinoDB.Core, it only reads
    // attribute metadata by name).
    private enum IndexKind { Hash, RedBlackOrdered }
    private enum Uniqueness { Unique, NonUnique }
}
