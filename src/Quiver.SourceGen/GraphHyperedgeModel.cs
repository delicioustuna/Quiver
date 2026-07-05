namespace Quiver.SourceGen;

internal sealed class GraphHyperedgeModel
{
    public string Namespace { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string HyperedgeType { get; set; } = "";

    /// <summary>
    /// 付与クラスが public 宣言かどうか。トラバーサル糖衣の拡張クラスは
    /// シグネチャに付与クラス型を含むため、アクセシビリティを揃えて emit する
    /// (internal クラスへ public 拡張を生成するとアクセシビリティ不整合になる)。
    /// </summary>
    public bool IsPublic { get; set; }

    public List<RoleModel> Roles { get; } = new();
    public List<PropertyModel> Properties { get; } = new();
}

internal sealed class RoleModel
{
    /// <summary>C# プロパティ名。</summary>
    public string PropertyName { get; set; } = "";

    /// <summary>グラフ上のロール名 (<c>[Role("...")]</c> 省略時はプロパティ名)。</summary>
    public string RoleName { get; set; } = "";

    /// <summary>束縛先ノード型の完全修飾名 (<c>global::Ns.Person</c>)。</summary>
    public string NodeFqn { get; set; } = "";

    /// <summary><c>IReadOnlyList&lt;GraphNodeRef&lt;TNode&gt;&gt;</c> 型で複数メンバーを束縛する。</summary>
    public bool IsMultiValued { get; set; }

    /// <summary>nullable な単一ロール — 省略可能なメンバーを表す。</summary>
    public bool IsOptional { get; set; }
}
