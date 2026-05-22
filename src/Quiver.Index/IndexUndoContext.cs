namespace Quiver.Index;

/// <summary>
/// FT-17: B+Tree インデックスの論理ミューテーション (Insert / Delete) を、進行中の
/// 書き込みトランザクションへ通知する受け口。
/// </summary>
public interface IIndexUndoSink
{
    /// <summary>
    /// インデックスへの 1 件の論理ミューテーションを記録する。
    /// </summary>
    /// <param name="indexName">索引名。</param>
    /// <param name="keyKind">キー型タグ。</param>
    /// <param name="keyBytes">コーデックでエンコード済みのキーバイト列。</param>
    /// <param name="value">エントリ値 (通常は NodeId.Value)。</param>
    /// <param name="isInsert"><c>true</c> なら Insert、<c>false</c> なら Delete。</param>
    void RecordIndexMutation(
        string indexName, IndexKeyKind keyKind, ReadOnlySpan<byte> keyBytes,
        long value, bool isInsert);
}

/// <summary>
/// FT-17: 書き込みトランザクション中のインデックス論理 undo ロギング用の、
/// スレッドローカルな ambient コンテキスト。<see cref="Quiver.Wal"/> 名前空間の
/// <c>WalPageContext</c> と同じパターン: 書き込みトランザクション開始時にセットし、
/// Commit / Abort 時にクリアする。
///
/// <see cref="BTreeIndex{TKey}"/> はキーコーデックを所有しキーをエンコードできる
/// 唯一の層なので、Insert / Delete 成功時にここへ通知する。コンテキスト未設定時
/// (トランザクション外のバルク構築・recovery 中の逆適用など) は no-op。
/// </summary>
public static class IndexUndoContext
{
    [ThreadStatic]
    private static IIndexUndoSink? _current;

    /// <summary>このスレッドでインデックス undo ロギングを開始する。</summary>
    public static void Begin(IIndexUndoSink sink) => _current = sink;

    /// <summary>現在の undo コンテキストを破棄する。</summary>
    public static void End() => _current = null;

    /// <summary>
    /// 書き込みトランザクションがアクティブな場合、インデックスミューテーションを
    /// 記録する。未設定時は no-op。
    /// </summary>
    public static void Record(
        string indexName, IndexKeyKind keyKind, ReadOnlySpan<byte> keyBytes,
        long value, bool isInsert)
        => _current?.RecordIndexMutation(indexName, keyKind, keyBytes, value, isInsert);
}
