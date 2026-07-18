using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// backend 非依存の内部トランザクション契約。公開 <see cref="IReadTransaction"/> から
/// 外した「物理プラン実行 / access methods / 隣接ブロック」を担い、エンジン内部と
/// backend 衛星が実装する。API 利用者には露出しない。
/// </summary>
internal interface IReadTransactionInternal : IReadTransaction
{
    /// <summary>物理オペレータへ渡す snapshot-scoped store context。</summary>
    ITransaction Inner { get; }

    TransactionId TransactionId { get; }

    /// <summary>
    /// 内部オーケストレータが複数の公開操作を一つのトランザクション実行フローとして束ねる。
    /// </summary>
    TransactionUsageLease EnterUsage();

    /// <summary>backend が提供する access methods (capability 問い合わせ・scan / expand)。</summary>
    IGraphAccessMethods Access { get; }

    /// <summary>隣接ブロックインデックス。bulk load で構築された場合のみ非 null。</summary>
    IAdjacencySegmentStore? AdjacencySegments { get; }

    /// <summary>物理プランを実行して結果を <see cref="QueryResult"/> で返す。</summary>
    QueryResult Execute(IPhysicalOperator plan);

    /// <summary>物理プランをストリーミング実行し、<see cref="IQueryCursor"/> で逐次取得する。</summary>
    IQueryCursor ExecuteCursor(IPhysicalOperator plan);

    /// <summary>
    /// <paramref name="kind"/> の全件 (full scan) を対象とする集約で、
    /// <paramref name="key"/> が列化済みなら列スキャンで count/sum/min/max を直接集計する。
    /// 列が無い / mixed / backend 非対応なら false を返し、呼び出し側が row path フォールバックする。
    /// </summary>
    bool TryColumnAggregate(Core.EntityKind kind, string key, out ColumnAggregate result);
}

/// <summary>
/// 列スキャン集約の結果。<see cref="Sum"/>/<see cref="Min"/>/<see cref="Max"/> は
/// 数値 (double)、<see cref="LongSum"/> は整数型の厳密 long 合計 (SumLong 用)。
/// <see cref="ValueType"/> は列の scalar 型 (SumLong 可否判定などに使う)。
/// </summary>
internal readonly record struct ColumnAggregate(
    long Count,
    double Sum,
    double Min,
    double Max,
    long LongSum,
    Storage.Records.PropertyValueType ValueType);

/// <summary>
/// 公開 read transaction を内部の物理実行契約へ橋渡しする。
/// </summary>
internal static class GraphTransactionInternalExtensions
{
    /// <summary>公開ハンドルを内部契約へキャストする。</summary>
    internal static IReadTransactionInternal AsInternal(this IReadTransaction tx)
        => (IReadTransactionInternal)tx;

    /// <summary>物理プランを実行する。</summary>
    internal static QueryResult Execute(this IReadTransaction tx, IPhysicalOperator plan)
        => ((IReadTransactionInternal)tx).Execute(plan);

    /// <summary>物理プランをストリーミング実行する。</summary>
    internal static IQueryCursor ExecuteCursor(this IReadTransaction tx, IPhysicalOperator plan)
        => ((IReadTransactionInternal)tx).ExecuteCursor(plan);
}
