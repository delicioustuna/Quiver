namespace Quiver.Core;

/// <summary>
/// 1 件の (entity, index, provider) に対する埋め込みジョブのライフサイクル状態。
/// <c>Quiver.Embedding</c> (VEC-4) のヘルパ側 enum と対応しており、ヘルパが
/// バック参照無しでカタログを問い合わせられるようにする。VEC-2。
/// </summary>
public enum EmbeddingTaskState : byte
{
    /// <summary>未着手。</summary>
    NotStarted      = 0,
    /// <summary>処理中。</summary>
    InProgress      = 1,
    /// <summary>完了。</summary>
    Completed       = 2,
    /// <summary>失敗 (再試行可能)。</summary>
    FailedRetryable = 3,
    /// <summary>失敗 (恒久的)。</summary>
    FailedPermanent = 4,
    /// <summary>陳腐化済み。</summary>
    Stale           = 5,
}

/// <summary>
/// 埋め込みジョブの識別子。<c>embedding_tasks</c> SQLite 主キーと同じ構成で、
/// record struct とすることで割り当て無しに辞書キー / upsert フィルタとして使える。
/// </summary>
public readonly record struct EmbeddingTaskKey(
    EntityKind EntityKind,
    long EntityId,
    string IndexName,
    string ProviderId);

/// <summary>
/// 埋め込みジョブの永続化状態。<see cref="ContentHash"/> が最後に成功した埋め込み対象テキストを
/// 追跡することで、再実行時にプロバイダを呼び直さずに陳腐化を検出できる。
/// </summary>
public sealed record EmbeddingTaskRecord(
    EntityKind EntityKind,
    long EntityId,
    string IndexName,
    string ProviderId,
    EmbeddingTaskState State,
    string? ContentHash,
    string? LastError,
    DateTimeOffset LastUpdatedAtUtc)
{
    /// <summary>レコードのキー。</summary>
    public EmbeddingTaskKey Key => new(EntityKind, EntityId, IndexName, ProviderId);
}

/// <summary>
/// ベクトルインデックスと埋め込みタスクライフサイクルの永続メタデータ。カタログは
/// <see cref="VectorIndexSpec"/> 定義とエンティティ別タスクレコードを保持する。
/// 実際のベクトル payload と ANN インデックスは別の場所 (バイナリサイドカー / 将来の ANN バックエンド) に
/// 持つ — 詳細は codex_advice_3.md 6.5 節。VEC-2。
/// </summary>
public interface IVectorCatalog
{
    /// <summary>
    /// 新しいベクトルインデックスを登録する。同名のインデックスが既に存在する場合は
    /// <see cref="VectorException"/> を投げる — 下流の状態を壊す前にカタログ層で重複作成を検出する。
    /// </summary>
    void CreateIndex(VectorIndexSpec spec);

    /// <summary>
    /// インデックスを削除する。エントリが削除されたら true、その名前のインデックスが
    /// 無かった場合は false。後者をエラー扱いするかは呼び出し側の判断に委ねる。
    /// </summary>
    bool DropIndex(string name);

    /// <summary>名前で <see cref="VectorIndexSpec"/> を取得する。</summary>
    bool TryGetIndex(string name, out VectorIndexSpec spec);

    /// <summary>登録済みのインデックス一覧を返す。</summary>
    IReadOnlyList<VectorIndexSpec> ListIndexes();

    /// <summary>キーに紐づくタスクレコードを返す (存在しなければ <c>null</c>)。</summary>
    EmbeddingTaskRecord? GetTask(EmbeddingTaskKey key);

    /// <summary>
    /// タスクレコードを挿入または置換する。同一性は
    /// <c>(EntityKind, EntityId, IndexName, ProviderId)</c> タプル — SQLite 主キーと同じ。
    /// </summary>
    void UpsertTask(EmbeddingTaskRecord record);

    /// <summary>タスクレコードを削除する。</summary>
    bool DeleteTask(EmbeddingTaskKey key);

    /// <summary>
    /// タスクレコードを列挙する。任意で 1 つのインデックスに絞り込める。
    /// 順序は実装依存。安定順序が必要な呼び出し側は自身でソートすること。
    /// </summary>
    IEnumerable<EmbeddingTaskRecord> ListTasks(string? indexName = null);
}
