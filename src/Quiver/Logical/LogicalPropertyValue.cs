using Quiver.Storage.Records;

namespace Quiver.Logical;

/// <summary>
/// <see cref="PropertyValue"/> のヒープ保管可能なミラー型 (<c>ref struct</c> である PropertyValue は
/// 記録された <see cref="LogicalMutation"/> 内に保持できないため必要)。
/// 可変長 payload (String / Bytes) は内部配列にコピーするため、発行元のトランザクションが
/// 破棄された後もエントリは有効なまま残る。
/// </summary>
public readonly struct LogicalPropertyValue
{
    /// <summary>プロパティ値の型。</summary>
    public PropertyValueType Type { get; }

    /// <summary>スカラ表現 (Bool / Int32 / Int64 / Double のビット表現)。</summary>
    public long Scalar { get; }

    /// <summary>可変長 payload (String / Bytes 用)。それ以外の型は <c>null</c>。</summary>
    public byte[]? Bytes { get; }

    private LogicalPropertyValue(PropertyValueType type, long scalar, byte[]? bytes)
    {
        Type = type;
        Scalar = scalar;
        Bytes = bytes;
    }

    /// <summary>ref struct の <see cref="PropertyValue"/> をヒープ保持可能な形でキャプチャする。</summary>
    public static LogicalPropertyValue Capture(in PropertyValue value) => value.Type switch
    {
        PropertyValueType.Bool   => new(PropertyValueType.Bool,   value.BoolValue ? 1L : 0L, null),
        PropertyValueType.Int32  => new(PropertyValueType.Int32,  value.Int32Value, null),
        PropertyValueType.Int64  => new(PropertyValueType.Int64,  value.Int64Value, null),
        PropertyValueType.Double => new(PropertyValueType.Double, BitConverter.DoubleToInt64Bits(value.DoubleValue), null),
        PropertyValueType.String => new(PropertyValueType.String, 0, value.Utf8StringValue.ToArray()),
        PropertyValueType.Bytes  => new(PropertyValueType.Bytes,  0, value.BytesValue.ToArray()),
        _ => default,
    };

    /// <summary>
    /// replay 用にこのレコードから <see cref="PropertyValue"/> をマテリアライズする。
    /// 返却される <c>ref struct</c> は String / Bytes のヒープ配列を借用しているため、
    /// 呼び出し側はこの <see cref="LogicalPropertyValue"/> のスコープが終わる前に使用する必要がある。
    /// </summary>
    public PropertyValue ToPropertyValue() => Type switch
    {
        PropertyValueType.Bool   => PropertyValue.FromBool(Scalar != 0),
        PropertyValueType.Int32  => PropertyValue.FromInt32((int)Scalar),
        PropertyValueType.Int64  => PropertyValue.FromInt64(Scalar),
        PropertyValueType.Double => PropertyValue.FromDouble(BitConverter.Int64BitsToDouble(Scalar)),
        PropertyValueType.String => PropertyValue.FromUtf8(Bytes ?? []),
        PropertyValueType.Bytes  => PropertyValue.FromBytes(Bytes ?? []),
        _ => default,
    };
}
