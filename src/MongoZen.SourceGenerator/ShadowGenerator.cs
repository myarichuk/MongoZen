using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace MongoZen.SourceGenerator;

/// <summary>
/// Generates <c>IBlittableDocument&lt;T&gt;</c> implementations (Tier 1) for <c>[Document]</c> classes.
/// The type model mirrors the runtime's <c>BsonTypeModel</c> one for one: scalars, enums, Nullable of
/// those, string, byte[], collections, string-keyed dictionaries and user documents. Anything else is an
/// MZ001 error at compile time rather than a silently dropped or mangled field at runtime, and
/// null-handling / missing-from-snapshot semantics are identical to the reflection tier.
/// </summary>
[Generator]
public class ShadowGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor UnsupportedTypeDiagnostic = new(
        id: "MZ001",
        title: "Unsupported type in Document",
        messageFormat: "Type '{0}' is not supported for change tracking. Supported: int/long/short/ushort/byte/sbyte/char/uint/ulong, double/float, bool, string, ObjectId, DateTime, Guid, decimal, enums, Nullable<T> of those, byte[], arrays/IEnumerable<T> of a supported type, IDictionary<string, V> of a supported V, and your own classes/structs.",
        category: "MongoZen",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var annotatedClasses = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "MongoZen.DocumentAttribute",
                predicate: static (s, _) => s is TypeDeclarationSyntax,
                transform: static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol);

        var compilationAndClasses = context.CompilationProvider.Combine(annotatedClasses.Collect());

        context.RegisterSourceOutput(compilationAndClasses, (spc, source) => Execute(source.Left, source.Right!, spc));
    }

    private void Execute(Compilation compilation, ImmutableArray<INamedTypeSymbol> roots, SourceProductionContext context)
    {
        var typesToGenerate = new Dictionary<ITypeSymbol, TypeInfo>(SymbolEqualityComparer.Default);
        var queue = new Queue<INamedTypeSymbol>();

        foreach (var root in roots)
        {
            queue.Enqueue(root);
        }

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (typesToGenerate.ContainsKey(type))
            {
                continue;
            }

            var info = new TypeInfo(type);
            if (AnalyzeType(info, queue, context))
            {
                typesToGenerate[type] = info;
            }
        }

        foreach (var info in typesToGenerate.Values)
        {
            GenerateBlittableImplementation(info, context);
        }
    }

    private bool AnalyzeType(TypeInfo info, Queue<INamedTypeSymbol> queue, SourceProductionContext context)
    {
        var members = info.Symbol.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public && !p.IsReadOnly && !p.IsIndexer);

        foreach (var prop in members)
        {
            if (HasAttribute(prop, "BsonIgnoreAttribute"))
            {
                continue;
            }

            var shape = Categorize(prop.Type);
            if (!IsFullySupported(shape))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedTypeDiagnostic, prop.Locations.FirstOrDefault(), prop.Type.ToDisplayString()));
                return false;
            }

            var elementName = prop.Name;
            var bsonElementAttr = prop.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "BsonElementAttribute");
            if (bsonElementAttr != null && bsonElementAttr.ConstructorArguments.Length > 0)
            {
                elementName = bsonElementAttr.ConstructorArguments[0].Value?.ToString() ?? prop.Name;
            }

            // Case-insensitive, like DefaultIdConvention at runtime: `public int ID` is the id too.
            if (HasAttribute(prop, "BsonIdAttribute") || string.Equals(prop.Name, "Id", StringComparison.OrdinalIgnoreCase))
            {
                elementName = "_id";
            }

            if (HasAttribute(prop, "ConcurrencyCheckAttribute"))
            {
                elementName = "_etag";
            }

            EnqueueDocuments(shape, queue);
            info.Properties.Add(new PropertyInfo(prop, elementName, shape));
        }

        return true;
    }

    private static bool HasAttribute(ISymbol symbol, string attributeName) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.Name == attributeName);

    private static void EnqueueDocuments(Shape shape, Queue<INamedTypeSymbol> queue)
    {
        if (shape.Category == Category.Document && shape.Inner is INamedTypeSymbol named)
        {
            queue.Enqueue(named);
        }

        if (shape.InnerShape != null)
        {
            EnqueueDocuments(shape.InnerShape, queue);
        }
    }

    private static bool IsFullySupported(Shape shape) =>
        shape.Category != Category.Unsupported && (shape.InnerShape == null || IsFullySupported(shape.InnerShape));

    // ------------------------------------------------------------------ type model

    private enum Category
    {
        Scalar,
        Enum,
        String,
        Nullable,
        ByteArray,
        Collection,
        Dictionary,
        Document,
        /// <summary>Supported by the runtime codec but with no dedicated generated code (e.g. Nullable of a struct document); delegated to ElementCodec&lt;T&gt;.</summary>
        Codec,
        Unsupported,
    }

    /// <summary>Scalar mapping: writer/reader method plus the casts between the .NET type and the BSON-side type.</summary>
    private sealed class ScalarSpec(string write, string read, string writeCast, string readCast, string bsonSideType)
    {
        public string Write { get; } = write;
        public string Read { get; } = read;
        public string BsonSideType { get; } = bsonSideType;
        public string CastForWrite(string expr) => string.Format(writeCast, expr);
        public string CastForRead(string expr) => string.Format(readCast, expr);
    }

    private sealed class Shape(Category category, ITypeSymbol type)
    {
        public Category Category { get; } = category;
        public ITypeSymbol Type { get; } = type;
        /// <summary>Element type (Collection), value type (Dictionary), underlying type (Nullable/Enum), the type itself (Document).</summary>
        public ITypeSymbol? Inner { get; set; }
        public Shape? InnerShape { get; set; }
        public ScalarSpec? Scalar { get; set; }
    }

    private static ScalarSpec? GetScalar(ITypeSymbol type)
    {
        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fullName == "global::System.Guid") return new("WriteGuid", "GetGuid", "{0}", "{0}", "System.Guid");
        if (type.ContainingNamespace?.ToDisplayString() == "MongoDB.Bson" && type.Name == "ObjectId") return new("WriteObjectId", "GetObjectId", "{0}", "{0}", "MongoDB.Bson.ObjectId");

        return type.SpecialType switch
        {
            SpecialType.System_Int32 => new("WriteInt32", "GetInt32", "{0}", "{0}", "int"),
            SpecialType.System_Int16 => new("WriteInt32", "GetInt32", "(int){0}", "(short){0}", "int"),
            SpecialType.System_UInt16 => new("WriteInt32", "GetInt32", "(int){0}", "(ushort){0}", "int"),
            SpecialType.System_Byte => new("WriteInt32", "GetInt32", "(int){0}", "(byte){0}", "int"),
            SpecialType.System_SByte => new("WriteInt32", "GetInt32", "(int){0}", "(sbyte){0}", "int"),
            SpecialType.System_Char => new("WriteInt32", "GetInt32", "(int){0}", "(char){0}", "int"),
            SpecialType.System_Int64 => new("WriteInt64", "GetInt64", "{0}", "{0}", "long"),
            SpecialType.System_UInt32 => new("WriteInt64", "GetInt64", "(long){0}", "(uint){0}", "long"),
            SpecialType.System_UInt64 => new("WriteInt64", "GetInt64", "unchecked((long){0})", "unchecked((ulong){0})", "long"),
            SpecialType.System_Double => new("WriteDouble", "GetDouble", "{0}", "{0}", "double"),
            SpecialType.System_Single => new("WriteDouble", "GetDouble", "(double){0}", "(float){0}", "double"),
            SpecialType.System_Boolean => new("WriteBoolean", "GetBoolean", "{0}", "{0}", "bool"),
            SpecialType.System_DateTime => new("WriteDateTime", "GetDateTime", "{0}", "{0}", "System.DateTime"),
            SpecialType.System_Decimal => new("WriteDecimal128", "GetDecimal128", "{0}", "{0}", "decimal"),
            _ => null,
        };
    }

    private static Shape Categorize(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
        {
            return new Shape(Category.String, type);
        }

        if (type is IArrayTypeSymbol array)
        {
            if (array.Rank != 1)
            {
                return new Shape(Category.Unsupported, type);
            }

            if (array.ElementType.SpecialType == SpecialType.System_Byte)
            {
                return new Shape(Category.ByteArray, type);
            }

            return new Shape(Category.Collection, type) { Inner = array.ElementType, InnerShape = Categorize(array.ElementType) };
        }

        if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            var inner = named.TypeArguments[0];
            var innerShape = Categorize(inner);
            return innerShape.Category switch
            {
                Category.Scalar or Category.Enum => new Shape(Category.Nullable, type) { Inner = inner, InnerShape = innerShape, Scalar = innerShape.Scalar },
                Category.Document => new Shape(Category.Codec, type) { Inner = inner, InnerShape = innerShape },
                _ => new Shape(Category.Unsupported, type),
            };
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            var underlying = ((INamedTypeSymbol)type).EnumUnderlyingType!;
            var spec = GetScalar(underlying);
            return spec == null
                ? new Shape(Category.Unsupported, type)
                : new Shape(Category.Enum, type) { Inner = underlying, Scalar = spec };
        }

        var scalar = GetScalar(type);
        if (scalar != null)
        {
            return new Shape(Category.Scalar, type) { Scalar = scalar };
        }

        if (type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Pointer || type.TypeKind == TypeKind.FunctionPointer)
        {
            return new Shape(Category.Unsupported, type); // object, IntPtr, Enum, ValueType, Array, Delegate, ...
        }

        if (TryGetDictionaryTypes(type, out var keyType, out var valueType))
        {
            if (keyType!.SpecialType != SpecialType.System_String)
            {
                return new Shape(Category.Unsupported, type);
            }

            return new Shape(Category.Dictionary, type) { Inner = valueType, InnerShape = Categorize(valueType!) };
        }

        if (TryGetCollectionElementType(type, out var elementType))
        {
            return new Shape(Category.Collection, type) { Inner = elementType, InnerShape = Categorize(elementType!) };
        }

        if (type.TypeKind is TypeKind.Class or TypeKind.Struct)
        {
            var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
            if (ns == "System" || ns.StartsWith("System.") || ns == "MongoDB" || ns.StartsWith("MongoDB.") || ns == "Microsoft" || ns.StartsWith("Microsoft."))
            {
                // TimeSpan, DateTimeOffset, DateOnly, Uri, BsonDocument, ...: no representation chosen yet.
                return new Shape(Category.Unsupported, type);
            }

            return new Shape(Category.Document, type) { Inner = type };
        }

        return new Shape(Category.Unsupported, type);
    }

    private static bool TryGetDictionaryTypes(ITypeSymbol type, out ITypeSymbol? keyType, out ITypeSymbol? valueType)
    {
        foreach (var iface in InterfacesIncludingSelf(type))
        {
            if (!iface.IsGenericType)
            {
                continue;
            }

            var def = iface.ConstructedFrom.ToDisplayString();
            if (def == "System.Collections.Generic.IDictionary<TKey, TValue>" || def == "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>")
            {
                keyType = iface.TypeArguments[0];
                valueType = iface.TypeArguments[1];
                return true;
            }
        }

        keyType = null;
        valueType = null;
        return false;
    }

    private static bool TryGetCollectionElementType(ITypeSymbol type, out ITypeSymbol? elementType)
    {
        foreach (var iface in InterfacesIncludingSelf(type))
        {
            if (iface.IsGenericType && iface.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>")
            {
                elementType = iface.TypeArguments[0];
                return true;
            }
        }

        elementType = null;
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> InterfacesIncludingSelf(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Interface } self)
        {
            yield return self;
        }

        foreach (var iface in type.AllInterfaces)
        {
            yield return iface;
        }
    }

    // ------------------------------------------------------------------ emission

    private static string Lit(string s) => SymbolDisplay.FormatLiteral(s, quote: true);

    private static string TypeName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private void GenerateBlittableImplementation(TypeInfo info, SourceProductionContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using MongoZen;");
        sb.AppendLine("using MongoZen.Bson;");
        sb.AppendLine("using MongoZen.ChangeTracking;");
        sb.AppendLine("using SharpArena.Allocators;");
        sb.AppendLine("using MongoDB.Driver;");
        sb.AppendLine("using MongoDB.Bson;");
        sb.AppendLine();
        sb.AppendLine($"namespace {info.Symbol.ContainingNamespace.ToDisplayString()};");
        sb.AppendLine();
        var keyword = info.Symbol.TypeKind == TypeKind.Struct ? "struct" : "class";
        sb.AppendLine($"partial {keyword} {info.Name} : IBlittableDocument<{info.Name}>");
        sb.AppendLine("{");

        // Serialize
        sb.AppendLine($"    public static void Serialize(ref ArenaBsonWriter writer, {info.Name} entity)");
        sb.AppendLine("    {");
        sb.AppendLine("        writer.WriteStartDocument();");
        foreach (var prop in info.Properties)
        {
            GenerateWriteCall(sb, prop, $"entity.{prop.Symbol.Name}");
        }
        sb.AppendLine("        writer.WriteEndDocument();");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Deserialize
        sb.AppendLine($"    public static {info.Name} Deserialize(BlittableBsonDocument doc, ArenaAllocator arena)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var entity = new {info.Name}();");
        foreach (var prop in info.Properties)
        {
            if (prop.Symbol.SetMethod == null) continue;
            GenerateReadCall(sb, prop, "entity." + prop.Symbol.Name, "doc", "arena");
        }
        sb.AppendLine("        return entity;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // DeserializeInto
        sb.AppendLine($"    public static void DeserializeInto(BlittableBsonDocument doc, ArenaAllocator arena, {info.Name} entity)");
        sb.AppendLine("    {");
        foreach (var prop in info.Properties)
        {
            if (prop.Symbol.SetMethod == null) continue;
            GenerateReadCall(sb, prop, "entity." + prop.Symbol.Name, "doc", "arena");
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        // BuildUpdate
        sb.AppendLine($"    public static void BuildUpdate({info.Name} entity, BlittableBsonDocument snapshot, ref ArenaUpdateDefinitionBuilder builder, SharpArena.Allocators.ArenaAllocator arena, ReadOnlySpan<char> pathPrefix)");
        sb.AppendLine("    {");
        foreach (var prop in info.Properties)
        {
            EmitPropertyDiff(sb, prop, "entity", "snapshot", "builder", "pathPrefix");
        }
        sb.AppendLine("    }");

        sb.AppendLine("}");

        context.AddSource($"{info.Name}.Blittable.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private void GenerateWriteCall(StringBuilder sb, PropertyInfo prop, string access)
    {
        var n = Lit(prop.ElementName);
        var shape = prop.Shape;
        switch (shape.Category)
        {
            case Category.Scalar:
                sb.AppendLine($"        writer.{shape.Scalar!.Write}({n}, {shape.Scalar.CastForWrite(access)});");
                break;
            case Category.Enum:
                sb.AppendLine($"        writer.{shape.Scalar!.Write}({n}, ({shape.Scalar.BsonSideType}){access});");
                break;
            case Category.String:
                sb.AppendLine($"        if ({access} != null) writer.WriteString({n}, {access}.AsSpan()); else writer.WriteNull({n});");
                break;
            case Category.Nullable:
            {
                var valueExpr = shape.InnerShape!.Category == Category.Enum
                    ? $"({shape.Scalar!.BsonSideType}){access}.Value"
                    : shape.Scalar!.CastForWrite($"{access}.Value");
                sb.AppendLine($"        if ({access}.HasValue) writer.{shape.Scalar!.Write}({n}, {valueExpr}); else writer.WriteNull({n});");
                break;
            }
            case Category.ByteArray:
                sb.AppendLine($"        if ({access} != null) writer.WriteBinary({n}, {access}); else writer.WriteNull({n});");
                break;
            case Category.Document:
            {
                var write = $"writer.WriteName({n}, BlittableBsonConstants.BsonType.Document); {TypeName(shape.Inner!)}.Serialize(ref writer, {access});";
                if (shape.Type.IsValueType)
                {
                    sb.AppendLine($"        {write}");
                }
                else
                {
                    sb.AppendLine($"        if ({access} != null) {{ {write} }} else writer.WriteNull({n});");
                }
                break;
            }
            case Category.Collection:
            {
                var write = $"CollectionHelper<{TypeName(shape.Inner!)}>.WriteArray(ref writer, {n}.AsSpan(), {access});";
                sb.AppendLine(shape.Type.IsValueType ? $"        {write}" : $"        if ({access} != null) {write} else writer.WriteNull({n});");
                break;
            }
            case Category.Dictionary:
            {
                var write = $"DictionaryHelper<{TypeName(shape.Inner!)}>.WriteDictionary(ref writer, {n}.AsSpan(), {access});";
                sb.AppendLine(shape.Type.IsValueType ? $"        {write}" : $"        if ({access} != null) {write} else writer.WriteNull({n});");
                break;
            }
            case Category.Codec:
                sb.AppendLine($"        ElementCodec<{TypeName(shape.Type)}>.Write(ref writer, {n}.AsSpan(), {access});");
                break;
        }
    }

    private void GenerateReadCall(StringBuilder sb, PropertyInfo prop, string target, string docVar, string arenaVar)
    {
        var n = Lit(prop.ElementName);
        var shape = prop.Shape;
        var off = $"offset_{prop.Symbol.Name}";
        var isNull = $"ArenaBsonReader.GetElementType({docVar}, {off}) == BlittableBsonConstants.BsonType.Null";

        sb.AppendLine($"        if ({docVar}.TryGetElementOffset({n}, out var {off}))");
        sb.AppendLine("        {");

        switch (shape.Category)
        {
            case Category.Scalar:
                sb.AppendLine($"            {target} = {shape.Scalar!.CastForRead($"{docVar}.{shape.Scalar.Read}({off})")};");
                break;
            case Category.Enum:
                sb.AppendLine($"            {target} = ({TypeName(shape.Type)}){docVar}.{shape.Scalar!.Read}({off});");
                break;
            case Category.String:
                sb.AppendLine($"            {target} = {isNull} ? null! : {docVar}.GetString({off});");
                break;
            case Category.Nullable:
            {
                var read = shape.InnerShape!.Category == Category.Enum
                    ? $"({TypeName(shape.Inner!)}){docVar}.{shape.Scalar!.Read}({off})"
                    : shape.Scalar!.CastForRead($"{docVar}.{shape.Scalar.Read}({off})");
                sb.AppendLine($"            {target} = {isNull} ? null : {read};");
                break;
            }
            case Category.ByteArray:
                sb.AppendLine($"            {target} = {isNull} ? null! : {docVar}.GetBinary({off});");
                break;
            case Category.Document:
            {
                var read = $"{TypeName(shape.Inner!)}.Deserialize({docVar}.GetDocument({off}, {arenaVar}), {arenaVar})";
                sb.AppendLine($"            {target} = {isNull} ? {(shape.Type.IsValueType ? "default" : "null!")} : {read};");
                break;
            }
            case Category.Collection:
            {
                var array = $"CollectionHelper<{TypeName(shape.Inner!)}>.ReadArray({docVar}.GetArray({off}, {arenaVar}), {arenaVar})";
                var list = $"CollectionHelper<{TypeName(shape.Inner!)}>.ReadList({docVar}.GetArray({off}, {arenaVar}), {arenaVar})";
                string read;
                if (shape.Type is IArrayTypeSymbol || shape.Type.TypeKind == TypeKind.Interface)
                {
                    read = array;
                }
                else if (shape.Type is INamedTypeSymbol { IsGenericType: true } named && named.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.List<T>")
                {
                    read = list;
                }
                else
                {
                    read = $"new {TypeName(shape.Type)}({array})";
                }

                sb.AppendLine($"            {target} = {isNull} ? {(shape.Type.IsValueType ? "default" : "null!")} : {read};");
                break;
            }
            case Category.Dictionary:
            {
                var dict = $"DictionaryHelper<{TypeName(shape.Inner!)}>.ReadDictionary({docVar}.GetDocument({off}, {arenaVar}), {arenaVar})";
                string read;
                if (shape.Type.TypeKind == TypeKind.Interface
                    || (shape.Type is INamedTypeSymbol { IsGenericType: true } named && named.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.Dictionary<TKey, TValue>"))
                {
                    read = dict;
                }
                else
                {
                    read = $"new {TypeName(shape.Type)}({dict})";
                }

                sb.AppendLine($"            {target} = {isNull} ? {(shape.Type.IsValueType ? "default" : "null!")} : {read};");
                break;
            }
            case Category.Codec:
                sb.AppendLine($"            {target} = {docVar}.Read<{TypeName(shape.Type)}>({off}, {arenaVar});");
                break;
        }

        sb.AppendLine("        }");
    }

    private void EmitPropertyDiff(StringBuilder sb, PropertyInfo prop, string entityVar, string snapVar, string builderVar, string prefixVar)
    {
        var name = prop.ElementName;
        var n = Lit(name);
        var shape = prop.Shape;
        var id = prop.Symbol.Name;
        var access = $"{entityVar}.{id}";
        var off = $"off_{id}";
        var type = $"type_{id}";
        var path = $"path_{id}";
        var isNull = $"{type} == BlittableBsonConstants.BsonType.Null";

        sb.AppendLine($"        Span<char> fullPath_{id} = stackalloc char[{prefixVar}.Length + {name.Length} + 1];");
        sb.AppendLine($"        int len_{id} = 0;");
        sb.AppendLine($"        if ({prefixVar}.Length > 0) {{ {prefixVar}.CopyTo(fullPath_{id}); fullPath_{id}[{prefixVar}.Length] = '.'; len_{id} = {prefixVar}.Length + 1; }}");
        sb.AppendLine($"        {n}.AsSpan().CopyTo(fullPath_{id}.Slice(len_{id}));");
        sb.AppendLine($"        var {path} = fullPath_{id}.Slice(0, len_{id} + {name.Length});");
        sb.AppendLine($"        if ({snapVar}.TryGetElementOffset({n}, out var {off}))");
        sb.AppendLine("        {");
        sb.AppendLine($"            var {type} = ArenaBsonReader.GetElementType({snapVar}, {off});");

        switch (shape.Category)
        {
            case Category.Scalar:
            {
                var spec = shape.Scalar!;
                var compare = spec.Read == "GetDateTime"
                    ? $"!BsonDateTimeUtil.Equal({access}, {snapVar}.GetDateTime({off}))"
                    : $"{spec.CastForWrite(access)} != {snapVar}.{spec.Read}({off})";
                sb.AppendLine($"            if ({isNull} || {compare})");
                sb.AppendLine($"                {builderVar}.Set({path}, {spec.CastForWrite(access)});");
                break;
            }
            case Category.Enum:
            {
                var spec = shape.Scalar!;
                sb.AppendLine($"            if ({isNull} || ({spec.BsonSideType}){access} != {snapVar}.{spec.Read}({off}))");
                sb.AppendLine($"                {builderVar}.Set({path}, ({spec.BsonSideType}){access});");
                break;
            }
            case Category.String:
                sb.AppendLine($"            if (!object.Equals({isNull} ? null : {snapVar}.GetString({off}), {access}))");
                sb.AppendLine($"                {builderVar}.Set({path}, {access});");
                break;
            case Category.Nullable:
            case Category.ByteArray:
            case Category.Codec:
                sb.AppendLine($"            if (!ElementCodec<{TypeName(shape.Type)}>.EqualsAt({access}, {snapVar}, {off}, arena))");
                sb.AppendLine($"                {builderVar}.SetValue({path}, {access});");
                break;
            case Category.Document:
            {
                if (!shape.Type.IsValueType)
                {
                    sb.AppendLine($"            if ({access} == null)");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                if (!({isNull})) {builderVar}.SetNull({path});");
                    sb.AppendLine("            }");
                    sb.AppendLine($"            else if ({type} != BlittableBsonConstants.BsonType.Document)");
                }
                else
                {
                    sb.AppendLine($"            if ({type} != BlittableBsonConstants.BsonType.Document)");
                }

                sb.AppendLine($"                {builderVar}.SetValue({path}, {access});");
                sb.AppendLine("            else");
                sb.AppendLine("            {");
                sb.AppendLine($"                var nestedSnap_{id} = {snapVar}.GetDocument({off}, arena);");
                sb.AppendLine($"                {TypeName(shape.Inner!)}.BuildUpdate({access}, nestedSnap_{id}, ref {builderVar}, arena, {path});");
                sb.AppendLine("            }");
                break;
            }
            case Category.Collection:
            case Category.Dictionary:
            {
                var helper = shape.Category == Category.Collection
                    ? $"CollectionHelper<{TypeName(shape.Inner!)}>"
                    : $"DictionaryHelper<{TypeName(shape.Inner!)}>";
                if (!shape.Type.IsValueType)
                {
                    sb.AppendLine($"            if ({access} == null)");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                if (!({isNull})) {builderVar}.SetNull({path});");
                    sb.AppendLine("            }");
                    sb.AppendLine($"            else if (!{helper}.EqualsSnapshotAtOffset({access}, {snapVar}, {off}, arena))");
                }
                else
                {
                    sb.AppendLine($"            if (!{helper}.EqualsSnapshotAtOffset({access}, {snapVar}, {off}, arena))");
                }

                sb.AppendLine($"                {builderVar}.SetValue({path}, {access});");
                break;
            }
        }

        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("        {");

        // Missing from the snapshot: a non-null value is new information, a null one is not.
        switch (shape.Category)
        {
            case Category.Scalar:
                sb.AppendLine($"            {builderVar}.Set({path}, {shape.Scalar!.CastForWrite(access)});");
                break;
            case Category.Enum:
                sb.AppendLine($"            {builderVar}.Set({path}, ({shape.Scalar!.BsonSideType}){access});");
                break;
            default:
                if (shape.Type.IsValueType && shape.Category != Category.Nullable)
                {
                    sb.AppendLine($"            {builderVar}.SetValue({path}, {access});");
                }
                else
                {
                    sb.AppendLine($"            if ({access} != null) {builderVar}.SetValue({path}, {access});");
                }
                break;
        }

        sb.AppendLine("        }");
    }

    private sealed class TypeInfo(INamedTypeSymbol symbol)
    {
        public INamedTypeSymbol Symbol { get; } = symbol;
        public string Name => Symbol.Name;
        public List<PropertyInfo> Properties { get; } = [];
    }

    private sealed class PropertyInfo(IPropertySymbol symbol, string elementName, Shape shape)
    {
        public IPropertySymbol Symbol { get; } = symbol;
        public string ElementName { get; } = elementName;
        public Shape Shape { get; } = shape;
    }
}
