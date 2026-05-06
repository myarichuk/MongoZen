using System.Collections.Concurrent;
using System.Reflection;
using MongoDB.Bson;

namespace MongoZen;

/// <summary>
/// Conventions for a specific DocumentStore instance.
/// </summary>
public sealed class DocumentConventions
{
    private readonly ConcurrentDictionary<Type, string> _cachedCollectionNames = new();

    /// <summary>
    /// Delegate to find the collection name for a given type.
    /// The default implementation pluralizes the type name.
    /// </summary>
    public Func<Type, string> FindCollectionName { get; set; }

    /// <summary>
    /// Gets or sets the Guid representation to use.
    /// NOTE: Changing this will apply globally to the MongoDB Driver's ConventionRegistry
    /// when the DocumentStore is initialized.
    /// </summary>
    public GuidRepresentation GuidRepresentation { get; set; } = GuidRepresentation.Standard;

    public DocumentConventions()
    {
        FindCollectionName = type => _cachedCollectionNames.GetOrAdd(type, t => Inflector.Pluralize(t.Name));
    }

    /// <summary>
    /// Returns the collection name for a document type, respecting [Document] attribute.
    /// </summary>
    public string GetCollectionName(Type type)
    {
        var docAttr = type.GetCustomAttribute<DocumentAttribute>();
        if (docAttr?.CollectionName != null)
        {
            return docAttr.CollectionName;
        }

        return FindCollectionName(type);
    }

    /// <summary>
    /// Creates a BsonValue from a .NET object, respecting store-specific conventions (like GuidRepresentation).
    /// </summary>
    public BsonValue CreateBsonValue(object? value)
    {
        return value switch
        {
            null => BsonNull.Value,
            Guid guid => new BsonBinaryData(guid, GuidRepresentation),
            _ => BsonValue.Create(value)
        };
    }
}

internal static class Inflector
{
    /// <summary>
    /// A simple and pragmatic pluralizer for English words.
    /// Inspired by the RavenDB client's behavior.
    /// </summary>
    public static string Pluralize(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return word;
        }

        if (word.Length <= 1)
        {
            return $"{word}s";
        }

        var last = word[^1];
        var secondLast = word[^2];

        if (last is 'y' or 'Y')
        {
            if (IsVowel(secondLast))
            {
                return $"{word}s";
            }

            return $"{word[..^1]}{(last == 'y' ? "ies" : "IES")}";
        }

        if (last is 's' or 'S' or 'x' or 'X' or 'z' or 'Z')
        {
            return word + (char.IsUpper(last) ? "ES" : "es");
        }

        if (word.EndsWith("ch", StringComparison.OrdinalIgnoreCase) || 
            word.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
        {
            return $"{word}{(char.IsUpper(last) ? "ES" : "es")}";
        }

        if (last is 'f' or 'F')
        {
            return word[..^1] + (last == 'f' ? "ves" : "VES");
        }
        
        if (word.EndsWith("fe", StringComparison.OrdinalIgnoreCase))
        {
            return word[..^2] + (char.IsUpper(last) ? "VES" : "ves");
        }

        return $"{word}s";
    }

    private static bool IsVowel(char c)
    {
        c = char.ToLowerInvariant(c);
        return c is 'a' or 'e' or 'i' or 'o' or 'u';
    }
}