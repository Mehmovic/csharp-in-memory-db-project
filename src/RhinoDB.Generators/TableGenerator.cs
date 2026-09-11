using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RhinoDB.Generators;

// Milestone 1 scope only: TableKind.Instant, primary key only, no secondary
// indexes, no cross-table Validate() (that's Milestone 2). See
// Docs/02-architecture.md § Transactions and the plan's Part G (Redesigned
// 2026-09-10) for the target shape this is building toward.
[Generator]
public sealed class TableGenerator : IIncrementalGenerator {
    private const string GenerateTableAttributeFullName = "RhinoDB.Core.Tables.GenerateTableAttribute";
    private const string DatabaseAttributeFullName = "RhinoDB.Core.Tables.DatabaseAttribute";
    private const string PrimaryKeyAttributeFullName = "RhinoDB.Core.Tables.PrimaryKeyAttribute";
    private const string AutoIncrementAttributeFullName = "RhinoDB.Core.Tables.AutoIncrementAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var tables = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                GenerateTableAttributeFullName,
                // `record struct Foo(...)` parses as RecordDeclarationSyntax
                // (RecordStructDeclaration kind), not StructDeclarationSyntax -
                // a plain `struct Foo { }` is the only thing that IS the latter.
                predicate: static (node, _) => node is StructDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (ctx, _) => ToTableModel(ctx))
            .Collect();

        var databases = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                DatabaseAttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => ToDatabaseModel(ctx));

        var combined = databases.Combine(tables);
        context.RegisterSourceOutput(combined, static (spc, pair) => Emit(spc, pair.Left, pair.Right));
    }

    static private TableModel ToTableModel(GeneratorAttributeSyntaxContext ctx) {
        var rowType = (INamedTypeSymbol)ctx.TargetSymbol;
        var attribute = ctx.Attributes[0];

        var kind = (TableKind)(int)attribute.ConstructorArguments[0].Value!;
        var databaseType = (INamedTypeSymbol)attribute.ConstructorArguments[1].Value!;

        // The primary (positional) constructor - not the copy constructor a
        // record struct also has (single parameter of the row's own type).
        var primaryCtor = rowType.InstanceConstructors.First(c =>
            c.Parameters.Length > 0 &&
            !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, rowType)));

        var primaryKeyParam = primaryCtor.Parameters.First(p =>
            p.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == PrimaryKeyAttributeFullName));
        var primaryKeyAttribute = primaryKeyParam.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == PrimaryKeyAttributeFullName);
        var primaryKeyKind = primaryKeyAttribute.ConstructorArguments.Length > 0
            ? (IndexKind)(int)primaryKeyAttribute.ConstructorArguments[0].Value!
            : IndexKind.Hash;

        // Independent of [PrimaryKey] - usually the same parameter, but not
        // required to be (a separate, general-purpose attribute).
        var autoIncrementParam = primaryCtor.Parameters.FirstOrDefault(p =>
            p.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == AutoIncrementAttributeFullName));

        return new TableModel(
            rowType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            rowType.Name,
            rowType.ContainingNamespace.IsGlobalNamespace ? null : rowType.ContainingNamespace.ToDisplayString(),
            kind,
            databaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            primaryKeyParam.Name,
            primaryKeyParam.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            primaryKeyKind,
            autoIncrementParam?.Name,
            autoIncrementParam?.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    static private DatabaseModel ToDatabaseModel(GeneratorAttributeSyntaxContext ctx) {
        var databaseType = (INamedTypeSymbol)ctx.TargetSymbol;
        return new DatabaseModel(
            databaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            databaseType.Name,
            databaseType.ContainingNamespace.IsGlobalNamespace ? null : databaseType.ContainingNamespace.ToDisplayString());
    }

    static private void Emit(SourceProductionContext context, DatabaseModel database, ImmutableArray<TableModel> allTables) {
        var tables = allTables.Where(t => t.OwnerDatabaseFullName == database.FullName).ToImmutableArray();

        foreach (var table in tables) {
            if (table.Kind != TableKind.Instant) continue; // Persistent kind lands in Milestone 3
            context.AddSource($"{table.RowTypeName}Ops.g.cs", EmitOpsClass(table));
        }

        context.AddSource($"{database.SimpleName}.g.cs", EmitDatabase(database, tables));
    }

    static private string EmitOpsClass(TableModel table) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("using RhinoDB.Core;");
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

        sb.AppendLine($"public sealed class {opsName} {{");
        sb.AppendLine($"    private readonly Table<{key}, {row}> table;");
        if (hasAutoIncrement) sb.AppendLine("    private readonly AutoIncrementCounter autoIncrement;");
        sb.AppendLine($"    private readonly List<Change<{key}, {row}>> changes = [];");
        sb.AppendLine("    public bool Dirty { get; private set; }");
        sb.AppendLine("    private DbError lastError;");
        sb.AppendLine("    internal DbError LastError => lastError;");
        sb.AppendLine();
        if (hasAutoIncrement) {
            sb.AppendLine($"    public {opsName}(Table<{key}, {row}> table, AutoIncrementCounter autoIncrement) {{");
            sb.AppendLine("        this.table = table;");
            sb.AppendLine("        this.autoIncrement = autoIncrement;");
            sb.AppendLine("    }");
        } else {
            sb.AppendLine($"    public {opsName}(Table<{key}, {row}> table) => this.table = table;");
        }
        sb.AppendLine();

        // Read-your-own-writes: most-recent-first scan over this operation's
        // own staged changes before falling through to real storage - the
        // same algorithm ChangeSetTests.cs already proved, inlined per table.
        // Span + `ref readonly` avoids a struct copy per candidate examined
        // (a Change carries a full TRow) - see
        // Docs/01-performance-principles.md §1/§2 and
        // Docs/02-architecture.md § Transactions.
        sb.AppendLine($"    public Result<{row}> Get({key} id) {{");
        sb.AppendLine("        var span = CollectionsMarshal.AsSpan(changes);");
        sb.AppendLine("        for (var i = span.Length - 1; i >= 0; i--) {");
        sb.AppendLine("            ref readonly var c = ref span[i];");
        sb.AppendLine("            if (!c.Key.Equals(id)) continue;");
        sb.AppendLine($"            return c.Kind == ChangeKind.Delete");
        sb.AppendLine($"                ? Result<{row}>.Error(DbError.IndexKeyNotFound())");
        sb.AppendLine($"                : Result<{row}>.Ok(c.Row);");
        sb.AppendLine("        }");
        sb.AppendLine("        return table.Get(id);");
        sb.AppendLine("    }");
        sb.AppendLine();

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
        sb.AppendLine("        var current = Get(id);");
        sb.AppendLine("        if (!current.IsOk()) return;");
        sb.AppendLine("        changes.Add(new(ChangeKind.Delete, id, current.Unwrap()));");
        sb.AppendLine("        Dirty = true;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Milestone 1 stub: Table.Insert/Update/Delete's own checks at Apply
        // time are the only validation for now. Real pre-apply validation
        // (checking every staged change before mutating any of them, so a
        // later failure never leaves an earlier change applied) is Milestone 2.
        sb.AppendLine("    internal bool Validate() => true;");
        sb.AppendLine();

        sb.AppendLine("    internal void Apply() {");
        sb.AppendLine("        var span = CollectionsMarshal.AsSpan(changes);");
        sb.AppendLine("        for (var i = 0; i < span.Length; i++) {");
        sb.AppendLine("            ref readonly var c = ref span[i];");
        sb.AppendLine("            var result = c.Kind switch {");
        sb.AppendLine("                ChangeKind.Insert => table.Insert(c.Row),");
        sb.AppendLine("                ChangeKind.Update => table.Update(c.Key, c.Row),");
        sb.AppendLine("                _ => table.Delete(c.Key),");
        sb.AppendLine("            };");
        sb.AppendLine("            if (result.IsError()) lastError = result.GetError();");
        sb.AppendLine("        }");
        sb.AppendLine("        changes.Clear();");
        sb.AppendLine("        Dirty = false;");
        sb.AppendLine("    }");

        sb.AppendLine("}");
        return sb.ToString();
    }

    static private string EmitDatabase(DatabaseModel database, ImmutableArray<TableModel> tables) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using RhinoDB.Core;");
        sb.AppendLine("using RhinoDB.Lib.Execution;");
        sb.AppendLine("using RhinoDB.Lib.Indexing;");
        sb.AppendLine("using RhinoDB.Lib.Tables;");
        sb.AppendLine();
        if (database.Namespace is not null) {
            sb.AppendLine($"namespace {database.Namespace};");
            sb.AppendLine();
        }

        var txName = $"{database.SimpleName}Transaction";

        sb.AppendLine($"public sealed class {txName} : ITransaction {{");
        foreach (var table in tables)
            sb.AppendLine($"    public readonly {table.RowTypeName}Ops {table.RowTypeName}s;");
        sb.AppendLine();
        sb.Append($"    internal {txName}(");
        sb.Append(string.Join(", ", tables.Select(t => $"{t.RowTypeName}Ops {Camel(t.RowTypeName)}s")));
        sb.AppendLine(") {");
        foreach (var table in tables)
            sb.AppendLine($"        {table.RowTypeName}s = {Camel(table.RowTypeName)}s;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public Result Apply() {");
        foreach (var table in tables)
            sb.AppendLine($"        if ({table.RowTypeName}s.Dirty && !{table.RowTypeName}s.Validate()) return Result.Error({table.RowTypeName}s.LastError);");
        foreach (var table in tables)
            sb.AppendLine($"        if ({table.RowTypeName}s.Dirty) {table.RowTypeName}s.Apply();");
        sb.AppendLine("        return Result.Ok();");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine($"public partial class {database.SimpleName} {{");
        foreach (var table in tables) {
            var primaryIndex = table.PrimaryKeyKind == IndexKind.RedBlackOrdered
                ? $"new OrderedIndex<{table.PrimaryKeyTypeFullName}>()"
                : $"new HashIndex<{table.PrimaryKeyTypeFullName}>()";
            sb.AppendLine($"    private readonly Table<{table.PrimaryKeyTypeFullName}, {table.RowTypeFullName}> {Camel(table.RowTypeName)}Table = new(chunkSize: 64, {primaryIndex}, static row => row.{table.PrimaryKeyName});");
            if (table.AutoIncrementFieldName is not null)
                sb.AppendLine($"    private readonly AutoIncrementCounter {Camel(table.RowTypeName)}Counter = new();");
        }
        sb.AppendLine();
        sb.Append($"    protected override ITransaction CreateTransaction() => new {txName}(");
        sb.Append(string.Join(", ", tables.Select(t => t.AutoIncrementFieldName is not null
            ? $"new {t.RowTypeName}Ops({Camel(t.RowTypeName)}Table, {Camel(t.RowTypeName)}Counter)"
            : $"new {t.RowTypeName}Ops({Camel(t.RowTypeName)}Table)")));
        sb.AppendLine(");");
        sb.AppendLine("}");

        return sb.ToString();
    }

    static private string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    private sealed class TableModel(
        string rowTypeFullName, string rowTypeName, string? rowNamespace,
        TableKind kind, string ownerDatabaseFullName,
        string primaryKeyName, string primaryKeyTypeFullName, IndexKind primaryKeyKind,
        string? autoIncrementFieldName, string? autoIncrementFieldTypeFullName) {
        public string RowTypeFullName { get; } = rowTypeFullName;
        public string RowTypeName { get; } = rowTypeName;
        public string? RowNamespace { get; } = rowNamespace;
        public TableKind Kind { get; } = kind;
        public string OwnerDatabaseFullName { get; } = ownerDatabaseFullName;
        public string PrimaryKeyName { get; } = primaryKeyName;
        public string PrimaryKeyTypeFullName { get; } = primaryKeyTypeFullName;
        public IndexKind PrimaryKeyKind { get; } = primaryKeyKind;
        public string? AutoIncrementFieldName { get; } = autoIncrementFieldName;
        public string? AutoIncrementFieldTypeFullName { get; } = autoIncrementFieldTypeFullName;
    }

    private sealed class DatabaseModel(string fullName, string simpleName, string? @namespace) {
        public string FullName { get; } = fullName;
        public string SimpleName { get; } = simpleName;
        public string? Namespace { get; } = @namespace;
    }

    private enum TableKind { Instant, Persistent }

    // Mirrors RhinoDB.Core.Tables.IndexKind - kept in sync by hand, same
    // pattern as the local TableKind mirror above (this project doesn't
    // reference RhinoDB.Core, it only reads attribute metadata by name).
    private enum IndexKind { Hash, RedBlackOrdered }
}
