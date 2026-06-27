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
    private VisualNode? _selectedNode;

    [ObservableProperty]
    private VisualEdge? _selectedEdge;

    [ObservableProperty]
    private int _nodeCount;

    [ObservableProperty]
    private int _edgeCount;

    [ObservableProperty]
    private double _zoomLevel = 1.0;

    [ObservableProperty]
    private bool _isHierarchicalLayout;

    [ObservableProperty]
    private bool _isLinkMode;

    [ObservableProperty]
    private VisualNode? _linkSource;

    public double LinkCursorX { get; set; }
    public double LinkCursorY { get; set; }
    public double LastContextWorldX { get; set; }
    public double LastContextWorldY { get; set; }

    public List<VisualNode> Nodes { get; } = [];
    public List<VisualEdge> Edges { get; } = [];
    public GraphRenderer Renderer { get; } = new();

    public event Action? GraphChanged;
    public event Func<Task>? AddNodeRequested;
    public event Func<VisualNode, VisualNode, Task>? LinkCompleted;

    internal async Task InvokeAddNodeRequested()
    {
        if (AddNodeRequested is not null)
            await AddNodeRequested.Invoke();
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
        if (Nodes.Count > 0)
        {
            ApplyLayout();
            GraphChanged?.Invoke();
        }
    }

    private void ApplyLayout()
    {
        if (IsHierarchicalLayout)
            _hierarchyLayout.Layout(Nodes, Edges);
        else
            _forceLayout.Layout(Nodes, Edges);
    }

    public void BuildFromResult(QueryResult result)
    {
        Nodes.Clear();
        Edges.Clear();
        SelectedNode = null;

        if (!result.HasGraphData || _db.CurrentDatabase is null)
        {
            HasGraph = false;
            NodeCount = 0;
            EdgeCount = 0;
            GraphChanged?.Invoke();
            return;
        }

        using var tx = _db.CurrentDatabase.BeginReadOnlyTransaction();

        var nodeMap = new Dictionary<long, VisualNode>();

        foreach (var nid in result.ExtractedNodeIds)
        {
            if (nodeMap.ContainsKey(nid.Sequence)) continue;
            if (!tx.NodeExists(nid)) continue;

            var label = tx.GetNodeLabel(nid) ?? $"({nid.Sequence})";
            nodeMap[nid.Sequence] = new VisualNode(nid, label);
        }

        var edgeSet = new HashSet<long>();
        foreach (var (_, vn) in nodeMap)
        {
            var rels = tx.EnumerateRelationships(vn.Id);
            while (rels.MoveNext())
            {
                var rel = rels.Current;
                if (!edgeSet.Add(rel.Id.Sequence)) continue;
                if (!nodeMap.TryGetValue(rel.Source.Sequence, out var sourceVn)) continue;
                if (!nodeMap.TryGetValue(rel.Target.Sequence, out var targetVn)) continue;

                var typeName = tx.GetRelationshipTypeName(rel.Type) ?? rel.Type.Value.ToString();
                Edges.Add(new VisualEdge(rel.Id, sourceVn, targetVn, typeName));
            }
        }

        Nodes.AddRange(nodeMap.Values);
        NodeCount = Nodes.Count;
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

            foreach (var vn in Nodes)
            {
                if (scores.TryGetValue(vn.Id.Sequence, out var s))
                {
                    vn.VectorScore = s;
                    vn.NormalizedScore = range > 0 ? (s - min) / range : 1f;
                }
            }
        }

        _logger.LogInformation("グラフ構築: Nodes={Nodes} Edges={Edges}", NodeCount, EdgeCount);

        if (Nodes.Count > 0)
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

    public void SelectNode(VisualNode? node)
    {
        if (SelectedNode is not null)
            SelectedNode.IsSelected = false;
        if (SelectedEdge is not null)
            SelectedEdge.IsSelected = false;

        SelectedNode = node;
        SelectedEdge = null;

        if (node is not null)
            node.IsSelected = true;
    }

    public void SelectEdge(VisualEdge? edge)
    {
        if (SelectedNode is not null)
            SelectedNode.IsSelected = false;
        if (SelectedEdge is not null)
            SelectedEdge.IsSelected = false;

        SelectedNode = null;
        SelectedEdge = edge;

        if (edge is not null)
            edge.IsSelected = true;
    }

    public void BeginLinkMode(VisualNode source)
    {
        LinkSource = source;
        IsLinkMode = true;
    }

    public void CancelLinkMode()
    {
        IsLinkMode = false;
        LinkSource = null;
    }

    public async Task CompleteLinkAsync(VisualNode target)
    {
        if (LinkSource is null || LinkSource == target) { CancelLinkMode(); return; }
        var source = LinkSource;
        CancelLinkMode();
        if (LinkCompleted is not null)
            await LinkCompleted.Invoke(source, target);
    }

    public void AddNodeToGraph(NodeId id, string label, double worldX, double worldY)
    {
        var vn = new VisualNode(id, label) { X = worldX, Y = worldY, IsPinned = true };
        Nodes.Add(vn);
        NodeCount = Nodes.Count;
        HasGraph = true;
        GraphChanged?.Invoke();
    }

    public void AddEdgeToGraph(RelationshipId id, VisualNode source, VisualNode target, string type)
    {
        Edges.Add(new VisualEdge(id, source, target, type));
        EdgeCount = Edges.Count;
        GraphChanged?.Invoke();
    }

    public void RemoveNodeFromGraph(VisualNode node)
    {
        Edges.RemoveAll(e => e.Source == node || e.Target == node);
        Nodes.Remove(node);
        if (SelectedNode == node) SelectNode(null);
        NodeCount = Nodes.Count;
        EdgeCount = Edges.Count;
        HasGraph = Nodes.Count > 0;
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

    public VisualNode? FindNodeById(NodeId id) => Nodes.Find(n => n.Id == id);

    public string ExportSvg()
        => Export.SvgExporter.Export(Nodes, Edges, Renderer.IsDarkTheme);

    public void ExportPng(string outputPath)
        => Export.PngExporter.Export(Nodes, Edges, Renderer.IsDarkTheme, Renderer.VisualSettings, outputPath);

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
