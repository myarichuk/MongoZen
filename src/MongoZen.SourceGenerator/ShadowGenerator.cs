using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace MongoZen.SourceGenerator;

[Generator]
public class ShadowGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor UnsupportedTypeDiagnostic = new(
        id: "MZ001",
        title: "Unsupported type in Document",
        messageFormat: "Type '{0}' is not supported for change tracking. Only unmanaged types, strings, collections, dictionaries, or other Documents are supported.",
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
            .Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public && !p.IsReadOnly);

        foreach (var prop in members)
        {
            if (prop.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "MongoDB.Bson.Serialization.Attributes.BsonIgnoreAttribute" || a.AttributeClass?.Name == "BsonIgnoreAttribute"))
            {
                continue;
            }

            var category = CategorizeType(prop.Type, out var nestedType, out var secondaryNestedType);

            if (category == TypeCategory.Unsupported)
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedTypeDiagnostic, prop.Locations.FirstOrDefault(), prop.Type.ToDisplayString()));
                return false; 
            }

            var elementName = prop.Name;
            var bsonElementAttr = prop.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "BsonElementAttribute" || a.AttributeClass?.ToDisplayString() == "MongoDB.Bson.Serialization.Attributes.BsonElementAttribute");
            if (bsonElementAttr != null && bsonElementAttr.ConstructorArguments.Length > 0)
            {
                elementName = bsonElementAttr.ConstructorArguments[0].Value?.ToString() ?? prop.Name;
            }
            var bsonIdAttr = prop.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "BsonIdAttribute" || a.AttributeClass?.ToDisplayString() == "MongoDB.Bson.Serialization.Attributes.BsonIdAttribute");
            if (bsonIdAttr != null || prop.Name == "Id")
            {
                elementName = "_id";
            }
            var concurrencyCheckAttr = prop.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "ConcurrencyCheckAttribute" || a.AttributeClass?.ToDisplayString() == "MongoZen.ConcurrencyCheckAttribute");
            if (concurrencyCheckAttr != null)
            {
                elementName = "_etag";
            }

            if (category == TypeCategory.Document && nestedType is INamedTypeSymbol namedNested)
            {
                queue.Enqueue(namedNested);
            }

            info.Properties.Add(new PropertyInfo(prop, elementName, category, nestedType, secondaryNestedType));
        }

        return true;
    }

    private TypeCategory CategorizeType(ITypeSymbol type, out ITypeSymbol? nestedType, out ITypeSymbol? secondaryNestedType)
    {
        nestedType = null;
        secondaryNestedType = null;

        if (type.SpecialType == SpecialType.System_String)
        {
            return TypeCategory.String;
        }

        if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            nestedType = named.TypeArguments[0];
            return TypeCategory.Nullable;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return TypeCategory.Enum;
        }

        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fullName == "global::System.Guid" || fullName == "global::System.Decimal" || type.SpecialType != SpecialType.None)
        {
            return TypeCategory.Primitive;
        }

        if (IsDictionary(type, out var keyType, out var valueType))
        {
            nestedType = keyType;
            secondaryNestedType = valueType;
            return TypeCategory.Dictionary;
        }

        if (IsCollection(type, out var elementType))
        {
            nestedType = elementType;
            return TypeCategory.Collection;
        }

        if (type.TypeKind is TypeKind.Class or TypeKind.Struct)
        {
            var ns = type.ContainingNamespace.ToDisplayString();
            if (!ns.StartsWith("System") && !ns.StartsWith("MongoDB") && !ns.StartsWith("Microsoft"))
            {
                nestedType = type;
                return TypeCategory.Document;
            }
            
            if (ns == "MongoDB.Bson" && type.Name == "ObjectId")
            {
                return TypeCategory.Primitive;
            }
        }

        return TypeCategory.Unsupported;
    }

    private bool IsDictionary(ITypeSymbol type, out ITypeSymbol? keyType, out ITypeSymbol? valueType)
    {
        keyType = null;
        valueType = null;
        var dictInterface = type.AllInterfaces.FirstOrDefault(i => i.IsGenericType && (i.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.IDictionary<TKey, TValue>" || i.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"));
        if (dictInterface != null)
        {
            keyType = dictInterface.TypeArguments[0];
            valueType = dictInterface.TypeArguments[1];
            return true;
        }
        return false;
    }

    private static bool IsCollection(ITypeSymbol type, out ITypeSymbol? elementType)
    {
        elementType = null;
        var collectionInterface = type.AllInterfaces.FirstOrDefault(i => i.IsGenericType && i.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.ICollection<T>");
        if (collectionInterface != null)
        {
            elementType = collectionInterface.TypeArguments[0];
            return true;
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            elementType = arrayType.ElementType;
            return true;
        }

        return false;
    }

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
        sb.AppendLine($"partial class {info.Name} : IBlittableDocument<{info.Name}>");
        sb.AppendLine("{");

        // Serialize
        sb.AppendLine($"    public static void Serialize(ref ArenaBsonWriter writer, {info.Name} entity)");
        sb.AppendLine("    {");
        sb.AppendLine("        writer.WriteStartDocument();");
        foreach (var prop in info.Properties)
        {
            var access = $"entity.{prop.Symbol.Name}";
            GenerateWriteCall(sb, prop, access);
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
        var name = prop.ElementName;
        switch (prop.Category)
        {
            case TypeCategory.Enum:
                var underlyingType = ((INamedTypeSymbol)prop.Symbol.Type).EnumUnderlyingType!;
                var enumMethod = GetWriteMethod(underlyingType);
                sb.AppendLine($"        writer.{enumMethod}(\"{name}\", ({underlyingType.ToDisplayString()}){access});");
                break;
            case TypeCategory.Primitive:
                var method = GetWriteMethod(prop.Symbol.Type);
                sb.AppendLine($"        writer.{method}(\"{name}\", {access});");
                break;
            case TypeCategory.String:
                sb.AppendLine($"        if ({access} != null) writer.WriteString(\"{name}\", {access}.AsSpan());");
                break;
            case TypeCategory.Nullable:
                sb.AppendLine($"        if ({access}.HasValue) {{ writer.{GetWriteMethod(prop.NestedType!)}(\"{name}\", {access}.Value); }} else {{ writer.WriteNull(\"{name}\"); }}");
                break;
            case TypeCategory.Document:
                sb.AppendLine($"        if ({access} != null) {{ writer.WriteName(\"{name}\", BlittableBsonConstants.BsonType.Document); {prop.NestedType!.Name}.Serialize(ref writer, {access}); }}");
                break;
            case TypeCategory.Collection:
                sb.AppendLine($"        if ({access} != null) CollectionHelper<{prop.NestedType!.ToDisplayString()}>.WriteArray(ref writer, \"{name}\".AsSpan(), {access});");
                break;
            case TypeCategory.Dictionary:
                sb.AppendLine($"        if ({access} != null) DictionaryHelper<{prop.SecondaryNestedType!.ToDisplayString()}>.WriteDictionary(ref writer, \"{name}\".AsSpan(), {access});");
                break;
        }
    }

    private void GenerateReadCall(StringBuilder sb, PropertyInfo prop, string target, string docVar, string arenaVar)
    {
        var name = prop.ElementName;
        
        sb.AppendLine($"        if ({docVar}.TryGetElementOffset(\"{name}\", out var offset_{prop.Symbol.Name}))");
        sb.AppendLine("        {");
        
        switch (prop.Category)
        {
            case TypeCategory.Enum:
                var underlyingType = ((INamedTypeSymbol)prop.Symbol.Type).EnumUnderlyingType!;
                var enumMethod = GetReadMethod(underlyingType);
                sb.AppendLine($"            {target} = ({prop.Symbol.Type.ToDisplayString()}){docVar}.{enumMethod}(offset_{prop.Symbol.Name});");
                break;
            case TypeCategory.Primitive:
                var method = GetReadMethod(prop.Symbol.Type);
                sb.AppendLine($"            {target} = {docVar}.{method}(offset_{prop.Symbol.Name});");
                break;
            case TypeCategory.String:
                sb.AppendLine($"            {target} = {docVar}.GetString(offset_{prop.Symbol.Name});");
                break;
            case TypeCategory.Nullable:
                sb.AppendLine($"            {target} = {docVar}.{GetReadMethod(prop.NestedType!)}(offset_{prop.Symbol.Name});");
                break;
            case TypeCategory.Document:
                sb.AppendLine($"            {target} = {prop.NestedType!.Name}.Deserialize({docVar}.GetDocument(offset_{prop.Symbol.Name}, {arenaVar}), {arenaVar});");
                break;
            case TypeCategory.Collection:
                var arrayCall = $"{docVar}.GetArray(offset_{prop.Symbol.Name}, {arenaVar})";
                if (prop.Symbol.Type is IArrayTypeSymbol)
                {
                    sb.AppendLine($"            {target} = CollectionHelper<{prop.NestedType!.ToDisplayString()}>.ReadArray({arrayCall}, {arenaVar});");
                }
                else
                {
                    sb.AppendLine($"            {target} = CollectionHelper<{prop.NestedType!.ToDisplayString()}>.ReadList({arrayCall}, {arenaVar});");
                }
                break;
            case TypeCategory.Dictionary:
                sb.AppendLine($"            {target} = DictionaryHelper<{prop.SecondaryNestedType!.ToDisplayString()}>.ReadDictionary({docVar}.GetDocument(offset_{prop.Symbol.Name}, {arenaVar}), {arenaVar});");
                break;
        }
        
        sb.AppendLine("        }");
    }

    private void EmitPropertyDiff(StringBuilder sb, PropertyInfo prop, string entityVar, string snapVar, string builderVar, string prefixVar)
    {
        var name = prop.ElementName;
        var propAccess = $"{entityVar}.{prop.Symbol.Name}";
        
        sb.AppendLine($"        if ({snapVar}.TryGetElementOffset(\"{name}\", out var off_{prop.Symbol.Name}))");
        sb.AppendLine("        {");
        sb.AppendLine($"            Span<char> fullPath_{prop.Symbol.Name} = stackalloc char[{prefixVar}.Length + {name.Length} + 1];");
        sb.AppendLine($"            int len_{prop.Symbol.Name} = 0;");
        sb.AppendLine($"            if ({prefixVar}.Length > 0) {{ {prefixVar}.CopyTo(fullPath_{prop.Symbol.Name}); fullPath_{prop.Symbol.Name}[{prefixVar}.Length] = '.'; len_{prop.Symbol.Name} = {prefixVar}.Length + 1; }}");
        sb.AppendLine($"            \"{name}\".AsSpan().CopyTo(fullPath_{prop.Symbol.Name}.Slice(len_{prop.Symbol.Name}));");
        sb.AppendLine($"            var path_{prop.Symbol.Name} = fullPath_{prop.Symbol.Name}.Slice(0, len_{prop.Symbol.Name} + {name.Length});");

        switch (prop.Category)
        {
            case TypeCategory.Enum:
                var underlyingType = ((INamedTypeSymbol)prop.Symbol.Type).EnumUnderlyingType!;
                var enumMethod = GetReadMethod(underlyingType);
                sb.AppendLine($"            if (({underlyingType.ToDisplayString()}){propAccess} != {snapVar}.{enumMethod}(off_{prop.Symbol.Name}))");
                sb.AppendLine($"                {builderVar}.Set(path_{prop.Symbol.Name}, ({underlyingType.ToDisplayString()}){propAccess});");
                break;
            case TypeCategory.Primitive:
                var method = GetReadMethod(prop.Symbol.Type);
                sb.AppendLine($"            if ({propAccess} != {snapVar}.{method}(off_{prop.Symbol.Name}))");
                sb.AppendLine($"                {builderVar}.Set(path_{prop.Symbol.Name}, {propAccess});");
                break;
            case TypeCategory.String:
                sb.AppendLine($"            if (!object.Equals({snapVar}.GetString(off_{prop.Symbol.Name}), {propAccess}))");
                sb.AppendLine($"                {builderVar}.Set(path_{prop.Symbol.Name}, {propAccess});");
                break;
            case TypeCategory.Nullable:
                var nMethod = GetReadMethod(prop.NestedType!);
                sb.AppendLine($"            var snap_{prop.Symbol.Name} = {snapVar}.{nMethod}(off_{prop.Symbol.Name});");
                sb.AppendLine($"            if (!object.Equals({propAccess}, snap_{prop.Symbol.Name}))");
                sb.AppendLine($"                {builderVar}.SetObject<{prop.Symbol.Type.ToDisplayString()}>(path_{prop.Symbol.Name}, {propAccess});");
                break;
            case TypeCategory.Document:
                sb.AppendLine($"            if ({propAccess} != null)");
                sb.AppendLine("            {");
                sb.AppendLine($"                var nestedSnap_{prop.Symbol.Name} = {snapVar}.GetDocument(off_{prop.Symbol.Name}, arena);");
                sb.AppendLine($"                {prop.NestedType!.Name}.BuildUpdate({propAccess}, nestedSnap_{prop.Symbol.Name}, ref {builderVar}, arena, path_{prop.Symbol.Name});");
                sb.AppendLine("            }");
                break;
            case TypeCategory.Collection:
                sb.AppendLine($"            {builderVar}.Set(path_{prop.Symbol.Name}, CollectionHelper<{prop.NestedType!.ToDisplayString()}>.ToBsonValue({propAccess}));");
                break;
            case TypeCategory.Dictionary:
                sb.AppendLine($"            {builderVar}.Set(path_{prop.Symbol.Name}, DictionaryHelper<{prop.SecondaryNestedType!.ToDisplayString()}>.ToBsonValue({propAccess}));");
                break;
            default:
                sb.AppendLine($"            {builderVar}.Set(path_{prop.Symbol.Name}, BsonValue.Create({propAccess}));");
                break;
        }
        
        sb.AppendLine("        }");
    }

    private string GetWriteMethod(ITypeSymbol type)
    {
        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fullName == "global::System.Guid") return "WriteGuid";
        if (fullName == "global::System.Decimal") return "WriteDecimal128";

        var name = type.Name;
        if (type.ContainingNamespace?.ToDisplayString() == "MongoDB.Bson" && name == "ObjectId")
            return "WriteObjectId";

        return type.SpecialType switch
        {
            SpecialType.System_Int32 => "WriteInt32",
            SpecialType.System_Int64 => "WriteInt64",
            SpecialType.System_Double => "WriteDouble",
            SpecialType.System_Boolean => "WriteBoolean",
            SpecialType.System_DateTime => "WriteDateTime",
            SpecialType.System_Decimal => "WriteDecimal128",
            _ => "WriteBinary"
        };
    }

    private string GetReadMethod(ITypeSymbol type)
    {
        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fullName == "global::System.Guid") return "GetGuid";
        if (fullName == "global::System.Decimal") return "GetDecimal128";

        var name = type.Name;
        if (type.ContainingNamespace?.ToDisplayString() == "MongoDB.Bson" && name == "ObjectId")
            return "GetObjectId";

        return type.SpecialType switch
        {
            SpecialType.System_Int32 => "GetInt32",
            SpecialType.System_Int64 => "GetInt64",
            SpecialType.System_Double => "GetDouble",
            SpecialType.System_Boolean => "GetBoolean",
            SpecialType.System_DateTime => "GetDateTime",
            SpecialType.System_Decimal => "GetDecimal128",
            _ => $"Get<{type.ToDisplayString()}>"
        };
    }

    private class TypeInfo(INamedTypeSymbol symbol)
    {
        public INamedTypeSymbol Symbol { get; } = symbol;
        public string Name => Symbol.Name;
        public List<PropertyInfo> Properties { get; } = [];
    }

    private class PropertyInfo(IPropertySymbol symbol, string elementName, TypeCategory category, ITypeSymbol? nestedType, ITypeSymbol? secondaryNestedType)
    {
        public IPropertySymbol Symbol { get; } = symbol;
        public string ElementName { get; } = elementName;
        public TypeCategory Category { get; } = category;
        public ITypeSymbol? NestedType { get; } = nestedType;
        public ITypeSymbol? SecondaryNestedType { get; } = secondaryNestedType;
    }

    private enum TypeCategory
    {
        Primitive,
        String,
        Nullable,
        Collection,
        Dictionary,
        Document,
        Enum,
        Unsupported
    }
}
