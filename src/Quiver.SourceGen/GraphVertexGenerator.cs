using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Quiver.SourceGen;

/// <summary>
/// <c>[Vertex]</c> 属性付きクラスから <c>IGraphVertex&lt;T&gt;</c> の CRUD 実装を生成するソースジェネレータ。
/// </summary>
[Generator]
public sealed class GraphVertexGenerator : IIncrementalGenerator
{
    private const string VertexAttributeFqn = "Quiver.Api.VertexAttribute";
    private const string PropertyAttributeFqn = "Quiver.Api.PropertyAttribute";
    private const string IndexedAttributeFqn = "Quiver.Api.IndexedAttribute";

    /// <summary>生成パイプラインを登録する (<see cref="IIncrementalGenerator"/> 実装)。</summary>
    public void Initialize(IncrementalGeneratorInitializationContext ctx)
    {
        var provider = ctx.SyntaxProvider
            .ForAttributeWithMetadataName(
                VertexAttributeFqn,
                predicate: static (n, _) => n is ClassDeclarationSyntax,
                transform: static (ctx, _) => BuildModel(ctx))
            .Where(static m => m is not null);

        ctx.RegisterSourceOutput(provider, static (spc, model) =>
            spc.AddSource($"{model!.ClassName}.GraphDb.g.cs", GraphVertexEmitter.Emit(model)));
    }

    private static GraphVertexModel? BuildModel(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol classSymbol)
            return null;

        var graphVertexAttr = ctx.Attributes[0];
        string label = classSymbol.Name;
        if (graphVertexAttr.ConstructorArguments.Length > 0 &&
            graphVertexAttr.ConstructorArguments[0].Value is string labelArg &&
            !string.IsNullOrEmpty(labelArg))
        {
            label = labelArg;
        }

        var model = new GraphVertexModel
        {
            Namespace = classSymbol.ContainingNamespace.IsGlobalNamespace
                ? ""
                : classSymbol.ContainingNamespace.ToDisplayString(),
            ClassName = classSymbol.Name,
            Label = label,
        };

        foreach (var member in classSymbol.GetMembers())
        {
            if (member is not IPropertySymbol prop)
                continue;

            string? graphKey = null;
            string? indexName = null;

            foreach (var attr in prop.GetAttributes())
            {
                var attrFqn = attr.AttributeClass?.ToDisplayString();
                if (attrFqn == PropertyAttributeFqn)
                {
                    graphKey = prop.Name;
                    if (attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string keyArg &&
                        !string.IsNullOrEmpty(keyArg))
                    {
                        graphKey = keyArg;
                    }
                }
                else if (attrFqn == IndexedAttributeFqn)
                {
                    indexName = attr.ConstructorArguments.Length > 0 &&
                                attr.ConstructorArguments[0].Value is string { Length: > 0 } idxArg
                        ? idxArg
                        : $"idx_{label.ToLowerInvariant()}_{prop.Name.ToLowerInvariant()}";
                }
            }

            if (graphKey == null)
                continue;

            var typeName = prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            bool isMultiValued = false;

            if (prop.Type is INamedTypeSymbol { IsGenericType: true } namedType
                && namedType.TypeArguments.Length == 1
                && namedType.OriginalDefinition.ContainingNamespace is { } ns
                && ns.ToDisplayString() == "System.Collections.Generic"
                && namedType.OriginalDefinition.Name is "List" or "IList" or "IReadOnlyList")
            {
                isMultiValued = true;
                typeName = namedType.TypeArguments[0]
                    .ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            }

            model.Properties.Add(new PropertyModel
            {
                PropertyName = prop.Name,
                GraphKey = graphKey,
                CSharpType = typeName,
                IndexName = indexName,
                IsMultiValued = isMultiValued,
            });
        }

        return model;
    }
}
