using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace Quiver.Studio.Services;

public sealed class IntellisenseService : IDisposable
{
    private readonly ILogger<IntellisenseService> _logger;
    private IReadOnlyList<MetadataReference>? _metadataReferences;
    private CSharpParseOptions? _parseOptions;
    private Dictionary<string, string>? _xmlDocs;
    private Dictionary<string, XElement>? _xmlDocElements;

    private static readonly string[] Usings =
    [
        "System",
        "System.Linq",
        "System.Collections.Generic",
        "Quiver",
        "Quiver.Core",
        "Quiver.Api",
        "Quiver.Transactions",
        "Quiver.Storage.Records",
    ];

    private static readonly string Preamble = BuildPreamble();

    public IntellisenseService(ILogger<IntellisenseService> logger)
    {
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                _metadataReferences = CollectMetadataReferences();
                _parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
                (_xmlDocs, _xmlDocElements) = LoadXmlDocs();
                _logger.LogInformation("IntelliSense 初期化完了");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IntelliSense 初期化失敗");
            }
        });
    }

    public async Task<IReadOnlyList<CompletionEntry>> GetCompletionsAsync(
        string code, int caretPosition, CancellationToken ct)
    {
        if (_metadataReferences is null)
            return [];

        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                var wrappedCode = Preamble + code + "\n}}";
                var adjustedPosition = Preamble.Length + caretPosition;

                if (adjustedPosition < 0 || adjustedPosition > wrappedCode.Length)
                    return (IReadOnlyList<CompletionEntry>)[];

                var tree = CSharpSyntaxTree.ParseText(wrappedCode, _parseOptions);
                var compilation = CSharpCompilation.Create("Query",
                    [tree],
                    _metadataReferences,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                        .WithNullableContextOptions(NullableContextOptions.Enable));

                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot(ct);

                if (IsDotCompletion(root, adjustedPosition, out var exprBeforeDot))
                {
                    var typeInfo = model.GetTypeInfo(exprBeforeDot!, ct);
                    var type = typeInfo.Type;
                    if (type is null or IErrorTypeSymbol)
                        return (IReadOnlyList<CompletionEntry>)[];

                    var members = model.LookupSymbols(adjustedPosition, type);
                    return BuildEntries(members.Where(s => s.CanBeReferencedByName));
                }
                else
                {
                    var symbols = model.LookupSymbols(adjustedPosition);
                    return BuildEntries(symbols.Where(s => s.CanBeReferencedByName));
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "補完取得失敗");
            return [];
        }
    }

    private static bool IsDotCompletion(
        SyntaxNode root, int position, out ExpressionSyntax? expression)
    {
        expression = null;
        if (position <= 0)
            return false;

        var tokenBefore = root.FindToken(position - 1);
        if (tokenBefore.IsKind(SyntaxKind.DotToken) &&
            tokenBefore.Parent is MemberAccessExpressionSyntax memberAccess)
        {
            expression = memberAccess.Expression;
            return true;
        }

        var current = root.FindToken(position);
        if (current.Parent is IdentifierNameSyntax { Parent: MemberAccessExpressionSyntax parentAccess } &&
            parentAccess.OperatorToken.Span.Start < position)
        {
            expression = parentAccess.Expression;
            return true;
        }

        return false;
    }

    private IReadOnlyList<CompletionEntry> BuildEntries(IEnumerable<ISymbol> symbols)
    {
        var seen = new HashSet<string>();
        var results = new List<CompletionEntry>();

        foreach (var symbol in symbols)
        {
            var displayText = symbol.Name;
            if (!seen.Add(displayText))
                continue;

            var glyph = GetGlyph(symbol);
            var docId = symbol.GetDocumentationCommentId();

            results.Add(new CompletionEntry(
                displayText,
                displayText,
                displayText,
                glyph,
                docId is not null ? _ => Task.FromResult(GetXmlDocSummary(docId)) : null));
        }

        results.Sort((a, b) =>
            string.Compare(a.DisplayText, b.DisplayText, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    private static CompletionGlyph GetGlyph(ISymbol symbol) => symbol.Kind switch
    {
        SymbolKind.Method => symbol is IMethodSymbol { IsExtensionMethod: true }
            ? CompletionGlyph.ExtensionMethod
            : CompletionGlyph.Method,
        SymbolKind.Property => CompletionGlyph.Property,
        SymbolKind.Field => symbol is IFieldSymbol { IsConst: true }
            ? CompletionGlyph.Constant
            : CompletionGlyph.Field,
        SymbolKind.Event => CompletionGlyph.Event,
        SymbolKind.NamedType when symbol is INamedTypeSymbol nts => nts.TypeKind switch
        {
            TypeKind.Class => CompletionGlyph.Class,
            TypeKind.Struct => CompletionGlyph.Struct,
            TypeKind.Interface => CompletionGlyph.Interface,
            TypeKind.Enum => CompletionGlyph.Enum,
            TypeKind.Delegate => CompletionGlyph.Delegate,
            _ => CompletionGlyph.Class,
        },
        SymbolKind.Namespace => CompletionGlyph.Namespace,
        SymbolKind.Local or SymbolKind.Parameter => CompletionGlyph.Local,
        _ => CompletionGlyph.Other,
    };

    private string? GetXmlDocSummary(string docId)
    {
        if (_xmlDocs is not null && _xmlDocs.TryGetValue(docId, out var summary))
            return summary;
        return null;
    }

    private static List<MetadataReference> CollectMetadataReferences()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location))
                continue;
            if (!seen.Add(asm.Location))
                continue;
            try { refs.Add(MetadataReference.CreateFromFile(asm.Location)); }
            catch { /* skip inaccessible assemblies */ }
        }

        AddReferenceIfMissing(refs, seen, typeof(object));
        AddReferenceIfMissing(refs, seen, typeof(Enumerable));
        AddReferenceIfMissing(refs, seen, typeof(QuiverDatabase));
        return refs;
    }

    private static void AddReferenceIfMissing(
        List<MetadataReference> refs, HashSet<string> seen, Type markerType)
    {
        var location = markerType.Assembly.Location;
        if (!string.IsNullOrEmpty(location) && seen.Add(location))
            refs.Add(MetadataReference.CreateFromFile(location));
    }

    private static string BuildPreamble()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var u in Usings)
            sb.AppendLine($"using {u};");

        sb.AppendLine();
        sb.AppendLine("class __ScriptHost__ {");
        sb.AppendLine("    QuiverDatabase db = null!;");
        sb.AppendLine("    IGraphTransaction tx = null!;");
        sb.AppendLine("    GraphTraversalSource g = null!;");
        sb.AppendLine("    ISchemaApi schema = null!;");
        sb.AppendLine("    void __Run__() {");

        return sb.ToString();
    }

    private static (Dictionary<string, string>?, Dictionary<string, XElement>?) LoadXmlDocs()
    {
        var dllPath = typeof(QuiverDatabase).Assembly.Location;
        if (string.IsNullOrEmpty(dllPath))
            return (null, null);

        var xmlPath = Path.ChangeExtension(dllPath, ".xml");
        if (!File.Exists(xmlPath))
            return (null, null);

        try
        {
            var summaries = new Dictionary<string, string>();
            var elements = new Dictionary<string, XElement>();
            var doc = XDocument.Load(xmlPath);
            foreach (var member in doc.Descendants("member"))
            {
                var name = member.Attribute("name")?.Value;
                if (name is null) continue;
                elements[name] = member;
                var summary = member.Element("summary")?.Value;
                if (summary is not null)
                    summaries[name] = string.Join(' ',
                        summary.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
            }
            return (summaries, elements);
        }
        catch
        {
            return (null, null);
        }
    }

    public async Task<HoverInfo?> GetHoverInfoAsync(string code, int position, CancellationToken ct)
    {
        if (_metadataReferences is null)
            return null;

        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                var wrappedCode = Preamble + code + "\n}}";
                var adjustedPosition = Preamble.Length + position;

                if (adjustedPosition < 0 || adjustedPosition > wrappedCode.Length)
                    return null;

                var tree = CSharpSyntaxTree.ParseText(wrappedCode, _parseOptions);
                var compilation = CSharpCompilation.Create("Query",
                    [tree],
                    _metadataReferences,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                        .WithNullableContextOptions(NullableContextOptions.Enable));

                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot(ct);

                var token = root.FindToken(adjustedPosition);
                var node = token.Parent;

                while (node is not null)
                {
                    var symbolInfo = model.GetSymbolInfo(node, ct);
                    var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
                    if (symbol is not null)
                        return BuildHoverInfo(symbol);

                    var typeInfo = model.GetTypeInfo(node, ct);
                    if (typeInfo.Type is not null and not IErrorTypeSymbol)
                        return BuildHoverInfo(typeInfo.Type);

                    node = node.Parent;
                }

                return null;
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ホバー情報取得失敗");
            return null;
        }
    }

    private HoverInfo? BuildHoverInfo(ISymbol symbol)
    {
        var signature = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var docId = symbol.GetDocumentationCommentId();

        string? summary = null;
        var parameters = new List<(string Name, string Description)>();
        string? returns = null;

        if (docId is not null && _xmlDocElements is not null &&
            _xmlDocElements.TryGetValue(docId, out var element))
        {
            var summaryEl = element.Element("summary");
            if (summaryEl is not null)
                summary = CleanXml(summaryEl);

            foreach (var param in element.Elements("param"))
            {
                var name = param.Attribute("name")?.Value ?? "";
                parameters.Add((name, CleanXml(param)));
            }

            var returnsEl = element.Element("returns");
            if (returnsEl is not null)
                returns = CleanXml(returnsEl);
        }

        return new HoverInfo(signature, summary, parameters, returns);
    }

    private static string CleanXml(XElement element) =>
        string.Join(' ', element.Value.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));

    public async Task<string?> GetDocIdAtPositionAsync(string code, int caretPosition, CancellationToken ct)
    {
        if (_metadataReferences is null)
            return null;

        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                var wrappedCode = Preamble + code + "\n}}";
                var adjustedPosition = Preamble.Length + caretPosition;

                if (adjustedPosition < 0 || adjustedPosition > wrappedCode.Length)
                    return null;

                var tree = CSharpSyntaxTree.ParseText(wrappedCode, _parseOptions);
                var compilation = CSharpCompilation.Create("Query",
                    [tree],
                    _metadataReferences,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                        .WithNullableContextOptions(NullableContextOptions.Enable));

                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot(ct);

                var token = root.FindToken(adjustedPosition);
                var node = token.Parent;

                while (node is not null)
                {
                    var symbolInfo = model.GetSymbolInfo(node, ct);
                    var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
                    if (symbol is not null)
                        return symbol.GetDocumentationCommentId();
                    node = node.Parent;
                }

                return null;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DocId 取得失敗");
            return null;
        }
    }

    public void Dispose()
    {
    }
}

public readonly record struct CompletionEntry(
    string DisplayText,
    string FilterText,
    string SortText,
    CompletionGlyph Glyph,
    Func<CancellationToken, Task<string?>>? DescriptionFactory);

public enum CompletionGlyph
{
    Other,
    Method,
    ExtensionMethod,
    Property,
    Field,
    Event,
    Class,
    Struct,
    Interface,
    Enum,
    EnumMember,
    Delegate,
    Namespace,
    Keyword,
    Local,
    Constant,
}

public sealed record HoverInfo(
    string Signature,
    string? Summary,
    IReadOnlyList<(string Name, string Description)> Parameters,
    string? Returns);
