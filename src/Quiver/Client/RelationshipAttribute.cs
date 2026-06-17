using System;

namespace Quiver.Api;

/// <summary>
/// 付与したクラスを Quiver のリレーションシップとしてマークし、SourceGenerator が
/// 型安全 CRUD メソッド (<c>Insert</c> / <c>Load</c> / <c>Update</c> / <c>Delete</c>) と、
/// 始点 <typeparamref name="TSource"/> から終点 <typeparamref name="TTarget"/> への
/// 型保存トラバーサル糖衣を自動生成するための属性。
/// </summary>
/// <typeparam name="TSource">始点ノード型 (<c>[Node]</c> 付きクラス)。</typeparam>
/// <typeparam name="TTarget">終点ノード型 (<c>[Node]</c> 付きクラス)。</typeparam>
/// <remarks>
/// 型制約 (<c>IGraphNode&lt;T&gt;</c>) は本属性アセンブリが Quiver 本体を参照しない都合上
/// ここでは課さず、生成される <c>IGraphRelationship&lt;TSelf, TSource, TTarget&gt;</c> 実装側で
/// 担保される。
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RelationshipAttribute<TSource, TTarget> : Attribute
{
    /// <summary>付与クラスをリレーションシップとしてマークする。<paramref name="type"/> が null のときはクラス名が型として使われる。</summary>
    public RelationshipAttribute(string? type = null) { Type = type; }

    /// <summary>明示指定されたリレーションシップ型名 (省略時は <c>null</c>)。</summary>
    public string? Type { get; }
}
