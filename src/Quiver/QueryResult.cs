using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver;

/// <summary>
/// 結果行をマテリアライズする際に、NodeId 列へ現 slot 世代 (incarnation) を load する。
/// 内部クエリパイプラインは Sequence 空間 (gen=0) で動かして hot path のコストを避けつつ、
/// 利用者に返す NodeId の <c>Value</c> を <c>CreateNode</c>/<c>Allocate</c> が返した id と一致させる
/// (round-trip 一貫 + 世代付きで往復検証が効く)。リレーションシップは世代を持たないので素通し。
/// </summary>
internal static class QueryRowMaterializer
{
    public static void StampEntityGenerations(
        TupleSlot[] slots,
        INodeStore nodes,
        IHyperedgeStore hyperedges)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].Type == TupleSlotType.NodeId)
            {
                long seq = new NodeId(slots[i].LongValue).Sequence;
                if (seq < 0) continue;
                int gen = nodes.CurrentGeneration(seq);
                if (gen > 0) slots[i].LongValue = EntityRef.PackLocal(seq, gen);
            }
            else if (slots[i].Type == TupleSlotType.HyperedgeId)
            {
                var id = new HyperedgeId(slots[i].LongValue);
                if (!id.IsValid || id.Generation > 0) continue;
                using var header = hyperedges.Read(id);
                if (header.InUse) slots[i].LongValue = header.Id.Value;
            }
        }
    }
}

/// <summary>
/// 物理プランをマテリアライズして得られるクエリ結果。
/// 行 (<see cref="QueryRow"/>) のコレクションと統計情報を保持する。
/// </summary>
internal sealed class QueryResult : IDisposable
{
    private readonly List<QueryRow> _rows;

    /// <summary>結果タプルのスキーマ。</summary>
    public TupleSchema Schema { get; }

    /// <summary>実行時に集計したオペレータ統計。</summary>
    public OperatorStatistics Statistics { get; }

    internal QueryResult(TupleSchema schema, OperatorStatistics statistics, List<QueryRow> rows)
    {
        Schema = schema;
        Statistics = statistics;
        _rows = rows;
    }

    /// <summary>すべての結果行を順番に列挙する。</summary>
    public IEnumerable<QueryRow> Rows() => _rows;

    /// <summary>(現状は no-op。<see cref="IDisposable"/> の対称性を保つために提供。)</summary>
    public void Dispose() { }
}

/// <summary>クエリ結果の 1 行を表す軽量構造体。列アクセスは型ごとのメソッドで行う。</summary>
internal readonly struct QueryRow
{
    private readonly TupleSlot[] _slots;
    private readonly byte[]?[]? _byteData;

    internal QueryRow(TupleSlot[] slots, byte[]?[]? byteData = null)
    {
        _slots = slots;
        _byteData = byteData;
    }

    /// <summary>行が保持する列数。</summary>
    public int ColumnCount => _slots.Length;

    /// <summary>指定列の実型を返す。</summary>
    public TupleSlotType GetSlotType(int column) => _slots[column].Type;

    /// <summary>指定列を <see cref="long"/> として取り出す。</summary>
    public long GetInt64(int column) => _slots[column].LongValue;

    /// <summary>指定列を <see cref="NodeId"/> として取り出す。</summary>
    public NodeId GetNodeId(int column) => new(_slots[column].LongValue);

    /// <summary>指定列を <see cref="RelationshipId"/> として取り出す。</summary>
    public RelationshipId GetRelationshipId(int column) => new(_slots[column].LongValue);

    /// <summary>指定列を <see cref="HyperedgeId"/> として取り出す。</summary>
    public HyperedgeId GetHyperedgeId(int column) => new(_slots[column].LongValue);

    /// <summary>指定列を <see cref="double"/> として取り出す。</summary>
    public double GetDouble(int column) => _slots[column].DoubleValue;

    /// <summary>指定列を <see cref="bool"/> として取り出す。</summary>
    public bool GetBoolean(int column) => _slots[column].LongValue != 0;

    /// <summary>指定列を UTF-8 文字列にデコードして返す。バイトが無ければ空文字。</summary>
    public string GetString(int column)
        => _byteData?[column] is { } b ? System.Text.Encoding.UTF8.GetString(b) : string.Empty;

    /// <summary>指定列のバイト列をそのまま返す (UTF-8 / Bytes 用)。</summary>
    public byte[] GetBytes(int column)
        => _byteData?[column] ?? Array.Empty<byte>();
}
