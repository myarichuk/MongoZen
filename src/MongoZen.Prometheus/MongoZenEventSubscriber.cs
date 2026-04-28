using System.Collections.Concurrent;
using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver.Core.Events;

namespace MongoZen.Prometheus;

internal class MongoZenEventSubscriber : IEventSubscriber
{
    private readonly ConcurrentDictionary<int, long> _commandStartTimes = new();
    private readonly ConcurrentDictionary<long, long> _cursorStartTimes = new();
    private readonly ConcurrentDictionary<string, TagList> _tagCache = new();

    public bool TryGetEventHandler<TEvent>(out Action<TEvent> handler)
    {
        if (typeof(TEvent) == typeof(CommandStartedEvent))
        {
            handler = (Action<TEvent>)(object)new Action<CommandStartedEvent>(Handle);
            return true;
        }
        if (typeof(TEvent) == typeof(CommandSucceededEvent))
        {
            handler = (Action<TEvent>)(object)new Action<CommandSucceededEvent>(Handle);
            return true;
        }
        if (typeof(TEvent) == typeof(CommandFailedEvent))
        {
            handler = (Action<TEvent>)(object)new Action<CommandFailedEvent>(Handle);
            return true;
        }
        if (typeof(TEvent) == typeof(ConnectionOpenedEvent))
        {
            handler = (Action<TEvent>)(object)new Action<ConnectionOpenedEvent>(Handle);
            return true;
        }
        if (typeof(TEvent) == typeof(ConnectionClosedEvent))
        {
            handler = (Action<TEvent>)(object)new Action<ConnectionClosedEvent>(Handle);
            return true;
        }

        handler = null!;
        return false;
    }

    private TagList GetCommandTags(string commandName, string? dbName = null)
    {
        string key = dbName == null ? commandName : $"{commandName}:{dbName}";
        return _tagCache.GetOrAdd(key, k => 
        {
            var tags = new TagList { { "command_type", commandName } };
            if (dbName != null) tags.Add("target_db", dbName);
            return tags;
        });
    }

    private void Handle(CommandStartedEvent @event)
    {
        _commandStartTimes[@event.RequestId] = Stopwatch.GetTimestamp();

        var tags = GetCommandTags(@event.CommandName, @event.DatabaseNamespace.DatabaseName);

        if (@event.Command.Contains("filter"))
        {
            var filterValue = @event.Command["filter"];
            if (filterValue.IsBsonDocument)
            {
                MongoZenMetrics.QueryFilterSize.Record(CountClauses(filterValue.AsBsonDocument), tags);
            }
        }

        // Approximate request size without allocating the byte array
        MongoZenMetrics.CommandRequestSize.Record(GetBsonSize(@event.Command), tags);

        if (@event.CommandName == "find" || @event.CommandName == "aggregate")
        {
            MongoZenMetrics.IncrementOpenCursors();
            if (@event.OperationId.HasValue)
            {
                _cursorStartTimes[@event.OperationId.Value] = Stopwatch.GetTimestamp();
            }
        }
    }

    private void Handle(CommandSucceededEvent @event)
    {
        if (_commandStartTimes.TryRemove(@event.RequestId, out var startTime))
        {
            var elapsed = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
            var tags = new TagList
            {
                { "command_type", @event.CommandName },
                { "status", "success" }
            };
            MongoZenMetrics.CommandDuration.Record(elapsed, tags);
        }

        if (@event.Reply != null)
        {
            var tags = GetCommandTags(@event.CommandName);
            MongoZenMetrics.CommandResponseSize.Record(GetBsonSize(@event.Reply), tags);
            
            if (@event.CommandName == "getMore" || @event.CommandName == "find")
            {
                var cursor = @event.Reply.GetValue("cursor", null)?.AsBsonDocument;
                if (cursor != null && cursor.Contains("firstBatch"))
                {
                    MongoZenMetrics.CursorDocumentCount.Record(cursor["firstBatch"].AsBsonArray.Count);
                }
                else if (cursor != null && cursor.Contains("nextBatch"))
                {
                    MongoZenMetrics.CursorDocumentCount.Record(cursor["nextBatch"].AsBsonArray.Count);
                }

                if (cursor != null && cursor.GetValue("id", 0).ToInt64() == 0)
                {
                    // Cursor closed
                    MongoZenMetrics.DecrementOpenCursors();
                    if (@event.OperationId.HasValue && _cursorStartTimes.TryRemove(@event.OperationId.Value, out var cursorStartTime))
                    {
                        MongoZenMetrics.CursorDuration.Record(Stopwatch.GetElapsedTime(cursorStartTime).TotalSeconds);
                    }
                }
            }
        }
    }

    private void Handle(CommandFailedEvent @event)
    {
        _commandStartTimes.TryRemove(@event.RequestId, out _);
        
        var tags = new TagList
        {
            { "command_type", @event.CommandName },
            { "error_type", @event.Failure.GetType().Name }
        };
        MongoZenMetrics.CommandErrors.Add(1, tags);

        if (@event.CommandName == "find" || @event.CommandName == "aggregate" || @event.CommandName == "getMore")
        {
            MongoZenMetrics.DecrementOpenCursors();
            if (@event.OperationId.HasValue)
            {
                _cursorStartTimes.TryRemove(@event.OperationId.Value, out _);
            }
        }
    }

    private void Handle(ConnectionOpenedEvent @event)
    {
        MongoZenMetrics.ConnectionCreationRate.Add(1, new TagList { { "cluster_id", @event.ClusterId.ToString() } });
    }

    private void Handle(ConnectionClosedEvent @event)
    {
        // Connection closed
    }

    private static int GetBsonSize(BsonDocument doc)
    {
        if (doc is RawBsonDocument raw)
        {
            return raw.Slice.Length;
        }

        // Calculate size without full ToBson() allocation if possible
        // Actually, MongoDB Driver's BsonDocument doesn't expose a 'CalculateSize' easily without serializing.
        // But we can serialize to a null stream or a reusable buffer.
        // For simplicity and to avoid complex buffer management for now, we'll use a fast check.
        // In Driver 2.x/3.x, ToBson() is the standard way. 
        // We'll use a Reusable thin wrapper if we wanted to be extreme.
        return doc.ToBson().Length; 
    }

    private static int CountClauses(BsonDocument doc)
    {
        int count = doc.ElementCount;
        foreach (var element in doc.Elements)
        {
            if (element.Value.IsBsonDocument)
            {
                count += CountClauses(element.Value.AsBsonDocument);
            }
            else if (element.Value.IsBsonArray)
            {
                foreach (var item in element.Value.AsBsonArray)
                {
                    if (item.IsBsonDocument)
                    {
                        count += CountClauses(item.AsBsonDocument);
                    }
                }
            }
        }
        return count;
    }
}
