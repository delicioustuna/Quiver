using Quiver.Core;

namespace Quiver.Api;

/// <summary>
/// Nexusのロールに束縛するVertex参照。保存対象の <see cref="VertexId"/> を
/// 明示的に保持し、型付きロールプロパティ (<c>[Role]</c> 付き) の宣言型として使う。
/// </summary>
/// <remarks>
/// 型付きVertexオブジェクトは自身の <see cref="VertexId"/> を保持しないため、ロールに
/// どのVertexを束縛するかを ID で明示するのが本型の役割。<typeparamref name="TVertex"/>
/// はコンパイル時のロール型検査にのみ使われ、実行時には保持されない。
/// <see cref="VertexId"/> からの暗黙変換を用意しているので、代入は
/// <c>fact.Buyer = personId;</c> のように書ける。
/// </remarks>
/// <typeparam name="TVertex">束縛先Vertexの CLR 型 (<c>[Vertex]</c> 付きクラス)。</typeparam>
public readonly record struct GraphVertexRef<TVertex>(VertexId VertexId)
    where TVertex : IGraphVertex<TVertex>
{
    /// <summary><see cref="Core.VertexId"/> からロール束縛用の参照を生成する。</summary>
    public static implicit operator GraphVertexRef<TVertex>(VertexId vertexId) => new(vertexId);
}
