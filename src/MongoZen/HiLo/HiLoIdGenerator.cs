using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoZen.HiLo;

/// <summary>
/// Generates sequential string IDs using the Hi/Lo algorithm.
/// IDs are produced in the form "tag/N" where tag is typically a collection name.
/// </summary>
public sealed class HiLoIdGenerator
{
    private readonly IMongoDatabase _database;
    private readonly DocumentConventions _conventions;
    private readonly ConcurrentDictionary<string, HiLoRangeHolder> _ranges = new();

    public HiLoIdGenerator(IMongoDatabase database, DocumentConventions conventions)
    {
        _database = database;
        _conventions = conventions;
    }

    /// <summary>
    /// Generates the next ID for the given tag synchronously from the local cache.
    /// Only blocks on a DB call if the current range is exhausted.
    /// </summary>
    public string GenerateNextId(string tag)
    {
        var holder = _ranges.GetOrAdd(tag, _ => new HiLoRangeHolder());
        return holder.GenerateNextId(tag, _database, _conventions);
    }

    /// <summary>
    /// Generates the next ID for the given tag asynchronously.
    /// </summary>
    public ValueTask<string> GenerateNextIdAsync(string tag, CancellationToken ct = default)
    {
        var holder = _ranges.GetOrAdd(tag, _ => new HiLoRangeHolder());
        return holder.GenerateNextIdAsync(tag, _database, _conventions, ct);
    }

    /// <summary>
    /// Internal holder for a tag's Hi/Lo range and current position.
    /// A per-tag <see cref="SemaphoreSlim"/> is the sole coordination point for refills: on
    /// exhaustion, callers wait on it (blocking for the sync path, asynchronously for the
    /// async path) instead of busy-spinning, and re-check the range once inside in case
    /// another caller already refilled it.
    /// </summary>
    private sealed class HiLoRangeHolder
    {
        private long _high;
        private long _current;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public string GenerateNextId(string tag, IMongoDatabase database, DocumentConventions conventions)
        {
            while (true)
            {
                long nextValue = Interlocked.Increment(ref _current);
                if (nextValue <= Interlocked.Read(ref _high))
                {
                    return $"{tag}/{nextValue}";
                }

                _gate.Wait();
                try
                {
                    if (Interlocked.Read(ref _current) > _high)
                    {
                        RefillRange(tag, database, conventions);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }

        public async ValueTask<string> GenerateNextIdAsync(string tag, IMongoDatabase database, DocumentConventions conventions, CancellationToken ct)
        {
            while (true)
            {
                long nextValue = Interlocked.Increment(ref _current);
                if (nextValue <= Interlocked.Read(ref _high))
                {
                    return $"{tag}/{nextValue}";
                }

                await _gate.WaitAsync(ct);
                try
                {
                    if (Interlocked.Read(ref _current) > _high)
                    {
                        await RefillRangeAsync(tag, database, conventions, ct);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }

        private void RefillRange(string tag, IMongoDatabase database, DocumentConventions conventions)
        {
            var collection = database.GetCollection<BsonDocument>(conventions.HiLoCollectionName);
            var update = Builders<BsonDocument>.Update.Inc("Max", (long)conventions.HiLoCapacity);
            var options = new FindOneAndUpdateOptions<BsonDocument, BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            };

            var filter = Builders<BsonDocument>.Filter.Eq("_id", tag);
            var result = collection.FindOneAndUpdate(filter, update, options);

            ApplyRefillResult(result, conventions);
        }

        private async Task RefillRangeAsync(string tag, IMongoDatabase database, DocumentConventions conventions, CancellationToken ct)
        {
            var collection = database.GetCollection<BsonDocument>(conventions.HiLoCollectionName);
            var update = Builders<BsonDocument>.Update.Inc("Max", (long)conventions.HiLoCapacity);
            var options = new FindOneAndUpdateOptions<BsonDocument, BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            };

            var filter = Builders<BsonDocument>.Filter.Eq("_id", tag);
            var result = await collection.FindOneAndUpdateAsync(filter, update, options, ct);

            ApplyRefillResult(result, conventions);
        }

        private void ApplyRefillResult(BsonDocument? result, DocumentConventions conventions)
        {
            if (result != null && result.TryGetValue("Max", out var maxValue) && maxValue.IsNumeric)
            {
                // $inc on a field that doesn't exist yet creates it with the operand's own BSON
                // numeric type (Int32 here, since HiLoCapacity is an int), not Int64 — ToInt64()
                // converts regardless of which numeric type the server actually stored.
                long max = maxValue.ToInt64();
                // Range is (low, high] so a fresh range of size HiLoCapacity yields exactly
                // HiLoCapacity ids: low+1 .. high.
                long low = max - conventions.HiLoCapacity;
                _high = max;
                Interlocked.Exchange(ref _current, low);
            }
        }
    }
}
