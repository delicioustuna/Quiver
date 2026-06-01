using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// ARCH-2: backend 非依存の内部トランザクション契約。公開 <see cref="IGraphTransaction"/> から
/// 外した「物理プラン実行 / access methods / 隣接ブロック」を担い、エンジン内部と
/// backend 衛星 (binary / SQLite) の双方が実装する。API 利用者には露出しない。
/// </summary>
internal interface IGraphTransactionInternal : IGraphTransaction
{
    /// <summary>backend が提供する access methods (capability 問い合わせ・scan / expand)。</summary>
    IGraphAccessMethods Access { get; }

    /// <summary>隣接ブロックインデックス。bulk load で構築された場合のみ非 null。</summary>
    IAdjacencyBlockStore? AdjacencyBlocks { get; }

    /// <summary>物理プランを実行して結果を <see cref="QueryResult"/> で返す。</summary>
    QueryResult Execute(IPhysicalOperator plan);

    /// <summary>物理プランをストリーミング実行し、<see cref="IQueryCursor"/> で逐次取得する。</summary>
    IQueryCursor ExecuteCursor(IPhysicalOperator plan);
}

/// <summary>
/// ARCH-2: 旧 <see cref="IGraphTransaction"/> の物理実行 API を internal 拡張として温存し、
/// 既存の呼び出し側 (DSL / テスト) を無改変で内部経路へ橋渡しする。
/// </summary>
internal static class GraphTransactionInternalExtensions
{
    /// <summary>公開ハンドルを内部契約へキャストする。実装は binary / SQLite の双方が満たす。</summary>
    internal static IGraphTransactionInternal AsInternal(this IGraphTransaction tx)
        => (IGraphTransactionInternal)tx;

    /// <summary>物理プランを実行する (旧 <c>IGraphTransaction.Execute</c> 互換)。</summary>
    internal static QueryResult Execute(this IGraphTransaction tx, IPhysicalOperator plan)
        => ((IGraphTransactionInternal)tx).Execute(plan);

    /// <summary>物理プランをストリーミング実行する (旧 <c>IGraphTransaction.ExecuteCursor</c> 互換)。</summary>
    internal static IQueryCursor ExecuteCursor(this IGraphTransaction tx, IPhysicalOperator plan)
        => ((IGraphTransactionInternal)tx).ExecuteCursor(plan);
}
