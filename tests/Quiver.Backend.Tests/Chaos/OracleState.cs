using Quiver.Core;

namespace Quiver.Backend.Tests.Chaos;

/// <summary>
/// workload を実行した結果として「recovery 後に存在すべき」ノード集合を追跡する。
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
    private List<NodeRecord>? _currentTxNodes;
    private bool _txOpen;

    public IReadOnlyList<TxRecord> CommittedTxs => _committedTxs;

    public void BeginTx()
    {
        _currentTxNodes = new List<NodeRecord>();
        _txOpen = true;
    }

    public void RecordCreate(NodeId id, long markerValue, int? indexKey)
    {
        if (!_txOpen)
            throw new InvalidOperationException("BeginTx() must be called first.");
        _currentTxNodes!.Add(new NodeRecord(id, markerValue, indexKey));
    }

    public void CommitTx()
    {
        if (!_txOpen) return;
        _committedTxs.Add(new TxRecord(_currentTxNodes!));
        _currentTxNodes = null;
        _txOpen = false;
    }

    public void RollbackTx()
    {
        _currentTxNodes = null;
        _txOpen = false;
    }

    public IEnumerable<NodeRecord> AllCommittedNodes()
    {
        foreach (var tx in _committedTxs)
            foreach (var n in tx.Nodes)
                yield return n;
    }
}

internal sealed record TxRecord(IReadOnlyList<NodeRecord> Nodes);
internal sealed record NodeRecord(NodeId Id, long MarkerValue, int? IndexKey);
