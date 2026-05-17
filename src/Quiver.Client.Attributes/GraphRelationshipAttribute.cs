using System;

namespace Quiver.Client;

/// <summary>
/// 付与したクラスを Quiver のリレーションシップとしてマークし、SourceGenerator が
/// 型安全 CRUD メソッド (<c>Insert</c> / <c>Load</c> / <c>Update</c> / <c>Delete</c>) を
/// 自動生成するための属性。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GraphRelationshipAttribute : Attribute
{
    /// <summary>付与クラスをリレーションシップとしてマークする。<paramref name="type"/> が null のときはクラス名が型として使われる。</summary>
    public GraphRelationshipAttribute(string? type = null) { Type = type; }

    /// <summary>明示指定されたリレーションシップ型名 (省略時は <c>null</c>)。</summary>
    public string? Type { get; }
}
