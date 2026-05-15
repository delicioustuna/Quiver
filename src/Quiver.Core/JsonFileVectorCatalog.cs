using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quiver.Core;

/// <summary>
/// File-backed <see cref="IVectorCatalog"/> for the binary backend. Both
/// indexes and tasks live in a single <c>vector_catalog.json</c>; the file is
/// rewritten in full on every mutation (tens-to-hundreds of indexes is the
/// expected scale, so rewrite cost is irrelevant compared with vector I/O).
/// Concurrent access is serialised by an in-process lock — the binary backend
/// is itself single-writer so this is sufficient. VEC-2.
/// </summary>
public sealed class JsonFileVectorCatalog : IVectorCatalog
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, VectorIndexSpec> _indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<EmbeddingTaskKey, EmbeddingTaskRecord> _tasks = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Open or create the catalog rooted at <paramref name="path"/>. Missing
    /// files start empty; corrupt files raise <see cref="VectorException"/>
    /// so callers can decide whether to back up + reset or refuse to start.
    /// </summary>
    public JsonFileVectorCatalog(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        Load();
    }

    public void CreateIndex(VectorIndexSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrEmpty(spec.Name))
            throw new VectorException("Vector index name must not be empty.");
        if (spec.Dimensions <= 0)
            throw new VectorException(
                $"Vector index '{spec.Name}' must have positive dimensions (was {spec.Dimensions}).");

        lock (_gate)
        {
            if (_indexes.ContainsKey(spec.Name))
                throw new VectorException($"Vector index '{spec.Name}' already exists.");
            _indexes[spec.Name] = spec;
            Save();
        }
    }

    public bool DropIndex(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            if (!_indexes.Remove(name)) return false;
            // Drop all tasks bound to this index — orphans would silently
            // accumulate otherwise.
            var stale = _tasks.Keys.Where(k => k.IndexName == name).ToArray();
            foreach (var k in stale) _tasks.Remove(k);
            Save();
            return true;
        }
    }

    public bool TryGetIndex(string name, out VectorIndexSpec spec)
    {
        lock (_gate)
        {
            return _indexes.TryGetValue(name, out spec!);
        }
    }

    public IReadOnlyList<VectorIndexSpec> ListIndexes()
    {
        lock (_gate)
        {
            return _indexes.Values.ToArray();
        }
    }

    public EmbeddingTaskRecord? GetTask(EmbeddingTaskKey key)
    {
        lock (_gate)
        {
            return _tasks.TryGetValue(key, out var rec) ? rec : null;
        }
    }

    public void UpsertTask(EmbeddingTaskRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _tasks[record.Key] = record;
            Save();
        }
    }

    public bool DeleteTask(EmbeddingTaskKey key)
    {
        lock (_gate)
        {
            if (!_tasks.Remove(key)) return false;
            Save();
            return true;
        }
    }

    public IEnumerable<EmbeddingTaskRecord> ListTasks(string? indexName = null)
    {
        lock (_gate)
        {
            var src = _tasks.Values;
            return indexName is null
                ? src.ToArray()
                : src.Where(r => r.IndexName == indexName).ToArray();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;

        CatalogDocument? doc;
        try
        {
            using var stream = File.OpenRead(_path);
            doc = JsonSerializer.Deserialize<CatalogDocument>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new VectorException($"Vector catalog '{_path}' is corrupt: {ex.Message}", ex);
        }

        if (doc is null) return;

        if (doc.Indexes is not null)
        {
            foreach (var i in doc.Indexes)
                _indexes[i.Name] = new VectorIndexSpec(
                    i.Name,
                    i.EntityKind,
                    new PropertyKeyId(i.SourcePropertyKeyId),
                    i.Dimensions,
                    i.Metric,
                    i.ProviderId,
                    i.NormalizationProfile);
        }

        if (doc.Tasks is not null)
        {
            foreach (var t in doc.Tasks)
            {
                var rec = new EmbeddingTaskRecord(
                    t.EntityKind, t.EntityId, t.IndexName, t.ProviderId,
                    t.State, t.ContentHash, t.LastError, t.LastUpdatedAtUtc);
                _tasks[rec.Key] = rec;
            }
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var doc = new CatalogDocument
        {
            Indexes = _indexes.Values
                .Select(s => new IndexDto(
                    s.Name, s.EntityKind, s.SourcePropertyKeyId.Value,
                    s.Dimensions, s.Metric, s.ProviderId, s.NormalizationProfile))
                .ToArray(),
            Tasks = _tasks.Values
                .Select(t => new TaskDto(
                    t.EntityKind, t.EntityId, t.IndexName, t.ProviderId,
                    t.State, t.ContentHash, t.LastError, t.LastUpdatedAtUtc))
                .ToArray(),
        };

        // Atomic-ish write: tmp file + replace, so a crash mid-serialize does
        // not leave a half-written catalog behind.
        var tmp = _path + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, doc, JsonOptions);
        }
        if (File.Exists(_path)) File.Replace(tmp, _path, destinationBackupFileName: null);
        else File.Move(tmp, _path);
    }

    private sealed class CatalogDocument
    {
        public IndexDto[]? Indexes { get; set; }
        public TaskDto[]? Tasks { get; set; }
    }

    private sealed record IndexDto(
        string Name,
        EntityKind EntityKind,
        int SourcePropertyKeyId,
        int Dimensions,
        DistanceMetric Metric,
        string ProviderId,
        string? NormalizationProfile);

    private sealed record TaskDto(
        EntityKind EntityKind,
        long EntityId,
        string IndexName,
        string ProviderId,
        EmbeddingTaskState State,
        string? ContentHash,
        string? LastError,
        DateTimeOffset LastUpdatedAtUtc);
}
