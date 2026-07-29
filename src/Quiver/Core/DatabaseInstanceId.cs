namespace Quiver;

/// <summary>
/// graph export の出所を識別するデータベースインスタンス ID。
/// entity ID や認証境界としては使用しない。
/// </summary>
/// <param name="Value">データベースインスタンスを表す UUID。</param>
public readonly record struct DatabaseInstanceId(Guid Value)
{
    /// <summary>新しいデータベースインスタンス ID を生成する。</summary>
    public static DatabaseInstanceId New() => new(Guid.NewGuid());

    /// <summary>UUID の標準文字列表現を返す。</summary>
    public override string ToString() => Value.ToString("D");
}
