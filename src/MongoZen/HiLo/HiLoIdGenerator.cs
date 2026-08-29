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
    /// </summary>
    private sealed class HiLoRangeHolder
    {
        private long _low;
        private long _high;
        private long _current;
        private bool _refilling;
        private readonly object _lock = new();

        public HiLoRangeHolder()
        {
            _low = 0;
            _high = 0;
            _current = 0;
            _refilling = false;
        }

        public string GenerateNextId(string tag, IMongoDatabase database, DocumentConventions conventions)
        {
            while (true)
            {
                long nextValue = Interlocked.Increment(ref _current);
                if (nextValue < _high)
                {
                    return $"{tag}/{nextValue}";
                }

                bool shouldRefill = false;
                lock (_lock)
                {
                    nextValue = Interlocked.Read(ref _current);
                    if (nextValue >= _high && !_refilling)
                    {
                        _refilling = true;
                        shouldRefill = true;
                    }
                }

                if (shouldRefill)
                {
                    RefillRange(tag, database, conventions);
                    _refilling = false;
                }
            }
        }

        public async ValueTask<string> GenerateNextIdAsync(string tag, IMongoDatabase database, DocumentConventions conventions, CancellationToken ct)
        {
            while (true)
            {
                long nextValue = Interlocked.Increment(ref _current);
                if (nextValue < _high)
                {
                    return $"{tag}/{nextValue}";
                }

                bool shouldRefill = false;
                lock (_lock)
                {
                    nextValue = Interlocked.Read(ref _current);
                    if (nextValue >= _high && !_refilling)
                    {
                        _refilling = true;
                        shouldRefill = true;
                    }
                }

                if (shouldRefill)
                {
                    await RefillRangeAsync(tag, database, conventions, ct);
                    _refilling = false;
                }
            }
        }

        private void RefillRange(string tag, IMongoDatabase database, DocumentConventions conventions)
        {
            var collection = database.GetCollection<BsonDocument>(conventions.HiLoCollectionName);
            var update = Builders<BsonDocument>.Update.Inc("Max", conventions.HiLoCapacity);
            var options = new FindOneAndUpdateOptions<BsonDocument, BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            };

            var filter = Builders<BsonDocument>.Filter.Eq("_id", tag);
            var result = collection.FindOneAndUpdate(filter, update, options);

            if (result != null && result.TryGetValue("Max", out var maxValue) && maxValue.IsInt64)
            {
                long max = maxValue.AsInt64;
                _low = max - conventions.HiLoCapacity;
                _high = max;
                Interlocked.Exchange(ref _current, _low);
            }
        }

        private async Task RefillRangeAsync(string tag, IMongoDatabase database, DocumentConventions conventions, CancellationToken ct)
        {
            var collection = database.GetCollection<BsonDocument>(conventions.HiLoCollectionName);
            var update = Builders<BsonDocument>.Update.Inc("Max", conventions.HiLoCapacity);
            var options = new FindOneAndUpdateOptions<BsonDocument, BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            };

            var filter = Builders<BsonDocument>.Filter.Eq("_id", tag);
            var result = await collection.FindOneAndUpdateAsync(filter, update, options, ct);

            if (result != null && result.TryGetValue("Max", out var maxValue) && maxValue.IsInt64)
            {
                long max = maxValue.AsInt64;
                _low = max - conventions.HiLoCapacity;
                _high = max;
                Interlocked.Exchange(ref _current, _low);
            }
        }
    }
}
