using System.Collections.Immutable;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace RhinoDB.SchemaContracts;

static public class DescriptorBuilder {
    static public TableDescriptor BuildTable(
        string databaseFullName,
        string accessor,
        INamedTypeSymbol rowType,
        IParameterSymbol primaryKeyParam,
        ImmutableArray<IParameterSymbol> primaryCtorParams,
        string kind,
        uint tableIdHash,
        int revision
    ) => new TableDescriptor {
        DatabaseFullName = databaseFullName,
        Accessor = accessor,
        RowTypeFullName = rowType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        Kind = kind,
        NameHash = tableIdHash,
        Revision = revision,
        PrimaryKey = BuildFieldDescriptor(primaryKeyParam),
        Fields = FlattenFields(primaryCtorParams),
    };

    static public FieldDescriptor BuildFieldDescriptor(IParameterSymbol parameter) {
        var model = SchemaWalk.ToRowFieldModels(ImmutableArray.Create(parameter))[0];
        return new FieldDescriptor { Path = model.FieldName, TypeFullName = model.FieldTypeFullName, Kind = model.Kind };
    }

    static public List<FieldDescriptor> FlattenFields(ImmutableArray<IParameterSymbol> parameters, string pathPrefix = "") {
        var result = new List<FieldDescriptor>();
        var models = SchemaWalk.ToRowFieldModels(parameters);

        for (var i = 0; i < parameters.Length; i++) {
            var model = models[i];
            var path = pathPrefix.Length == 0 ? model.FieldName : $"{pathPrefix}.{model.FieldName}";

            if (model.IsCustomType && parameters[i].Type is INamedTypeSymbol customType) {
                var nestedCtor = SchemaWalk.FindPrimaryConstructor(customType);
                if (nestedCtor is not null) {
                    result.AddRange(FlattenFields(nestedCtor.Parameters, path));
                    continue;
                }
            }

            result.Add(new FieldDescriptor { Path = path, TypeFullName = model.FieldTypeFullName, Kind = model.Kind });
        }

        return result;
    }

    static public Dictionary<string, HashSet<(string DatabaseFullName, string Accessor)>> BuildReverseMap(
        IEnumerable<(string DatabaseFullName, string Accessor, ImmutableArray<IParameterSymbol> PrimaryCtorParams)> tables
    ) {
        var map = new Dictionary<string, HashSet<(string, string)>>();
        foreach (var (databaseFullName, accessor, primaryCtorParams) in tables)
            CollectCustomTypes(primaryCtorParams, (databaseFullName, accessor), map, new HashSet<string>());
        return map;
    }

    static private void CollectCustomTypes(
        ImmutableArray<IParameterSymbol> parameters,
        (string DatabaseFullName, string Accessor) table,
        Dictionary<string, HashSet<(string, string)>> map,
        HashSet<string> visitedForThisTable
    ) {
        foreach (var p in parameters) {
            if (p.Type is not INamedTypeSymbol customType || !SchemaWalk.HasCustomTypeAttribute(customType)) continue;

            var customTypeFullName = customType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (!map.TryGetValue(customTypeFullName, out var embeddingTables)) {
                embeddingTables = [];
                map[customTypeFullName] = embeddingTables;
            }
            embeddingTables.Add(table);

            if (!visitedForThisTable.Add(customTypeFullName)) continue;

            var nestedCtor = SchemaWalk.FindPrimaryConstructor(customType);
            if (nestedCtor is not null) CollectCustomTypes(nestedCtor.Parameters, table, map, visitedForThisTable);
        }
    }
}
