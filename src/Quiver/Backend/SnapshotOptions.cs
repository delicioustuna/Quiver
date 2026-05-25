namespace Quiver;

/// <summary>
/// OP-1: <see cref="GraphDatabase.CreateSnapshot"/> の挙動オプション。
/// 既定値で「索引・トークン・隣接ブロック・WAL を含む完全なライブスナップショット」になる。
/// </summary>
public sealed class SnapshotOptions
{
    /// <summary>
    /// B+Tree 索引ファイル (.idx / .idxmeta / .fileKinds) を含めるか。
    /// <c>false</c> のときは target を開いた後に <see cref="ISchemaApi.CreateIndex"/> で
    /// 再構築する想定。既定 <c>true</c>。
    /// </summary>
    public bool IncludeIndexes { get; set; } = true;
}
