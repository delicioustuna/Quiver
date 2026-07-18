using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Studio.Models;
using Quiver.Studio.Services;

namespace Quiver.Studio.ViewModels;

public enum InspectorMode { None, Vertex, Edge, CreateVertex, CreateEdge }

public sealed partial class PropertyInspectorViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private readonly GraphEditingService _editing;
    private readonly ILogger _logger;

    private VertexId _currentVertexId;
    private EdgeId _currentEdgeId;

    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private ObservableCollection<EditablePropertyEntry> _properties = [];

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private InspectorMode _mode;

    [ObservableProperty]
    private string _newPropertyKey = string.Empty;

    [ObservableProperty]
    private string _newPropertyValue = string.Empty;

    [ObservableProperty]
    private string _createLabel = string.Empty;

    [ObservableProperty]
    private string _createEdgeType = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> _labelSuggestions = [];

    [ObservableProperty]
    private IReadOnlyList<string> _edgeTypeSuggestions = [];

    [ObservableProperty]
    private ObservableCollection<EditablePropertyEntry> _createProperties = [];

    [ObservableProperty]
    private string _createNewKey = string.Empty;

    [ObservableProperty]
    private string _createNewValue = string.Empty;

    public bool IsEditing => Mode is InspectorMode.Vertex or InspectorMode.Edge;
    public bool IsCreating => Mode is InspectorMode.CreateVertex or InspectorMode.CreateEdge;
    public bool IsCreateVertex => Mode == InspectorMode.CreateVertex;
    public bool IsCreateEdge => Mode == InspectorMode.CreateEdge;

    public event Action<VertexId, string, double, double>? VertexCreated;
    public event Action<EdgeId, VisualVertex, VisualVertex, string>? EdgeCreated;
    public event Action? CreationCancelled;

    private double _createWorldX, _createWorldY;
    private VisualVertex? _linkSource, _linkTarget;

    public PropertyInspectorViewModel(DatabaseService databaseService, GraphEditingService editingService, ILogger logger)
    {
        _db = databaseService;
        _editing = editingService;
        _logger = logger;
    }

    public void InspectVertex(VisualVertex vertex)
    {
        if (_db.CurrentDatabase is null) { Clear(); return; }

        var schema = _db.CurrentDatabase.Schema;
        using var tx = _db.CurrentDatabase.BeginReadTransaction();
        if (!tx.VertexExists(vertex.Id)) { Clear(); return; }

        _currentVertexId = vertex.Id;
        Header = $"Vertex #{vertex.Id.Sequence} ({vertex.Label})";
        Mode = InspectorMode.Vertex;

        var keyMap = BuildPropertyKeyMap(schema);
        var entries = new ObservableCollection<EditablePropertyEntry>();

        var propEnum = tx.EnumerateProperties(vertex.Id);
        while (propEnum.MoveNext())
        {
            var keyName = keyMap.TryGetValue(propEnum.Current.KeyId, out var name) ? name : $"key#{propEnum.Current.KeyId.Value}";
            entries.Add(new EditablePropertyEntry(keyName, MaterializeValue(propEnum.Current.Value)));
        }

        Properties = entries;
        HasSelection = true;
        NewPropertyKey = string.Empty;
        NewPropertyValue = string.Empty;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(IsCreating));
        OnPropertyChanged(nameof(IsCreateVertex));
        OnPropertyChanged(nameof(IsCreateEdge));
    }

    public void InspectEdge(VisualEdge edge)
    {
        if (_db.CurrentDatabase is null) { Clear(); return; }

        var schema = _db.CurrentDatabase.Schema;
        using var tx = _db.CurrentDatabase.BeginReadTransaction();

        _currentEdgeId = edge.Id;
        Header = $"Edge #{edge.Id.Sequence} (:{edge.EdgeType})";
        Mode = InspectorMode.Edge;

        var entries = new ObservableCollection<EditablePropertyEntry>();

        foreach (var keyName in schema.ListPropertyKeys())
        {
            var cardinality = GetCardinality(schema, keyName);
            if (cardinality == PropertyCardinality.Set)
            {
                var values = tx.GetPropertyValues(edge.Id, keyName);
                var sb = new StringBuilder("[");
                var first = true;
                while (values.MoveNext())
                {
                    if (!first) sb.Append(", ");
                    sb.Append(MaterializeValue(values.Current));
                    first = false;
                }
                sb.Append(']');
                if (!first)
                    entries.Add(new EditablePropertyEntry(keyName, sb.ToString()));
            }
            else
            {
                var val = tx.GetProperty(edge.Id, keyName);
                if (val.Type != 0)
                    entries.Add(new EditablePropertyEntry(keyName, MaterializeValue(val)));
            }
        }

        Properties = entries;
        HasSelection = true;
        NewPropertyKey = string.Empty;
        NewPropertyValue = string.Empty;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(IsCreating));
        OnPropertyChanged(nameof(IsCreateVertex));
        OnPropertyChanged(nameof(IsCreateEdge));
    }

    public void BeginCreateVertex(double worldX, double worldY)
    {
        _createWorldX = worldX;
        _createWorldY = worldY;

        var labels = _db.CurrentDatabase?.Schema.ListLabels() ?? [];
        LabelSuggestions = labels;

        Header = "Vertexの作成";
        Mode = InspectorMode.CreateVertex;
        HasSelection = true;
        CreateLabel = string.Empty;
        CreateProperties = [];
        CreateNewKey = string.Empty;
        CreateNewValue = string.Empty;
        Properties = [];
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(IsCreating));
        OnPropertyChanged(nameof(IsCreateVertex));
        OnPropertyChanged(nameof(IsCreateEdge));
    }

    public void BeginCreateEdge(VisualVertex source, VisualVertex target)
    {
        _linkSource = source;
        _linkTarget = target;

        var types = _db.CurrentDatabase?.Schema.ListEdgeTypes() ?? [];
        EdgeTypeSuggestions = types;

        Header = $"Edgeの作成 ({source.Label} → {target.Label})";
        Mode = InspectorMode.CreateEdge;
        HasSelection = true;
        CreateEdgeType = string.Empty;
        CreateProperties = [];
        CreateNewKey = string.Empty;
        CreateNewValue = string.Empty;
        Properties = [];
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(IsCreating));
        OnPropertyChanged(nameof(IsCreateVertex));
        OnPropertyChanged(nameof(IsCreateEdge));
    }

    [RelayCommand]
    private void AddProperty()
    {
        var key = NewPropertyKey.Trim();
        var value = NewPropertyValue.Trim();
        if (string.IsNullOrEmpty(key)) return;

        try
        {
            if (Mode == InspectorMode.Vertex)
            {
                _editing.SetProperty(_currentVertexId, key, value);
                Properties.Add(new EditablePropertyEntry(key, value));
            }
            else if (Mode == InspectorMode.Edge)
            {
                _editing.SetEdgeProperty(_currentEdgeId, key, value);
                Properties.Add(new EditablePropertyEntry(key, value));
            }
            NewPropertyKey = string.Empty;
            NewPropertyValue = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "プロパティ追加失敗");
        }
    }

    [RelayCommand]
    private void RemoveProperty(EditablePropertyEntry entry)
    {
        if (Mode != InspectorMode.Vertex) return;
        try
        {
            _editing.RemoveProperty(_currentVertexId, entry.Key);
            Properties.Remove(entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "プロパティ削除失敗");
        }
    }

    [RelayCommand]
    private void AddCreateProperty()
    {
        var key = CreateNewKey.Trim();
        var value = CreateNewValue.Trim();
        if (string.IsNullOrEmpty(key)) return;
        CreateProperties.Add(new EditablePropertyEntry(key, value));
        CreateNewKey = string.Empty;
        CreateNewValue = string.Empty;
    }

    [RelayCommand]
    private void RemoveCreateProperty(EditablePropertyEntry entry)
    {
        CreateProperties.Remove(entry);
    }

    [RelayCommand]
    private void ConfirmCreate()
    {
        try
        {
            if (Mode == InspectorMode.CreateVertex)
            {
                var label = CreateLabel.Trim();
                if (string.IsNullOrEmpty(label)) return;

                var props = CreateProperties.Count > 0
                    ? CreateProperties.Select(p => (p.Key, p.Value)).ToList()
                    : null;
                var nid = _editing.CreateVertex(label, props);
                VertexCreated?.Invoke(nid, label, _createWorldX, _createWorldY);
            }
            else if (Mode == InspectorMode.CreateEdge && _linkSource is not null && _linkTarget is not null)
            {
                var type = CreateEdgeType.Trim();
                if (string.IsNullOrEmpty(type)) return;

                var rid = _editing.CreateEdge(_linkSource.Id, _linkTarget.Id, type);

                foreach (var prop in CreateProperties)
                    _editing.SetEdgeProperty(rid, prop.Key, prop.Value);

                EdgeCreated?.Invoke(rid, _linkSource, _linkTarget, type);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "作成失敗");
        }

        Clear();
    }

    [RelayCommand]
    private void CancelCreate()
    {
        CreationCancelled?.Invoke();
        Clear();
    }

    public void Clear()
    {
        Header = string.Empty;
        Properties = [];
        HasSelection = false;
        Mode = InspectorMode.None;
        _linkSource = null;
        _linkTarget = null;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(IsCreating));
        OnPropertyChanged(nameof(IsCreateVertex));
        OnPropertyChanged(nameof(IsCreateEdge));
    }

    private static Dictionary<PropertyKeyId, string> BuildPropertyKeyMap(ISchemaCatalog schema)
    {
        var map = new Dictionary<PropertyKeyId, string>();
        foreach (var name in schema.ListPropertyKeys())
        {
            if (schema.TryGetPropertyKeyId(name, out var id))
                map[id] = name;
        }
        return map;
    }

    private static PropertyCardinality GetCardinality(ISchemaCatalog schema, string keyName)
    {
        if (!schema.TryGetPropertyKeyId(keyName, out var id))
            return PropertyCardinality.Single;
        return schema.GetPropertyKeyCardinality(id);
    }

    private static string MaterializeValue(in PropertyValue value) => value.Type switch
    {
        PropertyValueType.Bool => value.BoolValue.ToString(),
        PropertyValueType.Int32 => value.Int32Value.ToString(),
        PropertyValueType.Int64 => value.Int64Value.ToString(),
        PropertyValueType.Double => value.DoubleValue.ToString("G"),
        PropertyValueType.String => Encoding.UTF8.GetString(value.Utf8StringValue),
        PropertyValueType.Bytes => $"[{value.BytesValue.Length} bytes]",
        PropertyValueType.FloatArray => $"float[{value.FloatArrayValue.Length}]",
        _ => "(null)",
    };
}

public sealed class EditablePropertyEntry(string key, string value)
{
    public string Key { get; } = key;
    public string Value { get; set; } = value;
}
