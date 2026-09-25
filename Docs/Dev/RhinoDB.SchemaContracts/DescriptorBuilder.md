# `src/RhinoDB.SchemaContracts/DescriptorBuilder.cs` — dev notes

## `FlattenFields`

Fully expanded/flattened through every `[CustomType]` boundary, recursively, to arbitrary depth, using
dotted paths (`"Loadout.WeaponId"`, or `"Loadout.Cosmetics.HatId"` for a CustomType nested inside another
CustomType) - see point A of the schema-migration design. A CustomType field with no primary constructor
found (shouldn't happen for a well-formed `[CustomType]`, since RHINO016 already requires one elsewhere)
falls back to a single opaque field entry rather than throwing, since this is a pure descriptor-building
step, not a validation step - validation already happened upstream.

## `BuildReverseMap`

CustomType full name -> every table (across the WHOLE compilation, not just one database - point A) that
embeds it, directly or transitively through another CustomType. Built from live compilation symbols (the
generator's own semantic model already has these at hand) - this is a build-time computation, not something
reconstructed from an already-committed Descriptor.json, since every consumer that needs it
(TableGenerator/CustomTypeGenerator for RHINO019/020's cascade check, the CLI via MSBuildWorkspace) always
has a live compilation available when it needs the map.

## `CollectCustomTypes`

Guards a hypothetical CustomType reference cycle from looping forever - not a case RHINO016 is expected to
ever let through (nothing in this codebase's real fixtures forms one), but this walk has no other
termination proof to lean on, so it's cheap insurance regardless.
