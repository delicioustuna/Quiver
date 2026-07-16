namespace Quiver.Storage.Records;

/// <summary>
/// <see cref="EntityVersionMeta"/> を <c>EntityId.LocalId</c> をキーに格納する sidecar の抽象。
///
/// <para>Vertex と Edge の MVCC + SSN メタデータは kind ごとに 1 ファイル
/// (<see cref="Quiver.Wal.WalFileKind.VertexVersionMeta"/> / <see cref="Quiver.Wal.WalFileKind.EdgeVersionMeta"/>)
/// で保持する。本 interface はその物理層を抽象化する。</para>
///
/// <para>現時点ではどこからも呼ばれていない (テストのみ)。将来的に各 store の
/// visibility access path を sidecar 経由に切り替え、その後 SSN protocol が Pstamp/Sstamp の
/// post-commit 更新で使い始める。</para>
/// </summary>
internal interface IEntityVersionStore : IDisposable
{
    /// <summary>
    /// 指定 LocalId のエントリを読み出す。未書き込みなら <see cref="EntityVersionMeta.Unset"/>。
    /// </summary>
    EntityVersionMeta Read(long localId);

    /// <summary>指定 LocalId のエントリを全フィールドまとめて上書きする。</summary>
    void Write(long localId, in EntityVersionMeta meta);

    /// <summary>Xmax のみを更新する (delete / logical free 経路)。他フィールドは不変。</summary>
    void UpdateXmax(long localId, long xmax);

    /// <summary>Pstamp のみを更新する (SSN post-commit: reader cstamp 反映)。他フィールドは不変。</summary>
    void UpdatePstamp(long localId, long pstamp);

    /// <summary>Sstamp のみを更新する (SSN post-commit: overwriter cstamp 反映)。他フィールドは不変。</summary>
    void UpdateSstamp(long localId, long sstamp);

    /// <summary>
    /// SSN の大域 commit-stamp 高水位を耐久メタ (sidecar ヘッダ) に書き込む。
    /// commit と同一の page-WAL 単位で永続化され、再起動跨ぎで commit-stamp クロックを単調連続に保つ
    /// (= 旧/新 stamp 空間の混在による false-abort ストームを防ぐ)。
    /// </summary>
    void WriteCommitStampHighWater(long value);

    /// <summary>永続化済みの commit-stamp 高水位を読み出す。未書き込みなら 0。</summary>
    long ReadCommitStampHighWater();

    /// <summary>
    /// 世代再利用 (vacuum 回収済み slot を Allocate が再利用し generation を 2 以上に上げる事象) が
    /// この store で一度でも起きたか。<c>false</c> の間は「全ライブ slot の generation = 1」が成立し、
    /// 結果行の世代 stamping を sidecar read 無しで gen=1 として確定できる (hot path 高速化)。
    /// 耐久メタ (sidecar ヘッダ) に永続化され reopen を跨ぐ。
    /// </summary>
    bool AnyGenerationReuse { get; }

    /// <summary>
    /// 世代再利用が起きたことを記録する (冪等)。<see cref="AnyGenerationReuse"/> を恒久的に <c>true</c> へ。
    /// Allocate が generation ≥ 2 を払い出した時に呼ぶ。呼び出し tx の WAL コンテキストで永続化され、
    /// abort / crash では他の割り当てと一緒に巻き戻る。
    /// </summary>
    void MarkGenerationReuse();
}
