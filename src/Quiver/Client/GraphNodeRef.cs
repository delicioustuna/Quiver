using Quiver.Core;

namespace Quiver.Api;

/// <summary>
/// ハイパーエッジのロールに束縛するノード参照。保存対象の <see cref="NodeId"/> を
/// 明示的に保持し、型付きロールプロパティ (<c>[Role]</c> 付き) の宣言型として使う。
/// </summary>
/// <remarks>
/// 型付きノードオブジェクトは自身の <see cref="NodeId"/> を保持しないため、ロールに
/// どのノードを束縛するかを ID で明示するのが本型の役割。<typeparamref name="TNode"/>
/// はコンパイル時のロール型検査にのみ使われ、実行時には保持されない。
/// <see cref="NodeId"/> からの暗黙変換を用意しているので、代入は
/// <c>fact.Buyer = personId;</c> のように書ける。
/// </remarks>
/// <typeparam name="TNode">束縛先ノードの CLR 型 (<c>[Node]</c> 付きクラス)。</typeparam>
public readonly record struct GraphNodeRef<TNode>(NodeId NodeId)
    where TNode : IGraphNode<TNode>
{
    /// <summary><see cref="Core.NodeId"/> からロール束縛用の参照を生成する。</summary>
    public static implicit operator GraphNodeRef<TNode>(NodeId nodeId) => new(nodeId);
}
