# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]
- Initial work on MongoZen.
- `SaveChangesAsync` now writes through the driver's `BulkWriteAsync` (`InsertOneModel`/`UpdateOneModel`/`DeleteOneModel`) instead of hand-built `RunCommand` documents, so writes get the driver's retryable-writes handling across elections/stepdowns. Zero-copy insert/update payloads are preserved via `RawBsonDocument`.
  - Known limitation: an ordered bulk write outside a transaction can partially commit before failing; the change tracker isn't reconciled with what actually persisted, so a naive retry can hit duplicate-key errors on already-inserted docs. Documented in code where the bulk write is built; not yet fixed.
- Transaction-support detection now uses a real topology probe (`hello`/`isMaster`, checking for `setName`/`isdbgrid`) cached on `DocumentStore`, replacing brittle detection based on matching exception message text.
- `DocumentStore.Dispose()` now disposes the underlying `MongoClient` only when the store created it itself; a client passed in by the caller is left alone.
- Disposed-session guards are now consistent across `DocumentSession`'s public API (`Store`, `StoreAsync`, `Delete`, `LoadAsync`, `QueryAsync`, `AggregateAsync`, `CountAsync`, `AnyAsync`, `StreamAsync`, and the `Advanced`/`Attachments` operations) instead of only `SaveChangesAsync`.
- Added `IDocumentSession.StoreAsync<T>`, an async counterpart to `Store<T>` that awaits Hi/Lo ID refill instead of blocking the calling thread when the current range is exhausted.
- `StreamAsync`'s scratch arena is now reset after each cursor batch instead of growing unbounded for the life of the stream.
- Added Testcontainers-backed integration tests (opt-in, skipped when Docker isn't reachable) covering real transaction commit/abort, concurrency-conflict detection, and index conflict/recreate — paths the Mongo.Fakes-based suite can't exercise correctly since it isn't a replica set.
- `DynamicBlittableSerializer`'s collection/dictionary field detection is now structural (matches `IList<T>`/`IReadOnlyList<T>`/`ICollection<T>`/`IEnumerable<T>` and `IDictionary<K,V>`/`IReadOnlyDictionary<K,V>` by interface) instead of matching a fixed list of concrete generic types. Non-`List<T>`/`Dictionary<K,V>` collection-shaped types (e.g. Google.Protobuf's `RepeatedField<T>`/`MapField<K,V>`) are now recognized as collections/dictionaries on write instead of silently serializing as an empty-looking nested document.
  - Known limitation: read-side reconstruction still only special-cases arrays and `List<T>` explicitly; other concrete collection types fall back to converting a `T[]` to the target type, which throws for types with no array-compatible cast (e.g. `RepeatedField<T>`) instead of silently losing data as before — louder, but not yet a full round-trip fix.
