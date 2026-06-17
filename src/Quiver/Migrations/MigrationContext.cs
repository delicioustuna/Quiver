using System.Diagnostics;
using Quiver.Core;

namespace Quiver.Migrations;

internal sealed class MigrationContext : IMigrationContext
{
    public IGraphTransaction Transaction { get; }
    public ISchemaApi Schema { get; }
    public string MigrationId { get; }

    // OP-4 (fix A): スキーマミューテーションを <see cref="Transaction"/> の rollback と整合させるための
    // 逆操作キュー。各 schema mutation を行うたびにその逆操作を append し、OnRolledBack で
    // 逆順 (LIFO) に再生する。TokenStore / IndexManager は ARIES tx に乗らないが、
    // この hook で論理的な巻き戻しを実現する。
    private readonly List<Action> _undoActions = [];
    private bool _hooksRegistered;

    internal MigrationContext(IGraphTransaction tx, ISchemaApi schema, string migrationId)
    {
        Transaction = tx;
        Schema = schema;
        MigrationId = migrationId;
    }

    /// <summary>
    /// 初回の schema mutation 時に tx の OnRolledBack に逆操作再生フックを登録する。
    /// 多重登録を避けるため idempotent。
    /// </summary>
    private void EnsureRollbackHook()
    {
        if (_hooksRegistered) return;
        _hooksRegistered = true;
        Transaction.OnRolledBack(ReplayUndo);
    }

    private void ReplayUndo()
    {
        // LIFO で巻き戻す。個別の逆操作が例外を投げても他の逆操作の実行は続ける
        // (rollback 経路で例外を投げると tx aborter が二次失敗する)。
        for (int i = _undoActions.Count - 1; i >= 0; i--)
        {
            try { _undoActions[i](); }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    $"[Quiver.Migrations] Migration '{MigrationId}' rollback step {i} threw: {ex}");
            }
        }
        _undoActions.Clear();
    }

    public bool RenameLabel(string oldName, string newName)
    {
        EnsureRollbackHook();
        // OP-4 (bug 1 fix): rename が「実際に状態を変える」場合だけ undo を登録する。
        // oldName が無く newName が既にある冪等 no-op パスで undo を登録すると、
        // rollback 時に pre-existing な newName を壊す corruption になる。
        bool willMutate = Schema.TryGetLabelId(oldName, out _) && !Schema.TryGetLabelId(newName, out _);
        var ok = Schema.RenameLabel(oldName, newName);
        if (ok && willMutate) _undoActions.Add(() => Schema.RenameLabel(newName, oldName));
        return ok;
    }

    public bool RenamePropertyKey(string oldName, string newName)
    {
        EnsureRollbackHook();
        bool willMutate = Schema.TryGetPropertyKeyId(oldName, out _) && !Schema.TryGetPropertyKeyId(newName, out _);
        var ok = Schema.RenamePropertyKey(oldName, newName);
        if (ok && willMutate) _undoActions.Add(() => Schema.RenamePropertyKey(newName, oldName));
        return ok;
    }

    public bool RenameRelationshipType(string oldName, string newName)
    {
        EnsureRollbackHook();
        bool willMutate = Schema.TryGetRelationshipTypeId(oldName, out _) && !Schema.TryGetRelationshipTypeId(newName, out _);
        var ok = Schema.RenameRelationshipType(oldName, newName);
        if (ok && willMutate) _undoActions.Add(() => Schema.RenameRelationshipType(newName, oldName));
        return ok;
    }

    public bool RenameIndex(string oldName, string newName)
    {
        EnsureRollbackHook();
        bool willMutate = Schema.IndexExists(oldName) && !Schema.IndexExists(newName);
        var ok = Schema.RenameIndex(oldName, newName);
        if (ok && willMutate) _undoActions.Add(() => Schema.RenameIndex(newName, oldName));
        return ok;
    }

    public void AddIndex(string indexName, string label, string propertyKey, IndexKind kind)
    {
        EnsureRollbackHook();
        // OP-4 (bug 2 fix): 既存索引に対する AddIndex は no-op。その場合 DropIndex undo を
        // 登録すると rollback で pre-existing な索引が消える corruption になる。
        bool existedBefore = Schema.IndexExists(indexName);
        Schema.CreateIndex(indexName, label, propertyKey, kind);
        if (!existedBefore) _undoActions.Add(() => Schema.DropIndex(indexName));
    }

    public void DropIndex(string indexName)
    {
        // OP-4 (fix A): DropIndex は索引ファイルを物理削除するため transactional rollback できない。
        // migrations を書く側で「失敗しうる重い処理の後」に置く / または rebuild migration として
        // 設計する責任がある点を IMigrationContext.DropIndex の doc で明記。
        Schema.DropIndex(indexName);
    }

    public void ForEachNode(string label, Action<NodeId> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrEmpty(label);
        var labelId = Schema.GetOrCreateLabel(label);
        if (Transaction is not GraphTransaction gtx)
            throw new InvalidOperationException(
                "MigrationContext.ForEachNode requires the default GraphTransaction implementation.");
        foreach (var nodeId in gtx.Access.ScanNodes(gtx.Inner, labelId))
            action(nodeId);
    }
}
