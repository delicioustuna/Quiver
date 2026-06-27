using System.Collections.Immutable;
using System.Composition.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Quiver.Studio.Services;

public sealed class IntellisenseService : IDisposable
{
    private readonly ILogger<IntellisenseService> _logger;
    private AdhocWorkspace? _workspace;
    private ProjectId? _projectId;

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
                BuildWorkspace();
                _logger.LogInformation("IntelliSense ワークスペース構築完了");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IntelliSense ワークスペース構築失敗");
            }
        });
    }

    public async Task<IReadOnlyList<CompletionEntry>> GetCompletionsAsync(
        string code, int caretPosition, CancellationToken ct)
    {
        if (_workspace is null || _projectId is null)
            return [];

        var wrappedCode = Preamble + code + "\n}}";
        var adjustedPosition = Preamble.Length + caretPosition;

        if (adjustedPosition < 0 || adjustedPosition > wrappedCode.Length)
            return [];

        var documentId = DocumentId.CreateNewId(_projectId);
        var solution = _workspace.CurrentSolution.AddDocument(
            documentId, "__Query__.cs", SourceText.From(wrappedCode));
        var document = solution.GetDocument(documentId);
        if (document is null)
            return [];

        try
        {
            var completionService = CompletionService.GetService(document);
            if (completionService is null)
                return [];

            var completions = await completionService.GetCompletionsAsync(
                document, adjustedPosition, cancellationToken: ct);
            if (completions is null)
                return [];

            var results = new List<CompletionEntry>(completions.ItemsList.Count);
            foreach (var item in completions.ItemsList)
            {
                ct.ThrowIfCancellationRequested();

                var capturedItem = item;
                var capturedDoc = document;
                var capturedSvc = completionService;

                results.Add(new CompletionEntry(
                    item.DisplayText,
                    item.FilterText,
                    item.SortText,
                    GetGlyphKind(item.Tags),
                    async descCt =>
                    {
                        var desc = await capturedSvc.GetDescriptionAsync(
                            capturedDoc, capturedItem, descCt);
                        if (desc is null) return null;
                        return string.Join("", desc.TaggedParts.Select(p => p.Text));
                    }));
            }

            return results;
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

    private void BuildWorkspace()
    {
        var assemblies = MefHostServices.DefaultAssemblies;
        var compositionContext = new ContainerConfiguration()
            .WithAssemblies(assemblies)
            .CreateContainer();
        var hostServices = MefHostServices.Create(compositionContext);

        _workspace?.Dispose();
        _workspace = new AdhocWorkspace(hostServices);

        _projectId = ProjectId.CreateNewId();

        var metadataReferences = CollectMetadataReferences();

        var projectInfo = ProjectInfo.Create(
            _projectId,
            VersionStamp.Default,
            "QuiverQuery",
            "QuiverQuery",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: metadataReferences);

        _workspace.AddProject(projectInfo);
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
            try
            {
                refs.Add(MetadataReference.CreateFromFile(asm.Location));
            }
            catch
            {
            }
        }

        AddReferenceIfMissing(refs, seen, typeof(object));
        AddReferenceIfMissing(refs, seen, typeof(Enumerable));
        AddReferenceIfMissing(refs, seen, typeof(GraphDatabase));

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
        sb.AppendLine("    GraphDatabase db = null!;");
        sb.AppendLine("    IGraphTransaction tx = null!;");
        sb.AppendLine("    GraphTraversalSource g = null!;");
        sb.AppendLine("    ISchemaApi schema = null!;");
        sb.AppendLine("    void __Run__() {");

        return sb.ToString();
    }

    private static CompletionGlyph GetGlyphKind(ImmutableArray<string> tags)
    {
        foreach (var tag in tags)
        {
            switch (tag)
            {
                case "Method": return CompletionGlyph.Method;
                case "Property": return CompletionGlyph.Property;
                case "Field": return CompletionGlyph.Field;
                case "Event": return CompletionGlyph.Event;
                case "Class": return CompletionGlyph.Class;
                case "Struct": case "Structure": return CompletionGlyph.Struct;
                case "Interface": return CompletionGlyph.Interface;
                case "Enum": return CompletionGlyph.Enum;
                case "EnumMember": return CompletionGlyph.EnumMember;
                case "Delegate": return CompletionGlyph.Delegate;
                case "Namespace": return CompletionGlyph.Namespace;
                case "Keyword": return CompletionGlyph.Keyword;
                case "Local": case "Parameter": return CompletionGlyph.Local;
                case "Constant": return CompletionGlyph.Constant;
                case "ExtensionMethod": return CompletionGlyph.ExtensionMethod;
            }
        }
        return CompletionGlyph.Other;
    }

    public void Dispose()
    {
        _workspace?.Dispose();
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
