# `Player.bin` — frozen generation-0 fixture

Real key/row bytes produced by `src/RhinoDB.Sandbox.MigrationFixture`'s **generation-0** build of
`Player(int Id, string Name)` — before `Rating` was ever inserted into the middle of the row (the real
breaking change this fixture proves the migration pipeline handles correctly).

## Provenance (do not repeat these steps against this file)

1. `Player` was `(int Id, string Name)`. Built for real (`dotnet build`), then
   `dotnet run --project src/RhinoDB.Sandbox.MigrationFixture -- <path>` called the real, generated
   `LeagueDbPlayerOps.SerializeKey`/`SerializeRow` directly on three known rows
   (`(1, "Alice")`, `(2, "Bob")`, `(3, "Cara")`) and wrote them to this file.
2. `rhinodb migration create` was run for real against the still-generation-0 project, establishing the
   initial `RhinoContracts/Descriptor.json` baseline (nothing to diff against yet, so nothing "breaking" -
   this exercised `MigrationCreateCommand`'s first-run bootstrap path).
3. `Rating` (an `int`) was inserted **in the middle** of `Player`, before `Name` — a genuine `Breaking`
   change per `Docs/06-schema-migration.md` Section 6 (looks like "just adding a field," but isn't).
4. `rhinodb migration create` was run again for real - it detected the breaking change, scaffolded
   `Migrations/Player_Rev0.g.cs` (the frozen `[FrozenSchema(0)]` snapshot of the old shape) and
   `Migrations/Player_FromRev0.cs` (the `[Migration(FromRevision=0)]` stub), and bumped
   `Descriptor.json` to generation 1 / revision 1.
5. The migration stub was hand-implemented: `new Player(old.Id, Rating: 0, old.Name)`.

## The rule this fixture exists to enforce

Per Section 10's explicit anti-circularity trap: generating "old" test bytes with the *current* codegen
makes a migration test pass even when the migration is wrong. This file's bytes were produced once, by a
build that has never seen `Rating` exist - `RhinoDB.Sandbox.MigrationFixture` itself has since moved on to
generation 1 and can never reproduce generation-0 bytes again. **Never regenerate this file.** If the
fixture project's `Player` type ever changes again, that's a new breaking change needing its own new
frozen fixture (`Generation1/`, and so on) - this one stays exactly as it is, forever.

## Consumed by

`RealBreakingChangeDryRunTests.cs` - seeds these exact bytes into a fresh `ColdStore`, runs the real
generated `LeagueDb.RunMigration()`, and asserts the rows come out with `Rating == 0` and `Id`/`Name`
preserved.
