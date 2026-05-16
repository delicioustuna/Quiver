using Quiver.Stores;

namespace Quiver.Logical;

/// <summary>
/// Heap-storable mirror of <see cref="PropertyValue"/> (which is a <c>ref struct</c>
/// and therefore cannot be stored inside a recorded <see cref="LogicalMutation"/>).
/// Variable-length payloads (String / Bytes) are copied into a private array so
/// the entry remains valid after the originating transaction has been disposed.
/// </summary>
public readonly struct LogicalPropertyValue
{
    public PropertyValueType Type { get; }
    public long Scalar { get; }
    public byte[]? Bytes { get; }

    private LogicalPropertyValue(PropertyValueType type, long scalar, byte[]? bytes)
    {
        Type = type;
        Scalar = scalar;
        Bytes = bytes;
    }

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
    /// Materialize a <see cref="PropertyValue"/> from this record for replay.
    /// The returned <c>ref struct</c> borrows the heap array for String / Bytes —
    /// callers must use it before this <see cref="LogicalPropertyValue"/> goes out of scope.
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
