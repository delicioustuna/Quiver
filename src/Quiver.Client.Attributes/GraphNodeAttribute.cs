using System;

namespace Quiver.Client;

/// <summary>
/// 付与したクラスを Quiver のグラフノードとしてマークし、SourceGenerator が
/// 型安全 CRUD メソッド (<c>Insert</c> / <c>Load</c> / <c>Update</c> / <c>Delete</c> /
/// <c>FindBy{PropName}</c>) を自動生成するための属性。
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GraphNodeAttribute : Attribute
{
    /// <summary>付与クラスをグラフノードとしてマークする。<paramref name="label"/> が null のときはクラス名がラベルとして使われる。</summary>
    public GraphNodeAttribute(string? label = null) { Label = label; }

    /// <summary>明示指定されたラベル名 (省略時は <c>null</c>)。</summary>
    public string? Label { get; }
}

/// <summary>
/// 付与したプロパティをグラフプロパティとしてマークし、SourceGenerator が
/// プロパティの読み書きコードを自動生成するための属性。
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class GraphPropertyAttribute : Attribute
{
    /// <summary>プロパティをグラフプロパティとしてマークする。<paramref name="key"/> が null のときはプロパティ名がキーとして使われる。</summary>
    public GraphPropertyAttribute(string? key = null) { Key = key; }

    /// <summary>明示指定されたグラフプロパティキー (省略時は <c>null</c>)。</summary>
    public string? Key { get; }
}

/// <summary>
/// 付与したプロパティをインデックス対象としてマークし、SourceGenerator が
/// <c>InsertIndexed</c> と <c>FindBy{PropName}</c> の生成・登録を行うための属性。
/// <see cref="GraphPropertyAttribute"/> と併用が必須。
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class GraphIndexedAttribute : Attribute
{
    /// <summary>プロパティを索引対象としてマークする。<paramref name="indexName"/> が null のときは <c>idx_{label}_{propertyName}</c> が自動生成される。</summary>
    public GraphIndexedAttribute(string? indexName = null) { IndexName = indexName; }

    /// <summary>明示指定された索引名 (省略時は <c>null</c>)。</summary>
    public string? IndexName { get; }
}
