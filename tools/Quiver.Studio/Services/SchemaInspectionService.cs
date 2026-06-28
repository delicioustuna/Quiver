using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Studio.Services;

public sealed class SchemaInspectionService
{
    private readonly DatabaseService _db;
    private const int SampleLimit = 200;

    public SchemaInspectionService(DatabaseService db)
    {
        _db = db;
    }

    public SchemaInspectionResult? Inspect()
    {
        var database = _db.CurrentDatabase;
        if (database is null) return null;

        var schema = database.Schema;
        var labels = schema.ListLabels();
        var relTypes = schema.ListRelationshipTypes();
        var propKeys = schema.ListPropertyKeys();

        var keyIdToName = new Dictionary<PropertyKeyId, string>();
        foreach (var name in propKeys)
        {
            if (schema.TryGetPropertyKeyId(name, out var id))
                keyIdToName[id] = name;
        }

        var labelProperties = new Dictionary<string, List<PropertyInfo>>();
        var relTypeProperties = new Dictionary<string, List<PropertyInfo>>();

        using var tx = database.BeginReadOnlyTransaction();
        var g = tx.G(schema);

        foreach (var label in labels)
        {
            var observed = new Dictionary<PropertyKeyId, PropertyTypeFlags>();
            var nodeIds = g.Nodes().HasLabel(label).Limit(SampleLimit).ToList();

            foreach (var nodeId in nodeIds)
            {
                var props = tx.EnumerateProperties(nodeId);
                while (props.MoveNext())
                {
                    var ph = props.Current;
                    observed.TryGetValue(ph.KeyId, out var flags);
                    observed[ph.KeyId] = flags | ph.Value.Type.ToFlags();
                }
            }

            var list = new List<PropertyInfo>();
            foreach (var (keyId, flags) in observed)
            {
                var keyName = keyIdToName.TryGetValue(keyId, out var n) ? n : $"key#{keyId.Value}";
                list.Add(new PropertyInfo(keyName, FormatType(flags)));
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            labelProperties[label] = list;
        }

        foreach (var relType in relTypes)
        {
            var observed = new Dictionary<string, PropertyTypeFlags>();
            int relSampled = 0;

            foreach (var label in labels)
            {
                if (relSampled >= SampleLimit) break;
                var nodeIds = g.Nodes().HasLabel(label).Limit(SampleLimit).ToList();
                foreach (var nodeId in nodeIds)
                {
                    if (relSampled >= SampleLimit) break;
                    var rels = tx.EnumerateRelationships(nodeId, typeFilter: relType);
                    while (rels.MoveNext())
                    {
                        var rel = rels.Current;
                        if (rel.Source != nodeId) continue;
                        foreach (var keyName in propKeys)
                        {
                            var val = tx.GetProperty(rel.Id, keyName);
                            if ((int)val.Type == 0) continue;
                            observed.TryGetValue(keyName, out var flags);
                            observed[keyName] = flags | val.Type.ToFlags();
                        }
                        relSampled++;
                        if (relSampled >= SampleLimit) break;
                    }
                }
            }

            var list = new List<PropertyInfo>();
            foreach (var (keyName, flags) in observed)
                list.Add(new PropertyInfo(keyName, FormatType(flags)));
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            relTypeProperties[relType] = list;
        }

        return new SchemaInspectionResult(labelProperties, relTypeProperties);
    }

    private static string FormatType(PropertyTypeFlags flags)
    {
        var types = new List<string>();
        if ((flags & PropertyTypeFlags.Bool) != 0) types.Add("bool");
        if ((flags & PropertyTypeFlags.Int32) != 0) types.Add("int");
        if ((flags & PropertyTypeFlags.Int64) != 0) types.Add("long");
        if ((flags & PropertyTypeFlags.Double) != 0) types.Add("double");
        if ((flags & PropertyTypeFlags.String) != 0) types.Add("string");
        if ((flags & PropertyTypeFlags.Bytes) != 0) types.Add("byte[]");
        if ((flags & PropertyTypeFlags.FloatArray) != 0) types.Add("float[]");
        return types.Count == 0 ? "unknown" : string.Join(" | ", types);
    }
}

public sealed record SchemaInspectionResult(
    Dictionary<string, List<PropertyInfo>> LabelProperties,
    Dictionary<string, List<PropertyInfo>> RelTypeProperties);

public sealed record PropertyInfo(string Name, string InferredType);
