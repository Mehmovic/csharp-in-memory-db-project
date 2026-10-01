# `src/RhinoDB.Generators/FrozenSchemaGenerator.cs` — dev notes

## File header

A `[FrozenSchema(Revision=N)]` type is a captured old shape of a former `[Table]` row - it exists purely so
the migration engine (Phase 4) and genesis replay (Phase 5) can decode archived Raw bytes written under an
older revision. Unlike `[Table]`/`[CustomType]`, it never carries `[MemoryPackable]`/`[MessagePackObject]` -
nothing ever sends a frozen row over IDC/Client, so that mandatory-attribute contract (RHINO015/RHINO017)
deliberately does not apply here. It does still need `[PrimaryKey]` on one field, mirroring a live row,
since archived WAL entries store Key and Row bytes separately (`WalChange`'s shape) and a frozen type must
be able to decode both independently.
