using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Studio.Models;
using Quiver.Studio.Services;

namespace Quiver.Studio.ViewModels;

public sealed partial class PropertyInspectorViewModel : ObservableObject
{
    private readonly DatabaseService _db;

    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private List<PropertyEntry> _properties = [];

    [ObservableProperty]
    private bool _hasSelection;

    public PropertyInspectorViewModel(DatabaseService databaseService)
    {
        _db = databaseService;
    }

    public void InspectNode(VisualNode node)
    {
        if (_db.CurrentDatabase is null)
        {
            Clear();
            return;
        }

        var schema = _db.CurrentDatabase.Schema;
        using var tx = _db.CurrentDatabase.BeginReadOnlyTransaction();
        if (!tx.NodeExists(node.Id))
        {
            Clear();
            return;
        }

        Header = $"Node #{node.Id.Sequence} ({node.Label})";

        var keyMap = BuildPropertyKeyMap(schema);
        var entries = new List<PropertyEntry>();

        var propEnum = tx.EnumerateProperties(node.Id);
        while (propEnum.MoveNext())
        {
            var keyName = keyMap.TryGetValue(propEnum.Current.KeyId, out var name) ? name : $"key#{propEnum.Current.KeyId.Value}";
            entries.Add(new PropertyEntry(keyName, MaterializeValue(propEnum.Current.Value)));
        }

        Properties = entries;
        HasSelection = true;
    }

    public void InspectEdge(VisualEdge edge)
    {
        if (_db.CurrentDatabase is null)
        {
            Clear();
            return;
        }

        var schema = _db.CurrentDatabase.Schema;
        using var tx = _db.CurrentDatabase.BeginReadOnlyTransaction();

        Header = $"Relationship #{edge.Id.Sequence} (:{edge.RelationshipType})";

        var entries = new List<PropertyEntry>();

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
                    entries.Add(new PropertyEntry(keyName, sb.ToString()));
            }
            else
            {
                var val = tx.GetProperty(edge.Id, keyName);
                if (val.Type != 0)
                    entries.Add(new PropertyEntry(keyName, MaterializeValue(val)));
            }
        }

        Properties = entries;
        HasSelection = true;
    }

    public void Clear()
    {
        Header = string.Empty;
        Properties = [];
        HasSelection = false;
    }

    private static Dictionary<PropertyKeyId, string> BuildPropertyKeyMap(ISchemaApi schema)
    {
        var map = new Dictionary<PropertyKeyId, string>();
        foreach (var name in schema.ListPropertyKeys())
        {
            if (schema.TryGetPropertyKeyId(name, out var id))
                map[id] = name;
        }
        return map;
    }

    private static PropertyCardinality GetCardinality(ISchemaApi schema, string keyName)
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

public sealed record PropertyEntry(string Key, string Value);
