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
            GenerateShadow(info, context);
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
            if (bsonIdAttr != null)
            {
                elementName = "_id";
            }

            if (category == TypeCategory.Document && nestedType is INamedTypeSymbol namedNested)
            {
                queue.Enqueue(namedNested);
            }
            else if (category == TypeCategory.Collection && nestedType != null)
            {
                 var elemCategory = CategorizeType(nestedType, out var elemNested, out _);
                 if (elemCategory == TypeCategory.Document && elemNested is INamedTypeSymbol namedElem)
                 {
                     queue.Enqueue(namedElem);
                 }
            }
            else if (category == TypeCategory.Dictionary && nestedType != null && secondaryNestedType != null)
            {
                var keyCategory = CategorizeType(nestedType, out var keyNested, out _);
                if (keyCategory == TypeCategory.Document && keyNested is INamedTypeSymbol namedKey)
                {
                    queue.Enqueue(namedKey);
                }

                var valCategory = CategorizeType(secondaryNestedType, out var valNested, out _);
                if (valCategory == TypeCategory.Document && valNested is INamedTypeSymbol namedVal)
                {
                    queue.Enqueue(namedVal);
                }
            }

            info.Properties.Add(new PropertyInfo(prop, elementName, category, nestedType, secondaryNestedType));
        }

        return true;
    }

    private static bool IsPolymorphic(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Interface)
        {
            return true;
        }

        if (type.IsAbstract)
        {
            return true;
        }

        if (type.SpecialType == SpecialType.System_Object)
        {
            return true;
        }

        if (type.GetAttributes().Any(a => a.AttributeClass?.Name == "BsonKnownTypesAttribute" || a.AttributeClass?.Name == "BsonKnownTypes"))
        {
            return true;
        }

        return false;
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

        if (type.IsUnmanagedType)
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
                if (IsPolymorphic(type))
                {
                    return TypeCategory.Polymorphic;
                }

                nestedType = type;
                return TypeCategory.Document;
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

    private void GenerateShadow(TypeInfo info, SourceProductionContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using MongoZen;");
        sb.AppendLine("using SharpArena.Allocators;");
        sb.AppendLine("using SharpArena.Collections;");
        sb.AppendLine("using MongoDB.Driver;");
        sb.AppendLine("using MongoDB.Bson;");
        sb.AppendLine();
        sb.AppendLine($"namespace {info.Symbol.ContainingNamespace.ToDisplayString()};");
        sb.AppendLine();
        sb.AppendLine($"public readonly unsafe struct {info.Name}Shadow");
        sb.AppendLine("{");
        sb.AppendLine("    public readonly bool _HasValue;");

        foreach (var prop in info.Properties)
        {
            sb.AppendLine($"    public readonly {GetShadowType(prop)} {prop.Symbol.Name};");
        }

        sb.AppendLine();
        sb.AppendLine(
            $"    public static {info.Name}Shadow Create({info.Symbol.ToDisplayString()}? entity, ArenaAllocator arena)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (entity == null) return default;");
        sb.AppendLine();
        
        foreach (var prop in info.Properties)
        {
            if (prop.Category == TypeCategory.Collection)
            {
                var elemShadow = GetCollectionElementShadowType(prop.NestedType!);
                sb.AppendLine($"        var {prop.Symbol.Name}_cloned = default(ArenaList<{elemShadow}>);");
                sb.AppendLine($"        if (entity.{prop.Symbol.Name} != null)");
                sb.AppendLine("        {");
                sb.AppendLine(
                    $"            {prop.Symbol.Name}_cloned = new ArenaList<{elemShadow}>(arena, entity.{prop.Symbol.Name}.Count);");
                sb.AppendLine($"            foreach (var item in entity.{prop.Symbol.Name})");
                sb.AppendLine("            {");
                sb.AppendLine(
                    $"                {prop.Symbol.Name}_cloned.Add({GenerateElementCloneExpr(prop.NestedType!, "item")});");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
            }
            else if (prop.Category == TypeCategory.Dictionary)
            {
                var keyShadow = GetCollectionElementShadowType(prop.NestedType!);
                var valShadow = GetCollectionElementShadowType(prop.SecondaryNestedType!);
                sb.AppendLine(
                    $"        var {prop.Symbol.Name}_cloned = default(ArenaDictionary<{keyShadow}, {valShadow}>);");
                sb.AppendLine($"        if (entity.{prop.Symbol.Name} != null)");
                sb.AppendLine("        {");
                sb.AppendLine(
                    $"            {prop.Symbol.Name}_cloned = new ArenaDictionary<{keyShadow}, {valShadow}>(arena, entity.{prop.Symbol.Name}.Count);");
                sb.AppendLine($"            foreach (var kvp in entity.{prop.Symbol.Name})");
                sb.AppendLine("            {");
                sb.AppendLine(
                    $"                {prop.Symbol.Name}_cloned.Add({GenerateElementCloneExpr(prop.NestedType!, "kvp.Key")}, {GenerateElementCloneExpr(prop.SecondaryNestedType!, "kvp.Value")});");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
            }
        }

        sb.AppendLine($"        return new {info.Name}Shadow(");
        sb.Append("            true");
        
        for (int i = 0; i < info.Properties.Count; i++)
        {
            var prop = info.Properties[i];
            var expr = prop.Category switch
            {
                TypeCategory.Primitive => $"entity.{prop.Symbol.Name}",
                TypeCategory.String =>
                    $"entity.{prop.Symbol.Name} == null ? default : ArenaUtf8String.Clone(entity.{prop.Symbol.Name}, arena)",
                TypeCategory.Nullable => $"entity.{prop.Symbol.Name}",
                TypeCategory.Document => $"{prop.NestedType!.Name}Shadow.Create(entity.{prop.Symbol.Name}, arena)",
                TypeCategory.Polymorphic => $"ClonePolymorphic<{(prop.Symbol.Type as INamedTypeSymbol)?.ToDisplayString() ?? "object"}>(entity.{prop.Symbol.Name}, arena)",
                TypeCategory.Collection => $"{prop.Symbol.Name}_cloned",
                TypeCategory.Dictionary => $"{prop.Symbol.Name}_cloned",
                _ => $"entity.{prop.Symbol.Name}"
            };
            sb.AppendLine(",");
            sb.Append($"            {expr}");
        }
        sb.AppendLine();
        sb.AppendLine("        );");
        sb.AppendLine("    }");

        sb.AppendLine();
        sb.Append($"    private {info.Name}Shadow(bool hasValue");
        for (int i = 0; i < info.Properties.Count; i++)
        {
            var prop = info.Properties[i];
            var type = GetShadowType(prop);
            sb.AppendLine(",");
            sb.Append($"        {type} {prop.Symbol.Name.ToLower()}");
        }
        sb.AppendLine();
        sb.AppendLine("    )");
        sb.AppendLine("    {");
        sb.AppendLine("        this._HasValue = hasValue;");
        foreach (var prop in info.Properties)
        {
            sb.AppendLine($"        this.{prop.Symbol.Name} = {prop.Symbol.Name.ToLower()};");
        }
        sb.AppendLine("    }");

        sb.AppendLine();
        sb.AppendLine("    private static global::MongoZen.ArenaBsonBytes ClonePolymorphic<T>(T? obj, ArenaAllocator arena)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (obj == null) return default;");
        sb.AppendLine("        var bytes = obj.ToBson<T>();");
        sb.AppendLine("        var ptr = (byte*)arena.Alloc((UIntPtr)bytes.Length, (UIntPtr)1);");
        sb.AppendLine("        new ReadOnlySpan<byte>(bytes).CopyTo(new Span<byte>(ptr, bytes.Length));");
        sb.AppendLine("        return new global::MongoZen.ArenaBsonBytes(ptr, bytes.Length);");
        sb.AppendLine("    }");

        sb.AppendLine();
        sb.AppendLine($"    public bool Equals({info.Symbol.ToDisplayString()}? entity)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (entity == null) return !this._HasValue;");
        sb.AppendLine("        if (!this._HasValue) return false;");
        sb.AppendLine();
        foreach (var prop in info.Properties)
        {
            var access = $"entity.{prop.Symbol.Name}";
            var shadow = $"this.{prop.Symbol.Name}";

            switch (prop.Category)
            {
                case TypeCategory.Primitive:
                case TypeCategory.Nullable:
                    sb.AppendLine($"        if ({access} != {shadow}) return false;");
                    break;
                case TypeCategory.String:
                    sb.AppendLine(
                        $"        if ({access} == null) {{ if ({shadow}.RawPtr != null) return false; }}");
                    sb.AppendLine($"        else if ({shadow}.RawPtr == null || !{shadow}.Equals({access})) return false;");
                    break;
                case TypeCategory.Document:
                    sb.AppendLine($"        if (!{shadow}.Equals({access})) return false;");
                    break;
                case TypeCategory.Polymorphic:
                    var polyType = (prop.Symbol.Type as INamedTypeSymbol)?.ToDisplayString() ?? "object";
                    sb.AppendLine($"        {{");
                    sb.AppendLine($"            var curPoly = {access}?.ToBson<{polyType}>();");
                    sb.AppendLine($"            if ((curPoly == null) != ({shadow}.RawPtr == null)) return false;");
                    sb.AppendLine($"            if (curPoly != null && !curPoly.AsSpan().SequenceEqual({shadow}.AsReadOnlySpan())) return false;");
                    sb.AppendLine($"        }}");
                    break;
                case TypeCategory.Collection:
                    sb.AppendLine($"        if (!Is{prop.Symbol.Name}Equal({access})) return false;");
                    break;
                case TypeCategory.Dictionary:
                    sb.AppendLine($"        if (!Is{prop.Symbol.Name}Equal({access})) return false;");
                    break;
            }
        }
        sb.AppendLine("        return true;");
        sb.AppendLine("    }");

        sb.AppendLine();
        sb.AppendLine(
            $"    public UpdateDefinition<BsonDocument>? BuildUpdate({info.Symbol.ToDisplayString()} entity, UpdateDefinitionBuilder<BsonDocument> builder)");
        sb.AppendLine("    {");
        sb.AppendLine("        UpdateDefinition<BsonDocument>? combined = null;");
        sb.AppendLine("        this.BuildUpdate(entity, \"\", builder, ref combined);");
        sb.AppendLine("        return combined;");
        sb.AppendLine("    }");

        sb.AppendLine();
        sb.AppendLine(
            $"    public void BuildUpdate({info.Symbol.ToDisplayString()}? entity, string pathPrefix, UpdateDefinitionBuilder<BsonDocument> builder, ref UpdateDefinition<BsonDocument>? combined)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (this.Equals(entity)) return;");
        sb.AppendLine("        if (entity == null) return;");

        foreach (var prop in info.Properties)
        {
            var propName = prop.Symbol.Name;
            var access = $"entity.{propName}";
            var path = $"(string.IsNullOrEmpty(pathPrefix) ? \"{prop.ElementName}\" : pathPrefix + \"{prop.ElementName}\")";

            sb.AppendLine();
            sb.AppendLine($"        // {propName}");
            switch (prop.Category)
            {
                case TypeCategory.Primitive:
                    sb.AppendLine($"        if ({access} != this.{propName})");
                    sb.AppendLine(
                        $"            combined = (combined == null) ? builder.Set({path}, {access}) : builder.Combine(combined, builder.Set({path}, {access}));");
                    break;

                case TypeCategory.String:
                    sb.AppendLine("        {");
                    sb.AppendLine($"            var cur = {access};");
                    sb.AppendLine("            if (cur == null)");
                    sb.AppendLine("            {");
                    sb.AppendLine(
                        $"                if (this.{propName}.RawPtr != null) combined = (combined == null) ? builder.Unset({path}) : builder.Combine(combined, builder.Unset({path}));");
                    sb.AppendLine("            }");
                    sb.AppendLine($"            else if (this.{propName}.RawPtr == null || !this.{propName}.Equals(cur))");
                    sb.AppendLine("            {");
                    sb.AppendLine(
                        $"                combined = (combined == null) ? builder.Set({path}, cur) : builder.Combine(combined, builder.Set({path}, cur));");
                    sb.AppendLine("            }");
                    sb.AppendLine("        }");
                    break;

                case TypeCategory.Nullable:
                    sb.AppendLine($"        if ({access} != this.{propName})");
                    sb.AppendLine("        {");
                    sb.AppendLine(
                        $"            if ({access} == null) combined = (combined == null) ? builder.Unset({path}) : builder.Combine(combined, builder.Unset({path}));");
                    sb.AppendLine(
                        $"            else combined = (combined == null) ? builder.Set({path}, {access}) : builder.Combine(combined, builder.Set({path}, {access}));");
                    sb.AppendLine("        }");
                    break;

                case TypeCategory.Document:
                    sb.AppendLine($"        var child_{propName} = {access};");
                    sb.AppendLine($"        if (child_{propName} == null)");
                    sb.AppendLine("        {");
                    sb.AppendLine(
                        $"            if (this.{propName}._HasValue) combined = (combined == null) ? builder.Unset({path}) : builder.Combine(combined, builder.Unset({path}));");
                    sb.AppendLine("        }");
                    sb.AppendLine($"        else if (!this.{propName}._HasValue)");
                    sb.AppendLine("        {");
                    sb.AppendLine(
                        $"            combined = (combined == null) ? builder.Set({path}, child_{propName}) : builder.Combine(combined, builder.Set({path}, child_{propName}));");
                    sb.AppendLine("        }");
                    sb.AppendLine("        else");
                    sb.AppendLine("        {");
                    sb.AppendLine(
                        $"            this.{propName}.BuildUpdate(child_{propName}, {path} + \".\", builder, ref combined);");
                    sb.AppendLine("        }");
                    break;

                case TypeCategory.Polymorphic:
                    var polyType = (prop.Symbol.Type as INamedTypeSymbol)?.ToDisplayString() ?? "object";
                    sb.AppendLine($"        var poly_{propName} = {access}?.ToBson<{polyType}>();");
                    sb.AppendLine($"        if (poly_{propName} == null)");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            if (this.{propName}.RawPtr != null) combined = (combined == null) ? builder.Unset({path}) : builder.Combine(combined, builder.Unset({path}));");
                    sb.AppendLine("        }");
                    sb.AppendLine($"        else if (this.{propName}.RawPtr == null || !poly_{propName}.AsSpan().SequenceEqual(this.{propName}.AsReadOnlySpan()))");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            combined = (combined == null) ? builder.Set({path}, {access}) : builder.Combine(combined, builder.Set({path}, {access}));");
                    sb.AppendLine("        }");
                    break;

                case TypeCategory.Collection:
                    sb.AppendLine($"        var coll_{propName} = {access};");
                    sb.AppendLine($"        if (coll_{propName} == null)");
                    sb.AppendLine("        {");
                    sb.AppendLine(
                        $"            if (this.{propName}.Length != 0) combined = (combined == null) ? builder.Unset({path}) : builder.Combine(combined, builder.Unset({path}));");
                    sb.AppendLine("        }");
                    sb.AppendLine($"        else if (!Is{propName}Equal(coll_{propName}))");
                    sb.AppendLine("        {");
                    sb.AppendLine(
                        $"            combined = (combined == null) ? builder.Set({path}, coll_{propName}) : builder.Combine(combined, builder.Set({path}, coll_{propName}));");
                    sb.AppendLine("        }");
                    break;
                case TypeCategory.Dictionary:
                    sb.AppendLine($"        var dict_{propName} = {access};");
                    sb.AppendLine($"        if (dict_{propName} == null)");
                    sb.AppendLine("        {");
                    sb.AppendLine(
                        $"            if (this.{propName}.Count != 0) combined = (combined == null) ? builder.Unset({path}) : builder.Combine(combined, builder.Unset({path}));");
                    sb.AppendLine("        }");
                    sb.AppendLine($"        else if (!Is{propName}Equal(dict_{propName}))");
                    sb.AppendLine("        {");
                    sb.AppendLine(
                        $"            combined = (combined == null) ? builder.Set({path}, dict_{propName}) : builder.Combine(combined, builder.Set({path}, dict_{propName}));");
                    sb.AppendLine("        }");
                    break;
            }
        }

        sb.AppendLine("    }");

        foreach (var prop in info.Properties.Where(p => p.Category is TypeCategory.Collection or TypeCategory.Dictionary))
        {
            GenerateCollectionEqualityHelper(sb, prop);
        }

        sb.AppendLine("}");

        context.AddSource($"{info.Name}.Shadow.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private void GenerateElementEqualityCheck(StringBuilder sb, ITypeSymbol elemType, string managed, string shadow, bool useReturn)
    {
        var category = CategorizeType(elemType, out var nested, out _);
        var indent = "                ";
        var suffix = useReturn ? " return false;" : " combined = (combined == null) ? builder.Set(path, managed) : builder.Combine(combined, builder.Set(path, managed));";
        
        switch (category)
        {
            case TypeCategory.Primitive:
            case TypeCategory.Nullable:
                sb.AppendLine($"{indent}if ({managed} != {shadow}){suffix}");
                break;
            case TypeCategory.String:
                sb.AppendLine($"{indent}if ({managed} == null) {{ if ({shadow}.RawPtr != null){suffix} }}");
                sb.AppendLine($"{indent}else if ({shadow}.RawPtr == null || !{shadow}.Equals({managed})){suffix}");
                break;
            case TypeCategory.Document:
                sb.AppendLine($"{indent}if (!{shadow}.Equals({managed})){suffix}");
                break;
            case TypeCategory.Polymorphic:
                var polyType = elemType.ToDisplayString();
                sb.AppendLine($"{indent}var curPoly = {managed}?.ToBson<{polyType}>();");
                sb.AppendLine($"{indent}if ((curPoly == null) != ({shadow}.RawPtr == null)){suffix}");
                sb.AppendLine($"{indent}if (curPoly != null && !curPoly.AsSpan().SequenceEqual({shadow}.AsReadOnlySpan())){suffix}");
                break;
        }
    }

    private void GenerateCollectionEqualityHelper(StringBuilder sb, PropertyInfo prop)
    {
        sb.AppendLine();
        var managedType = prop.Symbol.Type.ToDisplayString();
        if (prop.Category == TypeCategory.Collection)
        {
            sb.AppendLine($"    private bool Is{prop.Symbol.Name}Equal({managedType}? current)");
            sb.AppendLine("    {");
            sb.AppendLine($"        if (current == null) return this.{prop.Symbol.Name}.Length == 0;");
            sb.AppendLine($"        if (current.Count != this.{prop.Symbol.Name}.Length) return false;");
            sb.AppendLine("        var idx = 0;");
            sb.AppendLine($"        var span = this.{prop.Symbol.Name}.AsReadOnlySpan();");
            sb.AppendLine("        foreach (var item in current)");
            sb.AppendLine("        {");
            sb.AppendLine("            var s = span[idx++];");
            GenerateElementEqualityCheck(sb, prop.NestedType!, "item", "s", true);
            sb.AppendLine("        }");
            sb.AppendLine("        return true;");
            sb.AppendLine("    }");
        }
        else if (prop.Category == TypeCategory.Dictionary)
        {
            sb.AppendLine($"    private bool Is{prop.Symbol.Name}Equal({managedType}? current)");
            sb.AppendLine("    {");
            sb.AppendLine($"        if (current == null) return this.{prop.Symbol.Name}.Count == 0;");
            sb.AppendLine($"        if (current.Count != this.{prop.Symbol.Name}.Count) return false;");
            sb.AppendLine("        foreach (var kvp in current)");
            sb.AppendLine("        {");
            sb.AppendLine(
                $"            if (!this.{prop.Symbol.Name}.TryGetValue(kvp.Key, out var sv)) return false;");
            GenerateElementEqualityCheck(sb, prop.SecondaryNestedType!, "kvp.Value", "sv", true);
            sb.AppendLine("        }");
            sb.AppendLine("        return true;");
            sb.AppendLine("    }");
        }
    }

    private string GenerateElementCloneExpr(ITypeSymbol elemType, string access)
    {
        var category = CategorizeType(elemType, out var nested, out _);
        return category switch
        {
            TypeCategory.Primitive => access,
            TypeCategory.String => $"{access} == null ? default : ArenaUtf8String.Clone({access}, arena)",
            TypeCategory.Nullable => access,
            TypeCategory.Document => $"{nested!.Name}Shadow.Create({access}, arena)",
            TypeCategory.Polymorphic => $"ClonePolymorphic<{elemType.ToDisplayString()}>({access}, arena)",
            _ => access
        };
    }

    private string GetShadowType(PropertyInfo prop)
    {
        return prop.Category switch
        {
            TypeCategory.Primitive => prop.Symbol.Type.ToDisplayString(),
            TypeCategory.String => "ArenaUtf8String",
            TypeCategory.Nullable => prop.Symbol.Type.ToDisplayString(),
            TypeCategory.Document => $"{prop.NestedType!.Name}Shadow",
            TypeCategory.Polymorphic => "global::MongoZen.ArenaBsonBytes",
            TypeCategory.Collection => $"ArenaList<{GetCollectionElementShadowType(prop.NestedType!)}>",
            TypeCategory.Dictionary =>
                $"ArenaDictionary<{GetCollectionElementShadowType(prop.NestedType!)}, {GetCollectionElementShadowType(prop.SecondaryNestedType!)}>",
            _ => "object?"
        };
    }

    private string GetCollectionElementShadowType(ITypeSymbol elemType)
    {
        var category = CategorizeType(elemType, out var nested, out _);
        return category switch
        {
            TypeCategory.Primitive => elemType.ToDisplayString(),
            TypeCategory.String => "ArenaUtf8String",
            TypeCategory.Nullable => elemType.ToDisplayString(),
            TypeCategory.Document => $"{nested!.Name}Shadow",
            TypeCategory.Polymorphic => "global::MongoZen.ArenaBsonBytes",
            _ => "object?"
        };
    }

    private string GenerateKeyAccess(ITypeSymbol keyType, string managedAccess)
    {
        if (keyType.SpecialType == SpecialType.System_String)
        {
            return managedAccess;
        }

        return managedAccess;
    }

    private class TypeInfo
    {
        public INamedTypeSymbol Symbol { get; }
        public string Name => Symbol.Name;
        public List<PropertyInfo> Properties { get; } = [];

        public TypeInfo(INamedTypeSymbol symbol)
        {
            Symbol = symbol;
        }
    }

    private class PropertyInfo
    {
        public IPropertySymbol Symbol { get; }
        public string ElementName { get; }
        public TypeCategory Category { get; }
        public ITypeSymbol? NestedType { get; }
        public ITypeSymbol? SecondaryNestedType { get; }

        public PropertyInfo(IPropertySymbol symbol, string elementName, TypeCategory category, ITypeSymbol? nestedType, ITypeSymbol? secondaryNestedType)
        {
            Symbol = symbol;
            ElementName = elementName;
            Category = category;
            NestedType = nestedType;
            SecondaryNestedType = secondaryNestedType;
        }
    }

    private enum TypeCategory
    {
        Primitive,
        String,
        Nullable,
        Collection,
        Dictionary,
        Document,
        Polymorphic,
        Unsupported
    }
}
