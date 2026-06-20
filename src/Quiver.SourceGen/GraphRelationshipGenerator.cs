using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Quiver.SourceGen;

/// <summary>
/// <c>[Relationship&lt;TSource, TTarget&gt;]</c> 属性付きクラスから型付きリレーションシップ実装を
/// 生成するソースジェネレータ。
/// </summary>
[Generator]
public sealed class GraphRelationshipGenerator : IIncrementalGenerator
{
    private const string RelationshipAttributeFqn = "Quiver.Api.RelationshipAttribute`2";
    private const string PropertyAttributeFqn = "Quiver.Api.PropertyAttribute";

    /// <summary>生成パイプラインを登録する (<see cref="IIncrementalGenerator"/> 実装)。</summary>
    public void Initialize(IncrementalGeneratorInitializationContext ctx)
    {
        var provider = ctx.SyntaxProvider
            .ForAttributeWithMetadataName(
                RelationshipAttributeFqn,
                predicate: static (n, _) => n is ClassDeclarationSyntax,
                transform: static (ctx, _) => BuildModel(ctx))
            .Where(static m => m is not null);

        ctx.RegisterSourceOutput(provider, static (spc, model) =>
            spc.AddSource($"{model!.ClassName}.GraphRel.g.cs", GraphRelationshipEmitter.Emit(model)));
    }

    private static GraphRelationshipModel? BuildModel(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol classSymbol)
            return null;

        var relAttr = ctx.Attributes[0];
        string relType = classSymbol.Name;
        if (relAttr.ConstructorArguments.Length > 0 &&
            relAttr.ConstructorArguments[0].Value is string typeArg &&
            !string.IsNullOrEmpty(typeArg))
        {
            relType = typeArg;
        }
        else if (relAttr.ApplicationSyntaxReference?.GetSyntax() is
                 Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax attrSyntax)
        {
            var args = attrSyntax.ArgumentList?.Arguments;
            if (args?.Count > 0 &&
                args.Value[0].Expression is
                    Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax lit &&
                lit.Token.Value is string litVal &&
                !string.IsNullOrEmpty(litVal))
            {
                relType = litVal;
            }
        }

        // ジェネリック属性 RelationshipAttribute<TSource, TTarget> の型引数から端点型を取得する。
        var attrClass = relAttr.AttributeClass;
        if (attrClass is null || attrClass.TypeArguments.Length != 2)
            return null;

        var fqnFormat = SymbolDisplayFormat.FullyQualifiedFormat;
        var sourceFqn = attrClass.TypeArguments[0].ToDisplayString(fqnFormat);
        var targetFqn = attrClass.TypeArguments[1].ToDisplayString(fqnFormat);

        var model = new GraphRelationshipModel
        {
            Namespace = classSymbol.ContainingNamespace.IsGlobalNamespace
                ? ""
                : classSymbol.ContainingNamespace.ToDisplayString(),
            ClassName = classSymbol.Name,
            RelType = relType,
            SourceFqn = sourceFqn,
            TargetFqn = targetFqn,
        };

        foreach (var member in classSymbol.GetMembers())
        {
            if (member is not IPropertySymbol prop)
                continue;

            string? graphKey = null;
            foreach (var attr in prop.GetAttributes())
            {
                if (attr.AttributeClass?.ToDisplayString() == PropertyAttributeFqn)
                {
                    graphKey = prop.Name;
                    if (attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string keyArg &&
                        !string.IsNullOrEmpty(keyArg))
                    {
                        graphKey = keyArg;
                    }
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
                IsMultiValued = isMultiValued,
            });
        }

        return model;
    }
}
