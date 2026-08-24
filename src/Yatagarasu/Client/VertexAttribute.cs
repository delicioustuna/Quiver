using System;

namespace Yatagarasu.Api;

/// <summary>
/// 付与したクラスを Yatagarasu のグラフVertexとしてマークし、SourceGenerator が
/// 型安全 CRUD メソッド (<c>Insert</c> / <c>Load</c> / <c>Update</c> / <c>Delete</c> /
/// <c>FindBy{PropName}</c>) を自動生成するための属性。
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class VertexAttribute : Attribute
{
    /// <summary>付与クラスをグラフVertexとしてマークする。<paramref name="label"/> が null のときはクラス名がラベルとして使われる。</summary>
    public VertexAttribute(string? label = null) { Label = label; }

    /// <summary>明示指定されたラベル名 (省略時は <c>null</c>)。</summary>
    public string? Label { get; }
}

/// <summary>
/// 付与したプロパティをグラフプロパティとしてマークし、SourceGenerator が
/// プロパティの読み書きコードを自動生成するための属性。
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PropertyAttribute : Attribute
{
    /// <summary>プロパティをグラフプロパティとしてマークする。<paramref name="key"/> が null のときはプロパティ名がキーとして使われる。</summary>
    public PropertyAttribute(string? key = null) { Key = key; }

    /// <summary>明示指定されたグラフプロパティキー (省略時は <c>null</c>)。</summary>
    public string? Key { get; }
}

/// <summary>
/// 付与したプロパティをインデックス対象としてマークし、SourceGenerator が
/// <c>InsertIndexed</c> と <c>FindBy{PropName}</c> の生成・登録を行うための属性。
/// <see cref="PropertyAttribute"/> と併用が必須。
/// </summary>
/// <remarks>
/// 本属性は <b>SourceGenerator 向けのマーカーに過ぎず、実体の B+Tree インデックスは
/// ユーザが <c>tx.EditSchema.CreateIndex(new ScalarIndexDefinition(indexName, new PropertyTarget(PropertyOwnerKind.Vertex, propertyKey, label), kind))</c> で
/// 明示的に作成する必要がある</b>。属性の <see cref="IndexName"/> (省略時
/// <c>idx_{label}_{propertyName}</c>) と <c>CreateIndex</c> 第 1 引数を一致させること。
/// <see cref="IndexName"/> を作り忘れた場合、生成された <c>FindBy{Prop}</c> や
/// <c>IWriteTransaction.MergeVertex</c> はインデックス未登録としてフルスキャン経路に
/// フォールバックし、初回呼び出しで <see cref="System.Diagnostics.Trace.TraceWarning"/>
/// が出力される。
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IndexedAttribute : Attribute
{
    /// <summary>プロパティを索引対象としてマークする。<paramref name="indexName"/> が null のときは <c>idx_{label}_{propertyName}</c> が自動生成される。</summary>
    public IndexedAttribute(string? indexName = null) { IndexName = indexName; }

    /// <summary>明示指定された索引名 (省略時は <c>null</c>)。</summary>
    public string? IndexName { get; }

    /// <summary>
    /// 同じlabel内で文字列値を一意にする場合は<c>true</c>。
    /// 文字列のSingle cardinalityプロパティでだけ使用できる。
    /// </summary>
    public bool Unique { get; set; }
}
