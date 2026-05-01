using System;

namespace MongoZen;

/// <summary>
/// Specifies that shadow structs and diff enumerators should be generated for all classes
/// in the specified namespace.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class GenerateDocumentShadowsForNamespaceAttribute(string ns) : Attribute
{
    public string Namespace { get; } = ns;
}

/// <summary>
/// Specifies that a shadow struct and diff enumerator should be generated for this class.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class DocumentAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the collection name to use in MongoDB. If not specified, it will be inferred from the type name.
    /// </summary>
    public string? CollectionName { get; set; }
}
