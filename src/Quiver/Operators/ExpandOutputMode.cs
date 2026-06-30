namespace Quiver.Query.Physical;

/// <summary>
/// <see cref="ExpandOperator"/> が各エッジで放出するタプル構成を指定する。
/// </summary>
internal enum ExpandOutputMode
{
    NeighborOnly = 1,
    NeighborAndRel = 2,
    Full = 3,
    /// <summary>
    /// (rel, neighbor, weight) を放出する。weight は隣接ビューの inline payload lane から読む。
    /// weight スロットの型は <c>PayloadLaneSpec.Kind</c> に従う
    /// (Int64 → <see cref="TupleSlotType.Int64"/>、Double → <see cref="TupleSlotType.Double"/>)。
    /// payload lane を持たない <c>IAdjacencyBlockStore</c> では <c>DefaultRaw</c> が入る
    /// (property-chain 値ではない) ため、ビルドモードに依らず呼び出し元に一貫した契約を提供する。
    /// </summary>
    NeighborAndWeight = 4,
}
