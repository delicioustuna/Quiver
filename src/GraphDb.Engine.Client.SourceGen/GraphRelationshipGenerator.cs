using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GraphDb.Engine.Client.SourceGen;

[Generator]
public sealed class GraphRelationshipGenerator : IIncrementalGenerator
{
    private const string GraphRelationshipAttributeFqn = "GraphDb.Engine.Client.GraphRelationshipAttribute";
    private const string GraphPropertyAttributeFqn = "GraphDb.Engine.Client.GraphPropertyAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext ctx)
    {
        var provider = ctx.SyntaxProvider
            .ForAttributeWithMetadataName(
                GraphRelationshipAttributeFqn,
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

        var model = new GraphRelationshipModel
        {
            Namespace = classSymbol.ContainingNamespace.IsGlobalNamespace
                ? ""
                : classSymbol.ContainingNamespace.ToDisplayString(),
            ClassName = classSymbol.Name,
            RelType = relType,
        };

        foreach (var member in classSymbol.GetMembers())
        {
            if (member is not IPropertySymbol prop)
                continue;

            string? graphKey = null;
            foreach (var attr in prop.GetAttributes())
            {
                if (attr.AttributeClass?.ToDisplayString() == GraphPropertyAttributeFqn)
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
            model.Properties.Add(new PropertyModel
            {
                PropertyName = prop.Name,
                GraphKey = graphKey,
                CSharpType = typeName,
            });
        }

        return model;
    }
}
