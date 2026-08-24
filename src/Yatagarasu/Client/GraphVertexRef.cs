namespace Yatagarasu.Api;

/// <summary>
/// Nexusのロールに束縛するVertex参照。不透明な <see cref="VertexKey"/> を
/// 明示的に保持し、型付きロールプロパティ (<c>[Role]</c> 付き) の宣言型として使う。
/// </summary>
/// <remarks>
/// 型付きVertex参照から、ロールにどのVertexを束縛するかを明示するのが本型の役割。<typeparamref name="TVertex"/>
/// はコンパイル時のロール型検査にのみ使われ、実行時には保持されない。
/// <see cref="GraphEntity{T}"/> と <see cref="VertexKey"/> から暗黙変換できる。
/// </remarks>
/// <typeparam name="TVertex">束縛先Vertexの CLR 型 (<c>[Vertex]</c> 付きクラス)。</typeparam>
public readonly record struct GraphVertexRef<TVertex>(VertexKey Key)
    where TVertex : IGraphEntity<TVertex>
{
    /// <summary>旧内部 graph ID からロール束縛用の参照を生成する。</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    internal GraphVertexRef(Core.VertexId vertexId) : this(new VertexKey(vertexId))
    {
    }

    /// <summary>旧内部 graph ID。新しい利用者コードでは <see cref="Key"/> を使用する。</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    internal Core.VertexId VertexId => Key.ToCore();

    /// <summary>型付き Vertex 参照からロール束縛用の参照を生成する。</summary>
    public static implicit operator GraphVertexRef<TVertex>(GraphEntity<TVertex> entity) => new(entity.Key);

    /// <summary>不透明 Vertex key からロール束縛用の参照を生成する。</summary>
    public static implicit operator GraphVertexRef<TVertex>(VertexKey key) => new(key);

}
