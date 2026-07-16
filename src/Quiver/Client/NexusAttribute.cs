using System;

namespace Quiver.Api;

/// <summary>
/// 付与したクラスを Quiver のNexus (n 項の関係) としてマークし、
/// SourceGenerator が型安全 CRUD メソッド (<c>Insert</c> / <c>Load</c> /
/// <c>Update</c> / <c>Delete</c>) を自動生成するための属性。
/// </summary>
/// <remarks>
/// メンバー (ロールに束縛したVertex) は <c>[Role]</c> 付きプロパティで宣言する。
/// Nexusのメンバー集合は作成時に確定し、以後は変更しない — 生成される
/// <c>Update</c> はプロパティのみを書き換え、ロール束縛は再構成しない。
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NexusAttribute : Attribute
{
    /// <summary>付与クラスをNexusとしてマークする。<paramref name="type"/> が null のときはクラス名が型名として使われる。</summary>
    public NexusAttribute(string? type = null) { Type = type; }

    /// <summary>明示指定されたNexus型名 (省略時は <c>null</c>)。</summary>
    public string? Type { get; }
}

/// <summary>
/// 付与したプロパティをNexusのロールとしてマークし、SourceGenerator が
/// ロールへのVertex束縛の読み書きコードを自動生成するための属性。
/// </summary>
/// <remarks>
/// プロパティ型は単一メンバーなら <c>GraphVertexRef&lt;TVertex&gt;</c>、複数メンバーなら
/// <c>IReadOnlyList&lt;GraphVertexRef&lt;TVertex&gt;&gt;</c> とする。nullable な単一ロールは
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
