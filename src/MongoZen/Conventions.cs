using System.Collections.Concurrent;

namespace MongoZen;

/// <summary>
/// Global conventions for MongoZen.
/// </summary>
public static class Conventions
{
    private static readonly ConcurrentDictionary<Type, string> CachedCollectionNames = new();

    /// <summary>
    /// Delegate to find the collection name for a given type.
    /// The default implementation pluralizes the type name.
    /// </summary>
    public static Func<Type, string> FindCollectionName { get; set; } = 
        type => CachedCollectionNames.GetOrAdd(type, t => Inflector.Pluralize(t.Name));
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

        // Common irregulars or special cases could go here, but let's keep it pragmatic.
        if (word.Length <= 1)
        {
            return $"{word}s";
        }

        var last = word[^1];
        var secondLast = word[^2];

        // y -> ies (e.g. Company -> Companies), but not if preceded by vowel (e.g. Day -> Days)
        if (last is 'y' or 'Y')
        {
            if (IsVowel(secondLast))
            {
                return $"{word}s";
            }

            return $"{word[..^1]}{(last == 'y' ? "ies" : "IES")}";
        }

        // s, x, z, ch, sh -> es (e.g. Status -> Statuses, Fox -> Foxes)
        if (last is 's' or 'S' or 'x' or 'X' or 'z' or 'Z')
        {
            return word + (char.IsUpper(last) ? "ES" : "es");
        }

        if (word.EndsWith("ch", StringComparison.OrdinalIgnoreCase) || 
            word.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
        {
            return $"{word}{(char.IsUpper(last) ? "ES" : "es")}";
        }

        // f, fe -> ves (e.g. Leaf -> Leaves, Wife -> Wives)
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
