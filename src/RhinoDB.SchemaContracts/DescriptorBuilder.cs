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
    ) => new() {
        DatabaseFullName = databaseFullName,
        Accessor = accessor,
        RowTypeFullName = rowType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        Kind = kind,
        TableIdHash = tableIdHash,
        Revision = revision,
        PrimaryKey = BuildFieldDescriptor(primaryKeyParam),
        Fields = FlattenFields(primaryCtorParams),
    };

    static public FieldDescriptor BuildFieldDescriptor(IParameterSymbol parameter) {
        var model = SchemaWalk.ToRowFieldModels(ImmutableArray.Create(parameter))[0];
        return new FieldDescriptor { Path = model.FieldName, TypeFullName = model.FieldTypeFullName, Kind = model.Kind };
    }

    // Fully expanded/flattened through every [CustomType] boundary, recursively, to arbitrary depth,
    // using dotted paths ("Loadout.WeaponId", or "Loadout.Cosmetics.HatId" for a CustomType nested inside
    // another CustomType) - see point A of the schema-migration design. A CustomType field with no
    // primary constructor found (shouldn't happen for a well-formed [CustomType], since RHINO016 already
    // requires one elsewhere) falls back to a single opaque field entry rather than throwing, since this
    // is a pure descriptor-building step, not a validation step - validation already happened upstream.
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

    // CustomType full name -> every table (across the WHOLE compilation, not just one database - point A)
    // that embeds it, directly or transitively through another CustomType. Built from live compilation
    // symbols (the generator's own semantic model already has these at hand) - this is a build-time
    // computation, not something reconstructed from an already-committed Descriptor.json, since every
    // consumer that needs it (TableGenerator/CustomTypeGenerator for RHINO019/020's cascade check, the CLI
    // via MSBuildWorkspace) always has a live compilation available when it needs the map.
    static public Dictionary<string, HashSet<(string DatabaseFullName, string Accessor)>> BuildReverseMap(
        IEnumerable<(string DatabaseFullName, string Accessor, ImmutableArray<IParameterSymbol> PrimaryCtorParams)> tables
    ) {
        var map = new Dictionary<string, HashSet<(string, string)>>();
        foreach (var (databaseFullName, accessor, primaryCtorParams) in tables)
            CollectCustomTypes(primaryCtorParams, (databaseFullName, accessor), map, new HashSet<string>());
        return map;
    }

    static void CollectCustomTypes(
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

            // Guards a hypothetical CustomType reference cycle from looping forever - not a case RHINO016
            // is expected to ever let through (nothing in this codebase's real fixtures forms one), but
            // this walk has no other termination proof to lean on, so it's cheap insurance regardless.
            if (!visitedForThisTable.Add(customTypeFullName)) continue;

            var nestedCtor = SchemaWalk.FindPrimaryConstructor(customType);
            if (nestedCtor is not null) CollectCustomTypes(nestedCtor.Parameters, table, map, visitedForThisTable);
        }
    }
}
