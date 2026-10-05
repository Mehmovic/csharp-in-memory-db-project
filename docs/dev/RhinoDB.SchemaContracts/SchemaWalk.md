# `src/RhinoDB.SchemaContracts/SchemaWalk.cs` — dev notes

## `FindPrimaryConstructor`

The "real" primary constructor of a record struct, excluding the compiler-generated copy constructor (a
single parameter of the record's own type) - the exact predicate `TableGenerator`'s `ToTableModels` and
`CustomTypeGenerator`'s `ToCustomTypeModel` both already duplicate independently; consolidated here since
`DescriptorBuilder` needs the identical lookup a third time.

## `ToRowFieldModels`

Shared between `TableGenerator` (row fields) and `CustomTypeGenerator` (custom type fields) - both walk a
primary constructor's parameters into the exact same field shape.
