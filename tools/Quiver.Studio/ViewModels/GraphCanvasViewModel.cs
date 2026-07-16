using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Quiver.Core;
using Quiver.Studio.Models;
using Quiver.Studio.Rendering;
using Quiver.Studio.Services;

namespace Quiver.Studio.ViewModels;

public sealed partial class GraphCanvasViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private readonly GraphLayoutService _forceLayout;
    private readonly SugiyamaLayoutService _hierarchyLayout;
    internal readonly GraphEditingService? _editingService;
    internal readonly ILogger _logger;

    [ObservableProperty]
    private bool _hasGraph;

    [ObservableProperty]
    private VisualVertex? _selectedVertex;

    [ObservableProperty]
    private VisualEdge? _selectedEdge;

    [ObservableProperty]
    private int _vertexCount;

    [ObservableProperty]
    private int _edgeCount;

    [ObservableProperty]
    private double _zoomLevel = 1.0;

    [ObservableProperty]
    private bool _isHierarchicalLayout;

    [ObservableProperty]
    private bool _isLinkMode;

    [ObservableProperty]
    private VisualVertex? _linkSource;

    public double LinkCursorX { get; set; }
    public double LinkCursorY { get; set; }
    public double LastContextWorldX { get; set; }
    public double LastContextWorldY { get; set; }

    public List<VisualVertex> Vertices { get; } = [];
    public List<VisualEdge> Edges { get; } = [];
    public GraphRenderer Renderer { get; } = new();

    public event Action? GraphChanged;
    public event Func<Task>? AddVertexRequested;
    public event Func<VisualVertex, VisualVertex, Task>? LinkCompleted;

    internal async Task InvokeAddVertexRequested()
    {
        if (AddVertexRequested is not null)
            await AddVertexRequested.Invoke();
    }

    public GraphCanvasViewModel(
        DatabaseService databaseService,
        GraphLayoutService layoutService,
        SugiyamaLayoutService hierarchyLayoutService,
        GraphEditingService? editingService,
        ILogger logger)
    {
        _db = databaseService;
        _forceLayout = layoutService;
        _hierarchyLayout = hierarchyLayoutService;
        _editingService = editingService;
        _logger = logger;
    }

    partial void OnIsHierarchicalLayoutChanged(bool value)
    {
        if (Vertices.Count > 0)
        {
            ApplyLayout();
            GraphChanged?.Invoke();
        }
    }

    private void ApplyLayout()
    {
        if (IsHierarchicalLayout)
            _hierarchyLayout.Layout(Vertices, Edges);
        else
            _forceLayout.Layout(Vertices, Edges);
    }

    public void BuildFromResult(QueryResult result)
    {
        Vertices.Clear();
        Edges.Clear();
        SelectedVertex = null;

        if (!result.HasGraphData || _db.CurrentDatabase is null)
        {
            HasGraph = false;
            VertexCount = 0;
            EdgeCount = 0;
            GraphChanged?.Invoke();
            return;
        }

        using var tx = _db.CurrentDatabase.BeginReadOnlyTransaction();

        var vertexMap = new Dictionary<long, VisualVertex>();

        foreach (var nid in result.ExtractedVertexIds)
        {
            if (vertexMap.ContainsKey(nid.Sequence)) continue;
            if (!tx.VertexExists(nid)) continue;

            var label = tx.GetVertexLabel(nid) ?? $"({nid.Sequence})";
            vertexMap[nid.Sequence] = new VisualVertex(nid, label);
        }

        var edgeSet = new HashSet<long>();
        foreach (var (_, vn) in vertexMap)
        {
            var edges = tx.EnumerateEdges(vn.Id);
            while (edges.MoveNext())
            {
                var edge = edges.Current;
                if (!edgeSet.Add(edge.Id.Sequence)) continue;
                if (!vertexMap.TryGetValue(edge.Source.Sequence, out var sourceVn)) continue;
                if (!vertexMap.TryGetValue(edge.Target.Sequence, out var targetVn)) continue;

                var typeName = tx.GetEdgeTypeName(edge.Type) ?? edge.Type.Value.ToString();
                Edges.Add(new VisualEdge(edge.Id, sourceVn, targetVn, typeName));
            }
        }

        Vertices.AddRange(vertexMap.Values);
        VertexCount = Vertices.Count;
        EdgeCount = Edges.Count;

        if (result.VectorScores is { Count: > 0 } scores)
        {
            var min = float.MaxValue;
            var max = float.MinValue;
            foreach (var (_, score) in scores)
            {
                if (score < min) min = score;
                if (score > max) max = score;
            }
            var range = max - min;

            foreach (var vn in Vertices)
            {
                if (scores.TryGetValue(vn.Id.Sequence, out var s))
                {
                    vn.VectorScore = s;
                    vn.NormalizedScore = range > 0 ? (s - min) / range : 1f;
                }
            }
        }

        _logger.LogInformation("グラフ構築: Vertices={Vertices} Edges={Edges}", VertexCount, EdgeCount);

        if (Vertices.Count > 0)
        {
            ApplyLayout();
            HasGraph = true;
        }
        else
        {
            HasGraph = false;
        }

        GraphChanged?.Invoke();
    }

    public void SelectVertex(VisualVertex? vertex)
    {
        if (SelectedVertex is not null)
            SelectedVertex.IsSelected = false;
        if (SelectedEdge is not null)
            SelectedEdge.IsSelected = false;

        SelectedVertex = vertex;
        SelectedEdge = null;

        if (vertex is not null)
            vertex.IsSelected = true;
    }

    public void SelectEdge(VisualEdge? edge)
    {
        if (SelectedVertex is not null)
            SelectedVertex.IsSelected = false;
        if (SelectedEdge is not null)
            SelectedEdge.IsSelected = false;

        SelectedVertex = null;
        SelectedEdge = edge;

        if (edge is not null)
            edge.IsSelected = true;
    }

    public void BeginLinkMode(VisualVertex source)
    {
        LinkSource = source;
        IsLinkMode = true;
    }

    public void CancelLinkMode()
    {
        IsLinkMode = false;
        LinkSource = null;
    }

    public async Task CompleteLinkAsync(VisualVertex target)
    {
        if (LinkSource is null || LinkSource == target) { CancelLinkMode(); return; }
        var source = LinkSource;
        CancelLinkMode();
        if (LinkCompleted is not null)
            await LinkCompleted.Invoke(source, target);
    }

    public void AddVertexToGraph(VertexId id, string label, double worldX, double worldY)
    {
        var vn = new VisualVertex(id, label) { X = worldX, Y = worldY, IsPinned = true };
        Vertices.Add(vn);
        VertexCount = Vertices.Count;
        HasGraph = true;
        GraphChanged?.Invoke();
    }

    public void AddEdgeToGraph(EdgeId id, VisualVertex source, VisualVertex target, string type)
    {
        Edges.Add(new VisualEdge(id, source, target, type));
        EdgeCount = Edges.Count;
        GraphChanged?.Invoke();
    }

    public void RemoveVertexFromGraph(VisualVertex vertex)
    {
        Edges.RemoveAll(e => e.Source == vertex || e.Target == vertex);
        Vertices.Remove(vertex);
        if (SelectedVertex == vertex) SelectVertex(null);
        VertexCount = Vertices.Count;
        EdgeCount = Edges.Count;
        HasGraph = Vertices.Count > 0;
        GraphChanged?.Invoke();
    }

    public void RemoveEdgeFromGraph(VisualEdge edge)
    {
        Edges.Remove(edge);
        if (SelectedEdge == edge) SelectEdge(null);
        EdgeCount = Edges.Count;
        GraphChanged?.Invoke();
    }

    public void ApplyVisualSettings(GraphVisualSettings settings)
    {
        Renderer.VisualSettings = settings;
        GraphChanged?.Invoke();
    }

    public VisualVertex? FindVertexById(VertexId id) => Vertices.Find(n => n.Id == id);

    public string ExportSvg()
        => Export.SvgExporter.Export(Vertices, Edges, Renderer.IsDarkTheme);

    public void ExportPng(string outputPath)
        => Export.PngExporter.Export(Vertices, Edges, Renderer.IsDarkTheme, Renderer.VisualSettings, outputPath);

    public event Func<string, Task>? ExportSvgRequested;
    public event Func<string, Task>? ExportPngRequested;

    internal async Task InvokeExportSvgAsync()
    {
        if (ExportSvgRequested is not null)
            await ExportSvgRequested.Invoke("svg");
    }

    internal async Task InvokeExportPngAsync()
    {
        if (ExportPngRequested is not null)
            await ExportPngRequested.Invoke("png");
    }
}
