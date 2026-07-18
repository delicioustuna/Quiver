namespace Quiver;

/// <summary>
/// <see cref="QuiverDatabase.CreateSnapshot"/> の挙動オプション。
/// 既定値で「索引・トークン・隣接ブロック・WAL を含む完全なライブスナップショット」になる。
/// </summary>
public sealed class SnapshotOptions
{
    /// <summary>
    /// B+Tree 索引を snapshot に含めるか。
    /// </summary>
    /// <remarks>
    /// 別ストレージで索引を独立ファイルに持つバックエンドのための互換オプション。
    /// <c>false</c> のときは target を開いた後に<see cref="ISchemaEditor.CreateIndex"/> で再構築が必要。
    /// </remarks>
    // binary backend では索引は <c>graph.quiver</c> 本体のページとして同居するため、
    // 物理 page-by-page コピーから索引だけを除外することはできない (本フラグは binary backend では
    // 実質 no-op で、索引は常に含まれる)。別ストレージで索引を独立ファイルに持つ backend のための
    // 互換オプションとして残す。
    public bool IncludeIndexes { get; set; } = true;
}
