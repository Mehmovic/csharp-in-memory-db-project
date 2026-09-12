using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RhinoDB.Generators;

// Milestone 3 scope: TableKind.Persistent + .Storage (Load/Evict/Peek)
// wired through the generated Apply() path, on top of Milestone 2's
// Instant-kind secondary indexes.
//
// Generated code for Instant-kind tables owns its physical storage
// directly - a DenseArray<TRow> field plus a concrete primary-index field
// (HashIndex<TKey>/OrderedIndex<TKey>) on the {Db} class, driven directly
// by the generated Ops class. There used to be a Table<TKey,TRow> class in
// between (a generic, interface/delegate-driven engine both hand-written
// code and codegen shared) - it was retired from the codebase entirely
// once secondary-index maintenance moved off it (Milestone 2) and it
// became clear the primary-key slot (IUniqueIndex<TKey> interface
// dispatch, Func<TRow,TKey> selector delegate) was the same category of
// unnecessary indirection: the generator always knows every table's exact
// concrete primary index type and primary-key field name at generation
// time, so there's nothing left for either to add.
//
// Persistent-kind tables are different: they go through
// PersistentTable<TKey,TRow>, not an inlined DenseArray/index pair, because
// the cold-storage primitives it composes (ColdStore/ColdTable) are
// `internal` to RhinoDB.Lib - generated code lives in the consuming
// assembly and cannot reach them directly. PersistentTable is the one
// public facade that can, so for Persistent-kind tables it stays exactly
// what Table<TKey,TRow> used to be for Instant-kind ones: a hand-written,
// interface/delegate-driven engine the generator constructs and drives.
// Secondary indexes on Persistent-kind tables are not yet supported
// (RHINO006) - PersistentTable doesn't report the offset/swap information
// a generated Ops class would need to maintain them itself (the
// InsertReturningOffset/DeleteReturningSwap equivalent Table<TKey,TRow> had
// before its own retirement), and that's real, not-yet-built work, not
// just an oversight. See Docs/02-architecture.md § Transactions and the
// plan's Part G (Redesigned 2026-09-10).
//
// Every rule this generator relies on is a diagnostic, not an assumption or
// a crash: ToTableModel never throws on malformed input (a missing
// [PrimaryKey], an empty Accessor, a composite index that disagrees with
// itself, [Index] on a Persistent-kind table) - it reports a real compile
// error and that one table is skipped (diagnostics non-empty => Model is
// null), so one broken table doesn't take down the whole compilation and
// the author sees an actual message, not a generator stack trace.
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

    static private readonly DiagnosticDescriptor PersistentIndexesNotSupportedDiagnostic = new(
        "RHINO006", "Secondary indexes not yet supported on Persistent-kind tables",
        "'{0}' is TableKind.Persistent and declares [Index] on '{1}' - secondary indexes on persistent " +
        "tables are Milestone 3+ scope, not yet built (see Docs/03-roadmap.md). Remove [Index] or use TableKind.Instant.",
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

    static private Location Loc(AttributeData attr) => attr.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? Location.None;

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

        // Independent of [PrimaryKey] - usually the same parameter, but not
        // required to be (a separate, general-purpose attribute).
        var autoIncrementParam = primaryCtor.Parameters.FirstOrDefault(p =>
            p.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == AutoIncrementAttributeFullName));

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

        if (kind == TableKind.Persistent) {
            foreach (var x in indexedParams)
                diagnostics.Add(Diagnostic.Create(PersistentIndexesNotSupportedDiagnostic, Loc(x.Attr), rowType.Name, x.Param.Name));
        }

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
            autoIncrementParam?.Name,
            autoIncrementParam?.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
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
        var hasAutoIncrement = table.AutoIncrementFieldName is not null;
        var uniqueIndexes = table.Indexes.Where(i => i.Uniqueness == Uniqueness.Unique).ToImmutableArray();
        var pk = table.PrimaryKeyAccessor;
        var isPersistent = table.Kind == TableKind.Persistent;
        var primaryIndexType = PrimaryIndexType(table);

        sb.AppendLine($"public sealed class {opsName} {{");
        if (isPersistent) {
            sb.AppendLine($"    private readonly PersistentTable<{key}, {row}> table;");
        } else {
            sb.AppendLine($"    private readonly DenseArray<{row}> storage;");
            sb.AppendLine($"    private readonly {primaryIndexType} primaryIndex;");
        }
        if (hasAutoIncrement) sb.AppendLine("    private readonly AutoIncrementCounter autoIncrement;");
        foreach (var idx in table.Indexes)
            sb.AppendLine($"    private readonly {ConcreteIndexType(idx)} {IndexFieldName(idx)};");
        sb.AppendLine($"    private readonly List<Change<{key}, {row}>> changes = [];");
        sb.AppendLine("    public bool Dirty { get; private set; }");
        sb.AppendLine("    private DbError lastError;");
        sb.AppendLine("    internal DbError LastError => lastError;");
        sb.AppendLine();

        // Constructor: [storage, primaryIndex] or [table], [autoIncrement],
        // then one concrete-typed parameter per index - position-matched
        // 1:1 against EmitDatabase's call site, both generated from the
        // same TableModel.Indexes order.
        if (isPersistent) {
            sb.Append($"    public {opsName}(PersistentTable<{key}, {row}> table");
        } else {
            sb.Append($"    public {opsName}(DenseArray<{row}> storage, {primaryIndexType} primaryIndex");
        }
        if (hasAutoIncrement) sb.Append(", AutoIncrementCounter autoIncrement");
        foreach (var idx in table.Indexes) sb.Append($", {ConcreteIndexType(idx)} {IndexFieldName(idx)}");
        sb.AppendLine(") {");
        if (isPersistent) {
            sb.AppendLine("        this.table = table;");
        } else {
            sb.AppendLine("        this.storage = storage;");
            sb.AppendLine("        this.primaryIndex = primaryIndex;");
        }
        if (hasAutoIncrement) sb.AppendLine("        this.autoIncrement = autoIncrement;");
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
        // [PrimaryKey(Accessor = "...")] renames it. For Persistent kind,
        // the fallback (PersistentTable.Get) never touches cold storage -
        // Get/GetByOffset are memory-only by design (see Cold storage decision 1).
        sb.AppendLine($"    public Result<{row}> {pk}({key} id) {{");
        sb.AppendLine("        var span = CollectionsMarshal.AsSpan(changes);");
        sb.AppendLine("        for (var i = span.Length - 1; i >= 0; i--) {");
        sb.AppendLine("            ref readonly var c = ref span[i];");
        sb.AppendLine("            if (!c.Key.Equals(id)) continue;");
        sb.AppendLine($"            return c.Kind == ChangeKind.Delete");
        sb.AppendLine($"                ? Result<{row}>.Error(DbError.IndexKeyNotFound())");
        sb.AppendLine($"                : Result<{row}>.Ok(c.Row);");
        sb.AppendLine("        }");
        if (isPersistent) {
            sb.AppendLine("        return table.Get(id);");
        } else {
            sb.AppendLine("        var offsetResult = primaryIndex.GetOffset(id);");
            sb.AppendLine("        if (offsetResult.IsError()) return offsetResult.Void();");
            sb.AppendLine("        return storage.Get(offsetResult.Unwrap());");
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        // Secondary-index read accessors - direct against the real index +
        // real storage only, NOT overlay-aware yet (doesn't see this
        // operation's own not-yet-applied staged writes by index key).
        // Milestone 4's job; see Docs/02-architecture.md § Transactions.
        // table.Indexes is always empty for Persistent kind (RHINO006), so
        // this loop naturally emits nothing there.
        foreach (var idx in table.Indexes) {
            var parameters = string.Join(", ", idx.Fields.Select(f => $"{f.FieldTypeFullName} {Camel(f.FieldName)}"));
            var keyExpr = idx.Fields.Length == 1
                ? Camel(idx.Fields[0].FieldName)
                : $"({string.Join(", ", idx.Fields.Select(f => Camel(f.FieldName)))})";
            if (idx.Uniqueness == Uniqueness.Unique) {
                sb.AppendLine($"    public Result<{row}> {idx.AccessorName}({parameters}) {{");
                sb.AppendLine($"        var offsetResult = {IndexFieldName(idx)}.GetOffset({keyExpr});");
                sb.AppendLine($"        if (offsetResult.IsError()) return offsetResult.Void();");
                sb.AppendLine($"        return storage.Get(offsetResult.Unwrap());");
                sb.AppendLine("    }");
            } else {
                sb.AppendLine($"    public List<{row}> {idx.AccessorName}({parameters}) {{");
                sb.AppendLine($"        var offsets = {IndexFieldName(idx)}.GetOffsets({keyExpr});");
                sb.AppendLine($"        var result = new List<{row}>(offsets.Count);");
                sb.AppendLine("        foreach (var offset in offsets) result.Add(storage.Get(offset));");
                sb.AppendLine("        return result;");
                sb.AppendLine("    }");
            }
        }
        if (table.Indexes.Length > 0) sb.AppendLine();

        sb.AppendLine($"    public void Insert({row} row) {{");
        if (hasAutoIncrement) {
            sb.AppendLine($"        if (row.{table.AutoIncrementFieldName} == 0)");
            sb.AppendLine($"            row = row with {{ {table.AutoIncrementFieldName} = ({table.AutoIncrementFieldTypeFullName})autoIncrement.Next() }};");
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
        //    For Persistent kind, "committed storage" here means memory only
        //    (PersistentTable.Get never touches cold), matching decision 1.
        //  - Update: primary-key immutability, then unique-index conflicts
        //    using the row's real physical offset *if it already has one*
        //    (Instant kind only - PersistentTable has no offset to give, and
        //    there are no unique secondary indexes on Persistent tables to
        //    check anyway, so the whole offset-fetch is skipped there and
        //    whenever a table simply has no unique secondary index at all).
        //    A key with no offset yet (inserted earlier in this same batch,
        //    not yet applied) is left to Apply()'s own in-order replay to
        //    resolve correctly, same as before this validation existed -
        //    Apply() replays in staged order, so by the time this Update
        //    replays, the earlier Insert has already physically landed.
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
        if (isPersistent) {
            sb.AppendLine("                if (!determined) existsAlready = table.Get(c.Key).IsOk();");
        } else {
            sb.AppendLine("                if (!determined) existsAlready = primaryIndex.GetOffset(c.Key).IsOk();");
        }
        sb.AppendLine("                if (existsAlready) { lastError = DbError.DuplicateKey(); return false; }");
        foreach (var idx in uniqueIndexes) {
            sb.AppendLine("                {");
            sb.AppendLine($"                    var checkResult = {IndexFieldName(idx)}.GetOffset({KeyExpr("c.Row", idx)});");
            sb.AppendLine("                    if (checkResult.IsOk()) { lastError = DbError.DuplicateKey(); return false; }");
            sb.AppendLine("                }");
        }
        sb.AppendLine("            } else {");
        sb.AppendLine($"                if (!c.Row.{table.PrimaryKeyName}.Equals(c.Key)) {{ lastError = DbError.PrimaryKeyImmutable(); return false; }}");
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

        // Apply: Instant kind owns storage/primaryIndex directly (no
        // Table<TKey,TRow> in between anymore - see the file-level comment)
        // and inlines the same swap-remove handling Table<TKey,TRow> used to:
        // DenseArray.Delete can relocate the physically-last row into the
        // freed slot, and every index - primary and secondary - must
        // repoint to follow it. Persistent kind delegates the whole
        // operation to PersistentTable, which already handles memory+cold
        // consistency (and its own swap-remove, internally) - there are no
        // secondary indexes to maintain there yet (RHINO006), so there's
        // nothing left for Apply() to do beyond checking the Result. Update
        // always unconditionally re-registers every index against the new
        // row, same as this project's standing self-collision-fix behavior
        // - see Docs/03-roadmap.md Stage 3's note.
        sb.AppendLine("    internal void Apply() {");
        sb.AppendLine("        foreach (ref readonly var c in CollectionsMarshal.AsSpan(changes)) {");
        sb.AppendLine("            switch (c.Kind) {");
        sb.AppendLine("                case ChangeKind.Insert: {");
        if (isPersistent) {
            sb.AppendLine("                    var insertResult = table.Insert(c.Row);");
            sb.AppendLine("                    if (insertResult.IsError()) { lastError = insertResult.GetError(); break; }");
        } else {
            sb.AppendLine($"                    var pk = c.Row.{table.PrimaryKeyName};");
            sb.AppendLine("                    if (primaryIndex.GetOffset(pk).IsOk()) { lastError = DbError.DuplicateKey(); break; }");
            sb.AppendLine("                    var offset = storage.Insert(c.Row);");
            sb.AppendLine("                    primaryIndex.Insert(pk, offset);");
            foreach (var idx in table.Indexes)
                sb.AppendLine($"                    {IndexFieldName(idx)}.Insert({KeyExpr("c.Row", idx)}, offset);");
        }
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("                case ChangeKind.Update: {");
        if (isPersistent) {
            sb.AppendLine("                    var updateResult = table.Update(c.Key, c.Row);");
            sb.AppendLine("                    if (updateResult.IsError()) { lastError = updateResult.GetError(); break; }");
        } else {
            sb.AppendLine("                    var offsetResult = primaryIndex.GetOffset(c.Key);");
            sb.AppendLine("                    if (offsetResult.IsError()) { lastError = offsetResult.GetError(); break; }");
            sb.AppendLine("                    var offset = offsetResult.Unwrap();");
            sb.AppendLine("                    var oldRow = storage.Get(offset);");
            sb.AppendLine("                    storage.Set(offset, c.Row);");
            foreach (var idx in table.Indexes) {
                var deleteArgs = idx.Uniqueness == Uniqueness.Unique ? KeyExpr("oldRow", idx) : $"{KeyExpr("oldRow", idx)}, offset";
                sb.AppendLine($"                    {IndexFieldName(idx)}.Delete({deleteArgs});");
                sb.AppendLine($"                    {IndexFieldName(idx)}.Insert({KeyExpr("c.Row", idx)}, offset);");
            }
        }
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("                default: {");
        if (isPersistent) {
            sb.AppendLine("                    var deleteResult = table.Delete(c.Key);");
            sb.AppendLine("                    if (deleteResult.IsError()) { lastError = deleteResult.GetError(); break; }");
        } else {
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
        }
        sb.AppendLine("                    break;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        changes.Clear();");
        sb.AppendLine("        Dirty = false;");
        sb.AppendLine("    }");

        // .Storage groups the Persistent-kind-only Load/Evict/Peek pass-
        // throughs - these already only ever touch `table` directly, never
        // the change log (see Docs/02-architecture.md § Cold storage), so
        // they need no staging/validation of their own.
        if (isPersistent) {
            sb.AppendLine();
            sb.AppendLine("    public StorageAccessor Storage => new(table);");
            sb.AppendLine($"    public readonly struct StorageAccessor(PersistentTable<{key}, {row}> table) {{");
            sb.AppendLine($"        public Result Load({key} id) => table.Load(id);");
            sb.AppendLine($"        public Result Evict({key} id) => table.Evict(id);");
            sb.AppendLine($"        public Result<{row}> Peek({key} id) => table.Peek(id);");
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
            sb.AppendLine($"    private readonly DenseArray<{table.RowTypeFullName}> {Camel(table.Accessor)}Storage = new(chunkSize: 64);");
            sb.AppendLine($"    private readonly {PrimaryIndexType(table)} {Camel(table.Accessor)}PrimaryIndex = new();");
            if (table.AutoIncrementFieldName is not null)
                sb.AppendLine($"    private readonly AutoIncrementCounter {Camel(table.Accessor)}Counter = new();");
            foreach (var idx in table.Indexes) {
                var fieldName = $"{Camel(table.Accessor)}{idx.AccessorName}Index";
                sb.AppendLine($"    private readonly {ConcreteIndexType(idx)} {fieldName} = new();");
            }
        }
        // Persistent-kind PersistentTable fields need a ColdStore, only
        // available once the base DbContext<TTx>(ColdStore) constructor has
        // already run - a field initializer can't reference even an
        // inherited instance property (same CS0236 category hit during
        // Milestone 2's interface retirement), so these are assigned in the
        // constructor below instead, using the constructor's own `cold`
        // parameter directly (never `this.Cold`, sidestepping the question
        // of exactly when that inherited property becomes safe to read).
        foreach (var table in persistentTables) {
            sb.AppendLine($"    private readonly PersistentTable<{table.PrimaryKeyTypeFullName}, {table.RowTypeFullName}> {Camel(table.Accessor)}Table;");
            if (table.AutoIncrementFieldName is not null)
                sb.AppendLine($"    private readonly AutoIncrementCounter {Camel(table.Accessor)}Counter = new();");
        }
        sb.AppendLine();

        if (persistentTables.Length > 0) {
            sb.AppendLine($"    public {database.SimpleName}(ColdStore cold) : base(cold) {{");
            foreach (var table in persistentTables) {
                var primaryIndex = table.PrimaryKeyKind == IndexKind.RedBlackOrdered
                    ? $"new OrderedIndex<{table.PrimaryKeyTypeFullName}>()"
                    : $"new HashIndex<{table.PrimaryKeyTypeFullName}>()";
                sb.AppendLine($"        {Camel(table.Accessor)}Table = new PersistentTable<{table.PrimaryKeyTypeFullName}, {table.RowTypeFullName}>(cold, \"{table.Accessor}\", chunkSize: 64, {primaryIndex}, static row => row.{table.PrimaryKeyName});");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.Append($"    protected override {txName} CreateTransaction() => new {txName}(");
        sb.Append(string.Join(", ", tables.Select(t => {
            var args = new System.Collections.Generic.List<string>();
            if (t.Kind == TableKind.Persistent) {
                args.Add($"{Camel(t.Accessor)}Table");
            } else {
                args.Add($"{Camel(t.Accessor)}Storage");
                args.Add($"{Camel(t.Accessor)}PrimaryIndex");
            }
            if (t.AutoIncrementFieldName is not null) args.Add($"{Camel(t.Accessor)}Counter");
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
        string primaryKeyAccessor, string accessor,
        string? autoIncrementFieldName, string? autoIncrementFieldTypeFullName,
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

        public string? AutoIncrementFieldName { get; } = autoIncrementFieldName;
        public string? AutoIncrementFieldTypeFullName { get; } = autoIncrementFieldTypeFullName;
        public ImmutableArray<IndexModel> Indexes { get; } = indexes;
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
