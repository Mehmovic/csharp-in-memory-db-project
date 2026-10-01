# `src/RhinoDB.Lib/Durability/WalFileHeaderCodec.cs` — dev notes

File header at position 0, `Docs/05-wal-design.md` Phase 1: format version + database
identity. A mismatch on either refuses to open rather than guessing — the WAL for
database A must never be silently replayed against database B's in-memory schema.
