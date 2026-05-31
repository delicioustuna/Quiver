using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// Branchless lookup from <see cref="PropertyValueType"/> to <see cref="PropertyTypeFlags"/>.
/// BA-8.
/// </summary>
public static class PropertyValueTypeExtensions
{
    // PropertyValueType values are 1..6 (no zero, no gaps). We size the table to 8
    // so the JIT can elide bounds checks for the (uint)type < table.Length compare,
    // and we keep the unused slots at 0 so an out-of-range or zero input yields None.
    private static readonly PropertyTypeFlags[] s_table =
    [
        PropertyTypeFlags.None,    // 0 (unused)
        PropertyTypeFlags.Bool,    // 1
        PropertyTypeFlags.Int32,   // 2
        PropertyTypeFlags.Int64,   // 3
        PropertyTypeFlags.Double,  // 4
        PropertyTypeFlags.String,  // 5
        PropertyTypeFlags.Bytes,   // 6
        PropertyTypeFlags.None,    // 7 (reserved)
    ];

    public static PropertyTypeFlags ToFlags(this PropertyValueType type)
    {
        uint i = (uint)type;
        return i < (uint)s_table.Length ? s_table[i] : PropertyTypeFlags.None;
    }

    /// <summary>
    /// Returns true when <paramref name="value"/> is one of the bits set in <paramref name="mask"/>.
    /// </summary>
    public static bool IsCompatibleWith(this PropertyValueType type, PropertyTypeFlags mask)
        => (type.ToFlags() & mask) != PropertyTypeFlags.None;
}
