using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quiver.Core;

/// <summary>
/// バイナリバックエンド向けのファイル裏付け <see cref="IVectorCatalog"/>。
/// インデックスとタスクの両方を 1 つの <c>vector_catalog.json</c> に保持し、
/// ミューテーション毎にファイル全体を書き直す (規模は数十〜数百インデックスを想定するため、
/// 書き換えコストはベクトル I/O に比べて無視できる)。並行アクセスはプロセス内ロックで直列化する —
/// バイナリバックエンド自体が単一書き込みのため、これで十分。
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
    /// <paramref name="path"/> をルートとするカタログをオープン (無ければ新規作成) する。
    /// ファイルが無い場合は空の状態で開始する。破損ファイルでは <see cref="VectorException"/> を投げ、
    /// 呼び出し側にバックアップ + リセットか起動拒否かを判断させる。
    /// </summary>
    public JsonFileVectorCatalog(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        Load();
    }

    /// <summary>新しいベクトルインデックスを登録する。空名 / 非正の次元 / 名前重複は <see cref="VectorException"/>。</summary>
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

    /// <summary>指定名のインデックスと紐づくタスクを削除する。存在しなければ <c>false</c>。</summary>
    public bool DropIndex(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            if (!_indexes.Remove(name)) return false;
            // このインデックスに紐づくタスクをすべて破棄する — そうしないと孤児が静かに溜まり続ける。
            var stale = _tasks.Keys.Where(k => k.IndexName == name).ToArray();
            foreach (var k in stale) _tasks.Remove(k);
            Save();
            return true;
        }
    }

    /// <summary>指定名のインデックス仕様を取得する。見つかれば <c>true</c>。</summary>
    public bool TryGetIndex(string name, out VectorIndexSpec spec)
    {
        lock (_gate)
        {
            return _indexes.TryGetValue(name, out spec!);
        }
    }

    /// <summary>登録済みインデックス仕様の一覧を返す。</summary>
    public IReadOnlyList<VectorIndexSpec> ListIndexes()
    {
        lock (_gate)
        {
            return _indexes.Values.ToArray();
        }
    }

    /// <summary>指定キーの埋め込みタスクを取得する。無ければ <c>null</c>。</summary>
    public EmbeddingTaskRecord? GetTask(EmbeddingTaskKey key)
    {
        lock (_gate)
        {
            return _tasks.TryGetValue(key, out var rec) ? rec : null;
        }
    }

    /// <summary>埋め込みタスクを挿入または更新する。</summary>
    public void UpsertTask(EmbeddingTaskRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _tasks[record.Key] = record;
            Save();
        }
    }

    /// <summary>指定キーの埋め込みタスクを削除する。無ければ <c>false</c>。</summary>
    public bool DeleteTask(EmbeddingTaskKey key)
    {
        lock (_gate)
        {
            if (!_tasks.Remove(key)) return false;
            Save();
            return true;
        }
    }

    /// <summary>埋め込みタスクを列挙する。<paramref name="indexName"/> 指定時はそのインデックス分のみ。</summary>
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
                    i.NormalizationProfile,
                    i.IndexKind,
                    i.HnswM,
                    i.HnswMMax0,
                    i.HnswMaxLayers,
                    i.HnswEfConstruction);
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
                    s.Dimensions, s.Metric, s.ProviderId, s.NormalizationProfile, s.IndexKind,
                    s.HnswM, s.HnswMMax0, s.HnswMaxLayers, s.HnswEfConstruction))
                .ToArray(),
            Tasks = _tasks.Values
                .Select(t => new TaskDto(
                    t.EntityKind, t.EntityId, t.IndexName, t.ProviderId,
                    t.State, t.ContentHash, t.LastError, t.LastUpdatedAtUtc))
                .ToArray(),
        };

        // 擬似アトミック書き込み: tmp ファイル + replace。シリアライズ中にクラッシュしても
        // 書きかけのカタログが残らないようにする。
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
        string? NormalizationProfile,
        VectorIndexKind IndexKind = VectorIndexKind.HnswFlat,
        int HnswM = 32,
        int HnswMMax0 = 64,
        int HnswMaxLayers = 8,
        int HnswEfConstruction = 400);

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
