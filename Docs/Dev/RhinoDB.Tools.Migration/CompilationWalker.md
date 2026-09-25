# `src/RhinoDB.Tools.Migration/CompilationWalker.cs` — dev notes

## File header

Builds a `DatabaseContractDescriptor` directly from a loaded `Compilation` - the CLI's counterpart to
`TableGenerator`'s `ForAttributeWithMetadataName`-driven discovery, since this runs outside the incremental
generator pipeline (a one-shot semantic walk over an MSBuildWorkspace-loaded project, not a live IDE/build
session). Reuses `SchemaWalk`/`DescriptorBuilder` from `RhinoDB.SchemaContracts` - the exact same
field-flattening logic `TableGenerator` itself uses for RHINO019, so a table's classification here always
agrees with what the generator would compute for the same source.

## `AllNamedTypes`

A partial type's declaration can span several syntax trees (e.g. a shorthand-expanded row type and, under
MSBuildWorkspace specifically, a duplicate Compile-item inclusion of its own generated output - confirmed
by a real smoke test against `RhinoDB.Run.Server.Sandbox`) - dedupe by symbol identity so one logical type
is never counted twice regardless of how many syntax nodes declare it.
