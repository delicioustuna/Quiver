using System;

namespace Quiver.Api;

/// <summary>
/// 付与したクラスを Quiver のハイパーエッジ (n 項の関係) としてマークし、
/// SourceGenerator が型安全 CRUD メソッド (<c>Insert</c> / <c>Load</c> /
/// <c>Update</c> / <c>Delete</c>) を自動生成するための属性。
/// </summary>
/// <remarks>
/// メンバー (ロールに束縛したノード) は <c>[Role]</c> 付きプロパティで宣言する。
/// ハイパーエッジのメンバー集合は作成時に確定し、以後は変更しない — 生成される
/// <c>Update</c> はプロパティのみを書き換え、ロール束縛は再構成しない。
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class HyperedgeAttribute : Attribute
{
    /// <summary>付与クラスをハイパーエッジとしてマークする。<paramref name="type"/> が null のときはクラス名が型名として使われる。</summary>
    public HyperedgeAttribute(string? type = null) { Type = type; }

    /// <summary>明示指定されたハイパーエッジ型名 (省略時は <c>null</c>)。</summary>
    public string? Type { get; }
}

/// <summary>
/// 付与したプロパティをハイパーエッジのロールとしてマークし、SourceGenerator が
/// ロールへのノード束縛の読み書きコードを自動生成するための属性。
/// </summary>
/// <remarks>
/// プロパティ型は単一メンバーなら <c>GraphNodeRef&lt;TNode&gt;</c>、複数メンバーなら
/// <c>IReadOnlyList&lt;GraphNodeRef&lt;TNode&gt;&gt;</c> とする。nullable な単一ロールは
/// 省略可能なメンバーを表す。<see cref="PropertyAttribute"/> との併用はできない。
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RoleAttribute : Attribute
{
    /// <summary>プロパティをロールとしてマークする。<paramref name="role"/> が null のときはプロパティ名がロール名として使われる。</summary>
    public RoleAttribute(string? role = null) { Role = role; }

    /// <summary>明示指定されたロール名 (省略時は <c>null</c>)。</summary>
    public string? Role { get; }
}
