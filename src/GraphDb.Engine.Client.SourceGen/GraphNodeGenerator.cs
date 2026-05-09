using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GraphDb.Engine.Client.SourceGen;

[Generator]
public sealed class GraphNodeGenerator : IIncrementalGenerator
{
    private const string GraphNodeAttributeFqn = "GraphDb.Engine.Client.GraphNodeAttribute";
    private const string GraphPropertyAttributeFqn = "GraphDb.Engine.Client.GraphPropertyAttribute";
    private const string GraphIndexedAttributeFqn = "GraphDb.Engine.Client.GraphIndexedAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext ctx)
    {
        var provider = ctx.SyntaxProvider
            .ForAttributeWithMetadataName(
                GraphNodeAttributeFqn,
                predicate: static (n, _) => n is ClassDeclarationSyntax,
                transform: static (ctx, _) => BuildModel(ctx))
            .Where(static m => m is not null);

        ctx.RegisterSourceOutput(provider, static (spc, model) =>
            spc.AddSource($"{model!.ClassName}.GraphDb.g.cs", GraphNodeEmitter.Emit(model)));
    }

    private static GraphNodeModel? BuildModel(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol classSymbol)
            return null;

        var graphNodeAttr = ctx.Attributes[0];
        string label = classSymbol.Name;
        if (graphNodeAttr.ConstructorArguments.Length > 0 &&
            graphNodeAttr.ConstructorArguments[0].Value is string labelArg &&
            !string.IsNullOrEmpty(labelArg))
        {
            label = labelArg;
        }

        var model = new GraphNodeModel
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
                if (attrFqn == GraphPropertyAttributeFqn)
                {
                    graphKey = prop.Name;
                    if (attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string keyArg &&
                        !string.IsNullOrEmpty(keyArg))
                    {
                        graphKey = keyArg;
                    }
                }
                else if (attrFqn == GraphIndexedAttributeFqn)
                {
                    if (attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string idxArg)
                    {
                        indexName = idxArg;
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
                IndexName = indexName,
            });
        }

        return model;
    }
}
