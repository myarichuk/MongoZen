# Optimistic Concurrency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement "Hidden by Default, Exposed by Choice" optimistic concurrency control using Guid ETags, including a side-channel `Advanced` API and precise failure detection.

**Architecture:** MongoZen will inject and track a `_etag` field in BSON. Collision detection occurs during `BulkWriteAsync` via the update filter. Recovery is handled via `Evict` and `RefreshAsync` in a new `session.Advanced` surface.

**Tech Stack:** C#, MongoDB .NET Driver, SharpArena

---

### Task 1: Infrastructure & Metadata
**Files:**
- Modify: `src/MongoZen/Attributes.cs`
- Create: `src/MongoZen/ConcurrencyException.cs`

- [x] **Step 1: Add ConcurrencyCheckAttribute**
```csharp
namespace MongoZen;
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConcurrencyCheckAttribute : Attribute { }
```
- [x] **Step 2: Create ConcurrencyException**
```csharp
namespace MongoZen;
public sealed class ConcurrencyException : Exception
{
    public object? Entity { get; }
    public ConcurrencyException(string message, object? entity = null, Exception? inner = null) 
        : base(message, inner) => Entity = entity;
}
```
- [x] **Step 3: Commit**

---

### Task 3: ChangeTracker & Update Logic
**Files:**
- Modify: `src/MongoZen/ChangeTracking/ChangeTracker.cs`

- [x] **Step 1: Add ETag to EntityEntry**
Update `EntityEntry` internal class to have a `Guid? ExpectedETag` property.
- [x] **Step 2: Update UpdateOperation to use ETag filter**
```csharp
public sealed class UpdateOperation(object id, Guid expectedEtag, BlittableBsonDocument update, string collectionName) : IPendingUpdate
{
    public WriteModel<BsonDocument> ToWriteModel()
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Filter.Eq("_etag", expectedEtag)
        );
        var updateDoc = new RawBsonDocument(update.AsReadOnlySpan().ToArray());
        return new UpdateOneModel<BsonDocument>(filter, updateDoc);
    }
}
```
- [x] **Step 3: Update InsertOperation to inject initial ETag**
Ensure the `_etag` is generated and injected into the BSON during the `InsertOperation`.
- [x] **Step 4: Commit**

---

### Task 4: Serializer Integration
**Files:**
- Modify: `src/MongoZen/Bson/DynamicBlittableSerializer.cs`

- [x] **Step 1: Add logic to detect [ConcurrencyCheck] property in `PrepareSerializer`**
- [x] **Step 2: Update Deserialization to read `_etag` into the marked property**
- [x] **Step 3: Update Serialization to ensure `_etag` is NOT overwritten by the POCO value (database wins)**
- [x] **Step 4: Commit**

---

### Task 5: Advanced API & Recovery
**Files:**
- Modify: `src/MongoZen/DocumentSession.cs`

- [x] **Step 1: Create ISessionAdvancedOperations and implement in DocumentSession**
- [x] **Step 2: Implement `Evict(entity)`**
Should remove from `_identityMap` and `_changeTracker`.
- [x] **Step 3: Implement `RefreshAsync(entity)`**
Should reload from DB, update POCO, and reset the `ChangeTracker` snapshot/ETag.
- [x] **Step 4: Commit**

---

### Task 6: SaveChangesAsync OCC Logic
**Files:**
- Modify: `src/MongoZen/DocumentSession.cs`

- [ ] **Step 1: Detect MatchedCount mismatch after BulkWriteAsync**
- [ ] **Step 2: Implement `IdentifyConcurrencyConflictAsync`**
Private helper that queries the DB for current ETags of the batch IDs to find the mismatching entity.
- [ ] **Step 3: Throw precise ConcurrencyException**
- [ ] **Step 4: Commit**

---

### Task 7: Final Validation
**Files:**
- Create: `tests/MongoZen.Tests/ConcurrencyTests.cs`

- [ ] **Step 1: Write Integrated Test for cross-session collision**
- [ ] **Step 2: Write test for RefreshAsync recovery path**
- [ ] **Step 3: Verify all existing tests pass**
- [ ] **Step 4: Commit**
