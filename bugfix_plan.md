# MongoZen Code Review — Bugs & Inefficiencies

## Overview

This is a structured review of the current MongoZen implementation covering `MutableDbSet`, `DbContextSession`, `DbSet`, `InMemoryDbSet`, `DbContext`, `DocId`, `EntityIdAccessor`, `ArenaDictionary`, `ShadowStructsGenerator`, and `DbContextSessionsGenerator`. Issues are grouped by severity.

---

## 🔴 Bugs (Correctness Issues)

### 1. Arena memory leaked on `ClearTracking()` — use-after-free risk

**File:** [`DbContextSession.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/src/MongoZen/DbContextSession.cs#L216-L224)  
**Lines:** 216–224

`ClearTracking()` calls `_arena.Reset()`, which reclaims all arena memory. However, it does **not** zero out the `ShadowPtr` values stored in `_trackedEntities`. Any call path that tries to use a stale `ShadowPtr` after the reset will dereference freed memory.

The dictionary is cleared right after (`_trackedEntities.Clear()`), but the order of operations is dangerous if any concurrent code (or future extension) reads `_trackedEntities` between the `_arena.Reset()` and the `_trackedEntities.Clear()` call. More importantly, `RefreshShadows` reads entries from `_trackedEntities` — if it is ever called after an arena reset but before `Clear()`, it will access a dangling pointer.

**Proposed fix:** Clear `_trackedEntities` *before* calling `_arena.Reset()`.

---

### 2. `SaveChangesAsync` silently swallows `AcceptChanges` on exception

**File:** [`DbContextSession.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/src/MongoZen/DbContextSession.cs#L89-L106)  
**Lines:** 89–106

```csharp
await _session.CommitTransactionAsync(cancellationToken);
_session.StartTransaction(); // auto-start next
AcceptChanges();             // <-- only called on happy path
```

If `CommitTransactionAsync` throws (network timeout, write conflict, etc.) `AcceptChanges()` is never called. That is correct, *but* the new transaction is also never started, so the session is left in a broken state — not in a transaction, but not re-initialized either. The subsequent `EnsureTransactionActive()` call in the next `SaveChangesAsync` *will* attempt to start a new transaction, but `_session` still holds the old session handle. Whether `_session.StartTransaction()` is safe to call on a session whose previous transaction failed-to-commit needs a guard here.

---

### 3. `TransactionsSupported()` leaks a `CancellationTokenSource`

**File:** [`DbContextSession.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/src/MongoZen/DbContextSession.cs#L340)  
**Line:** 340

```csharp
var hello = database.RunCommand<BsonDocument>(new BsonDocument("hello", 1),
    cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
```

`CancellationTokenSource` implements `IDisposable`. The instance created here is never disposed, causing a managed resource leak on every cold-start detection call. Because the result is cached, this only happens once per client, but it is still a correctness issue.

**Fix:** Use `using var cts = new CancellationTokenSource(...)`.

---

### 4. `DocId.WriteHash128` violates aliasing rules

**File:** [`DocId.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/src/MongoZen/DocId.cs#L40-L48)  
**Lines:** 40–48

```csharp
private void WriteHash128(ReadOnlySpan<byte> data)
{
    var hash = XxHash128.HashToUInt128(data);
    unsafe
    {
        *(UInt128*)Unsafe.AsPointer(ref _part1) = hash;
    }
}
```

`_part1` is a `ulong` at `[FieldOffset(4)]`. Writing a `UInt128` (16 bytes) starting at `_part1` means the write overflows into whatever follows `_part1` inside the struct — which is `_part2` at `[FieldOffset(12)]`. With a `[FieldOffset(4)]` start and a 16-byte `UInt128`, the write covers offsets 4–19, which exactly covers both `_part1` and `_part2`. This is *intentional* but undocumented and fragile: it relies on `LayoutKind.Explicit` layout details that the JIT is not explicitly told to honour. This is effectively undefined behaviour from the CLR/JIT perspective (pointer aliasing across fields).

A safer implementation would write the two `ulong` halves of the `UInt128` explicitly:
```csharp
_part1 = (ulong)(hash & ulong.MaxValue);
_part2 = (ulong)(hash >> 64);
```

---

### 5. `ArenaDictionary.Grow()` uses old freed arena pointers

**File:** [`ArenaDictionary.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/src/MongoZen/Collections/ArenaDictionary.cs#L137-L166)  
**Lines:** 137–166

`Grow()` calls `AllocateArrays(newCap)` which allocates new blocks from the arena and updates `_entries`/`_occupied`. The old pointers (`oldEntries`, `oldOccupied`) still point into valid arena memory **only if the arena does not compact**. If `ArenaAllocator.Alloc` is ever backed by a bump-pointer allocator that compacts on `Reset()`, the grow during an active session is fine, but if `Reset()` is ever called while a `Grow()` is in-flight (unlikely in single-threaded use), the old pointers become dangling.

More concretely: the old entries are never explicitly freed, which is expected for an arena allocator — but this also means every `Grow()` accumulates dead memory in the arena for the session's lifetime. For entities with large dictionaries that are refreshed repeatedly, this could cause the arena to grow without bound.

---

### 6. `InMemoryDbSet.CommitAsync` ignores `dedupeBuffer` — allows double-writes

**File:** [`InMemoryDbSet.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/src/MongoZen/InMemoryDbSet.cs#L84-L128)  
**Lines:** 84–128

The `InMemoryDbSet.CommitAsync` implementation accepts `dedupeBuffer`, `upsertBuffer`, and `rawIdBuffer` from the caller but **never uses them**. This means that calling `Remove` on an entity and then `Add`-ing it with the same ID in the same unit-of-work will result in the entity being first removed, then re-added — which might be correct — but the `dirty` entities from the tracker will then *also* overwrite it with the pre-change version. The real `DbSet.CommitAsync` guards against this via the `dedupeBuffer`. The in-memory version diverges in semantics.

---

### 7. `MutableDbSet.QueryAsync` uses different code paths for sessions vs. no-session

**File:** [`MutableDbSet.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/src/MongoZen/MutableDbSet.cs#L180-L198)  
**Lines:** 180–198

When `_includes.Count > 0 && _dbSet is DbSet<TEntity>` is true, the session is passed through. When `_includes.Count == 0` but a session exists, the code pattern is:

```csharp
var results = session != null && _dbSet is DbSet<TEntity> ds
    ? await ds.QueryAsync(filter, session, cancellationToken)
    : await _dbSet.QueryAsync(filter, cancellationToken);
```

The pattern `_dbSet is DbSet<TEntity> ds` performs the cast test **again** after it already passed or failed above. This is not a bug per se, but the interaction between both branches means that if `_includes.Count > 0` and `_dbSet is NOT DbSet<TEntity>` (e.g., a custom implementation), the method falls through to `QueryWithIncludesAsync` which will throw `InvalidOperationException` only when it tries to call `sessionTyped.GetDbContext()`. The error message is confusing — it says "Includes require an IDbContextSession tracker" but the real cause is the underlying set not being a `DbSet<TEntity>`.

---

## 🟡 Inefficiencies & Design Issues

### 8. `DbContext.GetCollectionName()` uses reflection on every call

**File:** [`DbContext.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/src/MongoZen/DbContext.cs#L29-L44)  
**Lines:** 29–44

This method scans all properties using reflection, creates a generic type with `MakeGenericType`, calls `GetProperty`, then `GetValue` — every single time it is called. The `QueryWithIncludesAsync` path in `MutableDbSet` calls this per-include, per-query execution. With source generators already generating the session class, a compile-time generated `GetCollectionName` override would be trivial and would make this O(1) instead of O(n) property scan.

---

### 9. `DbContextSession.RefreshShadows<T>` iterates a `Dictionary` and re-assigns entries mid-loop

**File:** [`DbContextSession.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/src/MongoZen/DbContextSession.cs#L126-L139)  
**Lines:** 126–139

```csharp
// Updating values in-place while iterating is safe for Dictionary in .NET.
foreach (var kvp in map)
{
    ...
    map[kvp.Key] = (entry.Entity, newShadowPtr, entry.Differ, entry.Materializer);
}
```

The comment says this is safe — and it is for value updates to *existing keys* in .NET's `Dictionary<K,V>`. But it is a subtle invariant that deserves a unit test explicitly validating it, and it would fail silently with a `HashSet` or any future collection type. More importantly, the old `ShadowPtr` is **not** freed before being overwritten. This is by design (arena-managed), but each `RefreshShadows` call allocates a new shadow block without reclaiming the old one, growing the arena every `SaveChangesAsync` call for sessions that live a long time.

---

### 10. `EntityIdAccessor<T>` uses `IIdConvention` as a dictionary key by reference equality

**File:** [`EntityIdAccessor.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/src/MongoZen/EntityIdAccessor.cs#L13-L14)  
**Lines:** 13–14

```csharp
private static readonly ConcurrentDictionary<IIdConvention, Lazy<Func<TEntity, object?>>> GetterCache = new();
```

The key is `IIdConvention`. Unless `IIdConvention` implementations override `Equals`/`GetHashCode`, the dictionary uses reference equality. If callers create a `new DefaultIdConvention()` for each `DbContextOptions` instance (which `new Conventions()` does), the cache will never hit — a new `Lazy<Func<...>>` is created and the expression compiled every time. Since `DbContextOptions` creates `new Conventions()` by default (DbContextOptions.cs line 15, 40), and `Conventions` creates `new DefaultIdConvention()`, every `new DbContextOptions()` will result in a cache miss.

**Verify:** Does `DefaultIdConvention` override `Equals`? If not, this is a significant performance issue: the LINQ expression tree is compiled on every `DbContextOptions` construction.

---

### 11. `ShadowStructsGenerator` — `IsPrimitive` treats all `SpecialType != None` as primitive

**File:** [`ShadowStructsGenerator.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/src/MongoZen/SourceGenerator/ShadowStructsGenerator.cs#L214-L223)  
**Lines:** 214–223

```csharp
private static bool IsPrimitive(ITypeSymbol type)
{
    if (type.SpecialType != SpecialType.None) return true;
    ...
}
```

`SpecialType.None` is only set for user-defined types. All C# built-in types including `string`, `object`, `dynamic`, `void`, `decimal`, `DateTime` (no, that's a struct but has `SpecialType.None`) — this is fine for numeric primitives but `string` (`SpecialType.System_String`) would be caught here and treated as a primitive (direct copy), except it has a guard before this check in `GetShadowTypeName`. The danger is in `GenerateValueDirtyCheck`: `IsPrimitive` is called there too, and if a `string` somehow slips through the string check, it would generate `if (shadow.X != current.X) return true;` which is reference equality for strings — not content equality. The code flow appears safe today, but the guard ordering is fragile.

---

### 12. `ShadowStructsGenerator` — string dictionary fallback is O(n²) for dirty checking

**File:** [`ShadowStructsGenerator.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/src/MongoZen/SourceGenerator/ShadowStructsGenerator.cs#L438-L456)  
**Lines:** 438–456

When the dictionary key is `string`, the generator falls back to `ArenaList<KeyValuePairShadow<ArenaString, ...>>` and performs a **linear scan** per key during dirty checking:

```csharp
for (int j = 0; j < shadow.Length; j++)
{
    var shadowPair = shadow[j];
    if (shadowPair.Key.Equals(kvp.Key))
    {
        // check value...
        break;
    }
}
```

This is O(n²) for a dictionary with n entries. For entities with `Dictionary<string, ...>` properties of non-trivial size, this will be a performance cliff. Since this is the generated path for string-keyed dictionaries (the common case), this deserves dedicated attention.

---

### 13. `DbContextSessionsGenerator` — missing `IMutableDbSet`-typed property discovery

**File:** [`DbContextSessionsGenerator.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/src/MongoZen/SourceGenerator/DbContextSessionsGenerator.cs#L88-L96)  
**Lines:** 88–96

```csharp
if (member.Type is INamedTypeSymbol { IsGenericType: true } namedType &&
    namedType.Name == "IDbSet")
```

The session generator only picks up `IDbSet<T>` properties, not `IMutableDbSet<T>`. This is technically correct since `DbContext` should only have `IDbSet<T>` properties, but the `ShadowStructsGenerator.cs` (line 39) checks for both `IDbSet` **and** `IMutableDbSet`. The two generators are inconsistent — if a user accidentally puts an `IMutableDbSet<T>` on their `DbContext`, the shadow struct will be generated but the session property won't.

---

### 14. `BugFixTests` — `TestSession.SaveChangesAsync` bypasses the base class pattern

**File:** [`BugFixTests.cs`](file:///c:/Users/myarichuk/source/repos/MongoZen/tests/MongoZen.Tests/BugFixTests.cs#L42-L50)  
**Lines:** 42–50

The `TestSession` in tests manually calls `CommitAsync` then `CommitTransactionAsync` then `ClearTracking`. This pattern is different from `DbContextSession.SaveChangesAsync`, which calls `CommitAsync`, then conditionally commits the real transaction and calls `AcceptChanges` (which calls both `RefreshShadows` and `ClearTracking`). The test session **never calls `RefreshShadows`**, so shadow state is never updated between saves in the test. This means any test relying on dirty-tracking across multiple saves in `TestSession` may give false results. This is a test fidelity issue, not a production bug.

---

## 🟢 Missing Test Coverage

| Scenario | Gap |
|---|---|
| Arena growth / memory under long-lived sessions | No test verifies arena size stays bounded after many `SaveChangesAsync` calls with dirty entities |
| `TransactionsSupported()` result is cached — if topology changes (replica set election) the cached `false` is never invalidated | No test; the `ConditionalWeakTable` will also keep the `MongoClient` alive longer than expected |
| `RefreshShadows` after `ClearTracking` (use-after-free scenario from Bug #1) | Test exists (`ClearTracking_ArenaReset_NoDanglingPointers`) but only when `materializer` returns `(IntPtr)1` — not a real arena pointer, so can't prove memory safety |
| `Dictionary<string, T>` dirty-checking with reordered entries (O(n²) path) | `Dictionary_OrderIndependence` test exists but does not verify **performance** — an n=1000 case would expose the O(n²) cliff |
| `InMemoryDbSet.CommitAsync` semantics divergence from `DbSet.CommitAsync` (Bug #6) | No test covering the remove-then-add-then-dirty-track sequence in the InMemory path |

---

## Summary Priority Order

| Priority | Issue |
|---|---|
| 🔴 Critical | #1 Arena freed before identity map cleared (use-after-free) |
| 🔴 High | #3 CancellationTokenSource leak in `TransactionsSupported()` |
| 🔴 High | #4 `DocId.WriteHash128` unsafe aliasing across struct fields |
| 🟡 Medium | #6 `InMemoryDbSet.CommitAsync` ignores deduplication buffers |
| 🟡 Medium | #10 `EntityIdAccessor` cache miss on reference-equality convention keys |
| 🟡 Medium | #12 O(n²) dirty check for `Dictionary<string, T>` |
| 🟡 Medium | #9 Arena grows unbounded across `RefreshShadows` calls in long sessions |
| 🟢 Low | #2 Broken session state after `CommitTransactionAsync` exception |
| 🟢 Low | #5 `ArenaDictionary.Grow()` accumulates dead arena memory |
| 🟢 Low | #7 Confusing error path for include with non-`DbSet` implementation |
| 🟢 Low | #8 Reflection in `GetCollectionName` — source-gen opportunity |
| 🟢 Low | #11 Fragile `IsPrimitive` guard ordering |
| 🟢 Low | #13 Generator inconsistency (`IMutableDbSet` properties ignored in session gen) |
| 🟢 Low | #14 Test fidelity: `TestSession` skips `RefreshShadows` |
