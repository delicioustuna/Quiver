namespace Quiver.Stores;

/// <summary>
/// FT-31: <see cref="EntityVersionMeta"/> を <c>EntityId.LocalId</c> をキーに格納する sidecar の抽象。
///
/// <para>Quiver の MVCC + SSN メタデータは EntityKind ごとに 1 ファイル
/// (<see cref="Quiver.Wal.WalFileKind.NodeVersionMeta"/> / <see cref="Quiver.Wal.WalFileKind.RelationshipVersionMeta"/>
/// / <see cref="Quiver.Wal.WalFileKind.PropertyVersionMeta"/>) で保持する。本 interface はその物理層を抽象化。</para>
///
/// <para>FT-31 時点ではどこからも呼ばれていない (テストのみ)。FT-32 で各 store の
/// visibility access path を sidecar 経由に切り替え、FT-33 で SSN protocol が Pstamp/Sstamp の
/// post-commit 更新で使い始める。</para>
/// </summary>
public interface IEntityVersionStore : IDisposable
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
}
