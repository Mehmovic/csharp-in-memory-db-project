# `src/RhinoDB.Lib/Indexing/IndexComparers.cs` — dev notes

Struct comparers used as the `TCmp` type argument of `BTreeIndex<TKey,TCmp>`
and `NonUniqueBTreeIndex<TKey,TCmp>` (replaced the `OrdinalComparers` factory
of class-based `IComparer<T>` instances, 2026-10-03). Being structs used
through a `where TCmp : struct, IComparer<TKey>` constraint, every compare is
devirtualized and inlined per closed generic instantiation.

## `DefaultComparer<T>`

Delegates to `Comparer<T>.Default`, which the JIT already devirtualizes for a
known `T` (an intrinsic). Deliberately has NO `where T : IComparable<T>`
constraint: enums only implement the non-generic `IComparable`, so a
constrained comparer could not be the element comparer of a composite key
containing an enum field. Also null-safe for reference types, same as
`Comparer<T>.Default`.

For `string` this is the culture-sensitive comparer - never emitted by the
generator for string keys (see `OrdinalStringComparer`).

## `OrdinalStringComparer`

`string.CompareOrdinal`: a memcmp-style compare, ~7x faster than the culture
comparer and, more importantly, host-culture independent - two servers scan
the same data in the same order.

## `TupleComparer<...>`

One per supported composite arity (2 and 3, matching the generator's limit),
parameterized by one struct comparer per element, so `(int, string)` gets an
inlined int compare followed by an ordinal string compare. The interface is
implemented over the unnamed tuple shape; tuple element names are erased, so
it satisfies the constraint for generated named-tuple keys like
`(int ClubId, string Name)`.

## Comparers the store recognises

`ChunkedKeyStore` switches search strategy on the exact comparer TYPE (a
JIT-time constant, see its dev notes): `OrdinalStringComparer` turns on the
anchor + packed-prefix mode, and `DefaultComparer<int|long|uint|ulong>` turns
on the Vector256 tail search. Any other comparer - including a hand-written
struct with identical ordering - takes the plain scalar path, so new key
kinds that want those fast paths need to be added there explicitly.
