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
    private readonly GraphLayoutService _layout;
    private readonly ILogger _logger;

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

    public List<VisualNode> Nodes { get; } = [];
    public List<VisualEdge> Edges { get; } = [];
    public GraphRenderer Renderer { get; } = new();

    public event Action? GraphChanged;

    public GraphCanvasViewModel(DatabaseService databaseService, GraphLayoutService layoutService, ILogger logger)
    {
        _db = databaseService;
        _layout = layoutService;
        _logger = logger;
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

        _logger.LogInformation("グラフ構築: Nodes={Nodes} Edges={Edges}", NodeCount, EdgeCount);

        if (Nodes.Count > 0)
        {
            _layout.Layout(Nodes, Edges);
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
}
