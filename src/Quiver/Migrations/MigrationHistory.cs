using System.Text;

namespace Quiver.Migrations;

/// <summary>
/// OP-4: 適用済みマイグレーションの永続記録。tab 区切りテキスト 1 行 / 1 エントリで
/// <c>&lt;DataDirectory&gt;/migrations.history</c> に保存する。
/// 形式: <c>Id\tVersion\tAppliedAtUtcIso\n</c>
/// </summary>
/// <remarks>
/// シンプルテキスト形式を採用する理由:
/// - 障害時にエディタで目視 / 編集できる
/// - JSON 依存を本体に追加せずに済む
/// - 1 マイグレーション 1 行で append-only に書け、partial write が直近行に局在する
/// 障害局在化のため、Append は flush + fsync で行う。Load は壊れた行を見つけ次第以降を破棄して
/// "applied までは durable" 前提を維持する (最後の append が中途半端だった場合に、その migration は
/// 未適用として扱われ、次回起動で再適用される — Apply 自体が idempotent である前提)。
/// </remarks>
internal sealed class MigrationHistory
{
    private readonly string _path;
    private readonly List<MigrationHistoryEntry> _entries = [];
    private readonly HashSet<string> _appliedIds = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    internal MigrationHistory(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "migrations.history");
        Load();
    }

    /// <summary>適用済み migration Id か。</summary>
    public bool IsApplied(string id)
    {
        lock (_gate) return _appliedIds.Contains(id);
    }

    /// <summary>適用済みエントリのスナップショット (順序: 適用順)。</summary>
    public IReadOnlyList<MigrationHistoryEntry> Entries
    {
        get
        {
            lock (_gate) return _entries.ToArray();
        }
    }

    /// <summary>
    /// <paramref name="entry"/> を append し、即座に fsync する。同一 ID の重複追加は
    /// <see cref="InvalidOperationException"/>。
    /// </summary>
    public void Append(MigrationHistoryEntry entry)
    {
        lock (_gate)
        {
            if (_appliedIds.Contains(entry.Id))
                throw new InvalidOperationException(
                    $"Migration '{entry.Id}' is already recorded in history.");

            using var fs = new FileStream(
                _path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var line = $"{Escape(entry.Id)}\t{entry.Version}\t{entry.AppliedAtUtc:O}\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);

            _entries.Add(entry);
            _appliedIds.Add(entry.Id);
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        foreach (var line in File.ReadAllLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 3) break; // 部分書き込みは以降破棄
            if (!int.TryParse(parts[1], out var version)) break;
            if (!DateTime.TryParse(parts[2], null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var appliedAt)) break;
            var entry = new MigrationHistoryEntry(Unescape(parts[0]), version, appliedAt);
            _entries.Add(entry);
            _appliedIds.Add(entry.Id);
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n");
    private static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
}

/// <summary>OP-4: 適用済みマイグレーション 1 件の記録。</summary>
public sealed record MigrationHistoryEntry(string Id, int Version, DateTime AppliedAtUtc);
