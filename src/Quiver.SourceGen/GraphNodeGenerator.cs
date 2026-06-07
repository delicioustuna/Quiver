using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Quiver.SourceGen;

[Generator]
public sealed class GraphNodeGenerator : IIncrementalGenerator
{
    private const string NodeAttributeFqn = "Quiver.Api.NodeAttribute";
    private const string PropertyAttributeFqn = "Quiver.Api.PropertyAttribute";
    private const string IndexedAttributeFqn = "Quiver.Api.IndexedAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext ctx)
    {
        var provider = ctx.SyntaxProvider
            .ForAttributeWithMetadataName(
                NodeAttributeFqn,
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
