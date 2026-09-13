# `src/RhinoDB.Native/DirectorySync.cs` — dev notes

Added 2026-09-13 for `ColdStore.Open`'s one-time parent-directory-fsync fix
(`Docs/02-architecture.md` § "Cold storage", `Docs/Dev/RhinoDB.Lib/Cold/ColdStore.md`).
libmdbx exposes nothing for this - it's a raw POSIX `open`/`fsync`/`close`
against the directory itself, not an mdbx API call, so it lives in its own
`PosixNative` binding (`Interop/PosixNative.cs`) rather than `MdbxNative.cs`.

`TrySync` is a no-op, always-`true` on Windows (`OperatingSystem.IsWindows()`
short-circuits before touching `PosixNative` at all - the P/Invoke against
`libc` is never even attempted there, so this never risks a load-time failure
on a platform that has no `libc` to resolve). This isn't a shortcut taken for
convenience - it's the correct behavior: the "fsync doesn't cover the
directory entry" gotcha this exists to close is specifically a POSIX/ext4-era
filesystem concern (NTFS journals directory metadata differently, and there's
no clean, widely-used Windows equivalent to "open a directory as a file
descriptor and fsync it").

`ORdOnly = 0` (`O_RDONLY`) - deliberately does *not* also pass `O_DIRECTORY`.
That flag's numeric value differs across platforms/architectures and its only
purpose here would be defensive (fail fast if `path` somehow isn't a
directory) - unnecessary since every call site already knows `path` is a
directory by construction (`ColdStore.Open`'s own target), so skipping it
avoids one more platform-specific magic number to get wrong for no real
safety gain in this codebase's actual usage.

Errors are surfaced via `out int errno` (`Marshal.GetLastPInvokeError()`)
rather than thrown - matches `RhinoDB.Native`'s standing convention (see
`MdbxEnvironment`/`Transaction`) of returning raw codes and letting
`RhinoDB.Lib` do the `Result`/`DbError` translation at the assembly boundary;
`RhinoDB.Native` itself stays exception-free.

**Not runtime-verified on Linux from this Windows dev environment** - same
caveat already carried for the rest of the native binding since Part A
(`open`/`fsync`/`close` are standard, well-understood POSIX calls, and the
binding itself is a straightforward three-call sequence, but it hasn't
actually executed on a Linux host during this work).
