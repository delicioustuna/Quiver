using Yatagarasu.Core;

namespace Yatagarasu.Backend.Tests.Chaos;

/// <summary>
/// workload を実行した結果として「recovery 後に存在すべき」Vertex集合を追跡する。
///
/// 1 tx 単位で <see cref="BeginTx"/> → ops 実行で <see cref="RecordCreate"/> → <see cref="CommitTx"/>
/// または <see cref="RollbackTx"/> でまとめる。Rollback されると当該 tx の create は破棄される。
///
/// torn-write fault は最後の commit を巻き戻し得るので、verify 時に suffix 損失を許容する
/// オプションを提供する。
/// </summary>
internal sealed class OracleState
{
    private readonly List<TxRecord> _committedTxs = new();
    private List<VertexRecord>? _currentTxVertices;
    private bool _txOpen;

    public IReadOnlyList<TxRecord> CommittedTxs => _committedTxs;

    public void BeginTx()
    {
        _currentTxVertices = new List<VertexRecord>();
        _txOpen = true;
    }

    public void RecordCreate(VertexId id, long markerValue, int? indexKey)
    {
        if (!_txOpen)
            throw new InvalidOperationException("BeginTx() must be called first.");
        _currentTxVertices!.Add(new VertexRecord(id, markerValue, indexKey));
    }

    public void CommitTx()
    {
        if (!_txOpen) return;
        _committedTxs.Add(new TxRecord(_currentTxVertices!));
        _currentTxVertices = null;
        _txOpen = false;
    }

    public void RollbackTx()
    {
        _currentTxVertices = null;
        _txOpen = false;
    }

    public IEnumerable<VertexRecord> AllCommittedVertices()
    {
        foreach (var tx in _committedTxs)
            foreach (var n in tx.Vertices)
                yield return n;
    }
}

internal sealed record TxRecord(IReadOnlyList<VertexRecord> Vertices);
internal sealed record VertexRecord(VertexId Id, long MarkerValue, int? IndexKey);
