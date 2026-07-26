using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Quiver.SourceGen;

/// <summary>
/// <c>[Nexus]</c> 属性付きクラスから <c>IGraphNexus&lt;T&gt;</c> の CRUD 実装を
/// 生成するソースジェネレータ。
/// </summary>
[Generator]
public sealed class GraphNexusGenerator : IIncrementalGenerator
{
    private const string NexusAttributeFqn = "Quiver.Api.NexusAttribute";
    private const string RoleAttributeFqn = "Quiver.Api.RoleAttribute";
    private const string PropertyAttributeFqn = "Quiver.Api.PropertyAttribute";
    private const string GraphVertexRefName = "GraphVertexRef";
    private const string GraphVertexRefNamespace = "Quiver.Api";

    // ロール宣言が矛盾しているケースを利用者へ報告する診断。
    private static readonly DiagnosticDescriptor RoleAndProperty = new(
        id: "QVRHE001",
        title: "Role and property on the same member",
        messageFormat: "'{0}' cannot be both a nexus role and a property; remove either [Role] or [Property]",
        category: "Quiver.Nexus",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RoleNotVertexRef = new(
        id: "QVRHE002",
        title: "Role must bind a vertex reference",
        messageFormat: "Role '{0}' must be typed as GraphVertexRef<TVertex> or IReadOnlyList<GraphVertexRef<TVertex>>",
        category: "Quiver.Nexus",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MemberNotWritable = new(
        id: "QVRHE003",
        title: "Role or property must be writable",
        messageFormat: "'{0}' must have an accessible setter so it can be loaded from the database",
        category: "Quiver.Nexus",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>生成パイプラインを登録する (<see cref="IIncrementalGenerator"/> 実装)。</summary>
    public void Initialize(IncrementalGeneratorInitializationContext ctx)
    {
        var provider = ctx.SyntaxProvider
            .ForAttributeWithMetadataName(
                NexusAttributeFqn,
                predicate: static (n, _) => n is ClassDeclarationSyntax,
                transform: static (ctx, _) => Build(ctx))
            .Where(static r => r.Model is not null);

        ctx.RegisterSourceOutput(provider, static (spc, result) =>
        {
            foreach (var diag in result.Diagnostics)
                spc.ReportDiagnostic(diag);
            spc.AddSource(
                $"{result.Model!.ClassName}.GraphNexus.g.cs",
                GraphNexusEmitter.Emit(result.Model));
        });
    }

    private readonly struct BuildResult
    {
        public BuildResult(GraphNexusModel? model, List<Diagnostic> diagnostics)
        {
            Model = model;
            Diagnostics = diagnostics;
        }

        public GraphNexusModel? Model { get; }
        public List<Diagnostic> Diagnostics { get; }
    }

    private static BuildResult Build(GeneratorAttributeSyntaxContext ctx)
    {
        var diagnostics = new List<Diagnostic>();

        if (ctx.TargetSymbol is not INamedTypeSymbol classSymbol)
            return new BuildResult(null, diagnostics);

        var nexusAttr = ctx.Attributes[0];
        string type = classSymbol.Name;
        if (nexusAttr.ConstructorArguments.Length > 0 &&
            nexusAttr.ConstructorArguments[0].Value is string typeArg &&
            !string.IsNullOrEmpty(typeArg))
        {
            type = typeArg;
        }

        var model = new GraphNexusModel
        {
            Namespace = classSymbol.ContainingNamespace.IsGlobalNamespace
                ? ""
                : classSymbol.ContainingNamespace.ToDisplayString(),
            ClassName = classSymbol.Name,
            NexusType = type,
            IsPublic = classSymbol.DeclaredAccessibility == Accessibility.Public,
        };

        var fqnFormat = SymbolDisplayFormat.FullyQualifiedFormat;

        foreach (var member in classSymbol.GetMembers())
        {
            if (member is not IPropertySymbol prop)
                continue;

            bool hasRole = false;
            bool hasProperty = false;
            string? roleName = null;
            string? propertyKey = null;

            foreach (var attr in prop.GetAttributes())
            {
                var attrFqn = attr.AttributeClass?.ToDisplayString();
                if (attrFqn == RoleAttributeFqn)
                {
                    hasRole = true;
                    roleName = prop.Name;
                    if (attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string { Length: > 0 } roleArg)
                    {
                        roleName = roleArg;
                    }
                }
                else if (attrFqn == PropertyAttributeFqn)
                {
                    hasProperty = true;
                    propertyKey = prop.Name;
                    if (attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string { Length: > 0 } keyArg)
                    {
                        propertyKey = keyArg;
                    }
                }
            }

            if (!hasRole && !hasProperty)
                continue;

            // [Role] と [Property] の同時指定は解決不能な宣言。
            if (hasRole && hasProperty)
            {
                diagnostics.Add(Diagnostic.Create(RoleAndProperty, Location(prop), prop.Name));
                continue;
            }

            // ロード時に代入するため、いずれのメンバーも setter が必要。
            if (prop.SetMethod is null)
            {
                diagnostics.Add(Diagnostic.Create(MemberNotWritable, Location(prop), prop.Name));
                continue;
            }

            if (hasRole)
            {
                if (!TryResolveRole(prop.Type, fqnFormat, out var vertexFqn, out bool isMulti, out bool isOptional))
                {
                    diagnostics.Add(Diagnostic.Create(RoleNotVertexRef, Location(prop), roleName));
                    continue;
                }

                model.Roles.Add(new RoleModel
                {
                    PropertyName = prop.Name,
                    RoleName = roleName!,
                    VertexFqn = vertexFqn,
                    IsMultiValued = isMulti,
                    IsOptional = isOptional,
                });
            }
            else
            {
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
                    GraphKey = propertyKey!,
                    CSharpType = typeName,
                    IsMultiValued = isMultiValued,
                });
            }
        }

        return new BuildResult(model, diagnostics);
    }

    /// <summary>
    /// ロールプロパティの宣言型が <c>GraphVertexRef&lt;TVertex&gt;</c> か
    /// <c>IReadOnlyList&lt;GraphVertexRef&lt;TVertex&gt;&gt;</c> かを判定し、束縛先Vertex型の
    /// 完全修飾名を取り出す。単一・複数・nullable を区別する。
    /// </summary>
    private static bool TryResolveRole(
        ITypeSymbol type,
        SymbolDisplayFormat fqnFormat,
        out string vertexFqn,
        out bool isMultiValued,
        out bool isOptional)
    {
        vertexFqn = "";
        isMultiValued = false;
        isOptional = false;

        // nullable value type (GraphVertexRef<T>?) — 省略可能な単一ロール。
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            && nullable.TypeArguments.Length == 1)
        {
            isOptional = true;
            type = nullable.TypeArguments[0];
        }

        if (type is not INamedTypeSymbol { IsGenericType: true } named)
            return false;

        // IReadOnlyList<GraphVertexRef<TVertex>> — 複数メンバー。要素が GraphVertexRef<TVertex>
        // であることで最終的に検証するため、コレクション種別は名前のみで判定する。
        if (named.TypeArguments.Length == 1
            && named.OriginalDefinition.Name is "IReadOnlyList" or "IList" or "List" or "IEnumerable" or "ICollection")
        {
            isMultiValued = true;
            if (named.TypeArguments[0] is not INamedTypeSymbol { IsGenericType: true } element)
                return false;
            named = element;
        }

        // GraphVertexRef<TVertex> — 単一メンバー。
        if (named.OriginalDefinition.Name != GraphVertexRefName
            || named.OriginalDefinition.ContainingNamespace?.ToDisplayString() != GraphVertexRefNamespace
            || named.TypeArguments.Length != 1)
        {
            return false;
        }

        vertexFqn = named.TypeArguments[0].ToDisplayString(fqnFormat);
        return true;
    }

    private static Location Location(ISymbol symbol) =>
        symbol.Locations.Length > 0 ? symbol.Locations[0] : Microsoft.CodeAnalysis.Location.None;
}
