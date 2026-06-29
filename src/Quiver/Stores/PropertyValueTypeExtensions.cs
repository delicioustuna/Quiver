using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// <see cref="PropertyValueType"/> から <see cref="PropertyTypeFlags"/> への分岐なしルックアップ。
/// </summary>
public static class PropertyValueTypeExtensions
{
    private static readonly PropertyTypeFlags[] s_table =
    [
        PropertyTypeFlags.None,       // 0 (unused)
        PropertyTypeFlags.Bool,       // 1
        PropertyTypeFlags.Int32,      // 2
        PropertyTypeFlags.Int64,      // 3
        PropertyTypeFlags.Double,     // 4
        PropertyTypeFlags.String,     // 5
        PropertyTypeFlags.Bytes,      // 6
        PropertyTypeFlags.FloatArray, // 7
    ];

    /// <summary>プロパティ値型を対応する <see cref="PropertyTypeFlags"/> ビットへ変換する。</summary>
    public static PropertyTypeFlags ToFlags(this PropertyValueType type)
    {
        uint i = (uint)type;
        return i < (uint)s_table.Length ? s_table[i] : PropertyTypeFlags.None;
    }

    /// <summary>
    /// <paramref name="type"/> が <paramref name="mask"/> に含まれるビットのいずれかに該当する場合 <c>true</c> を返す。
    /// </summary>
    public static bool IsCompatibleWith(this PropertyValueType type, PropertyTypeFlags mask)
        => (type.ToFlags() & mask) != PropertyTypeFlags.None;
}
