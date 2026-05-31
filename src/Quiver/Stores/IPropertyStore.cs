using Quiver.Core;

namespace Quiver.Storage.Records;

public interface IPropertyStore
{
    PropertyId Create(PropertyKeyId keyId, in PropertyValue value, PropertyId currentFirst);
    PropertyId Delete(PropertyId propId, PropertyId currentFirst);
    PropertyReadHandle Read(PropertyId propId);
    PropertyEnumerator Enumerate(PropertyId firstPropId);
}

public enum PropertyValueType : byte
{
    Bool = 1,
    Int32 = 2,
    Int64 = 3,
    Double = 4,
    String = 5,
    Bytes = 6,
}

public readonly ref struct PropertyValue
{
    private readonly long _scalar;
    private readonly ReadOnlySpan<byte> _span;
    private readonly PropertyValueType _type;

    private PropertyValue(PropertyValueType type, long scalar = 0, ReadOnlySpan<byte> span = default)
    {
        _type = type; _scalar = scalar; _span = span;
    }

    public PropertyValueType Type => _type;
    public bool BoolValue => _scalar != 0;
    public int Int32Value => (int)_scalar;
    public long Int64Value => _scalar;
    public double DoubleValue => BitConverter.Int64BitsToDouble(_scalar);
    public ReadOnlySpan<byte> Utf8StringValue => _span;
    public ReadOnlySpan<byte> BytesValue => _span;

    public static PropertyValue FromBool(bool v) => new(PropertyValueType.Bool, v ? 1L : 0L);
    public static PropertyValue FromInt32(int v) => new(PropertyValueType.Int32, v);
    public static PropertyValue FromInt64(long v) => new(PropertyValueType.Int64, v);
    public static PropertyValue FromDouble(double v) => new(PropertyValueType.Double, BitConverter.DoubleToInt64Bits(v));
    public static PropertyValue FromString(ReadOnlySpan<char> v)
    {
        int len = System.Text.Encoding.UTF8.GetByteCount(v);
        byte[] buf = new byte[len];
        System.Text.Encoding.UTF8.GetBytes(v, buf);
        return new(PropertyValueType.String, 0, buf);
    }
    public static PropertyValue FromUtf8(ReadOnlySpan<byte> v) => new(PropertyValueType.String, 0, v);
    public static PropertyValue FromBytes(ReadOnlySpan<byte> v) => new(PropertyValueType.Bytes, 0, v);

    public int EncodedSize => _type switch
    {
        PropertyValueType.Bool => 1,
        PropertyValueType.Int32 => 4,
        PropertyValueType.Int64 => 8,
        PropertyValueType.Double => 8,
        _ => _span.Length,
    };
}

public readonly ref struct PropertyReadHandle
{
    private readonly PropertyId _id;
    private readonly PropertyKeyId _keyId;
    private readonly PropertyId _nextPropertyId;
    private readonly PropertyValue _value;
    private readonly bool _inUse;

    /// <summary>FT-26: inUse 既定 true で旧呼出元 (BulkLoader 等) と互換。</summary>
    internal PropertyReadHandle(PropertyId id, PropertyKeyId keyId, PropertyId nextPropId, PropertyValue value, bool inUse = true)
    {
        _id = id; _keyId = keyId; _nextPropertyId = nextPropId; _value = value; _inUse = inUse;
    }

    public PropertyId Id => _id;
    public PropertyKeyId KeyId => _keyId;
    public PropertyValue Value => _value;
    public PropertyId NextPropertyId => _nextPropertyId;
    /// <summary>
    /// FT-26: MVCC visibility 判定の結果。false の場合は論理削除 / 不可視で、enumerate は skip すべき。
    /// </summary>
    public bool InUse => _inUse;
    public void Dispose() { }
}

public ref struct PropertyEnumerator
{
    private readonly IPropertyStore _store;
    private PropertyId _nextId;
    private PropertyReadHandle _current;
    private bool _started;

    internal PropertyEnumerator(IPropertyStore store, PropertyId firstId)
    {
        _store = store; _nextId = firstId; _started = false; _current = default;
    }

    public bool MoveNext()
    {
        if (_started) _nextId = _current.NextPropertyId;
        _started = true;
        // FT-26: 論理削除 / invisible な record はチェーンを進める。
        while (_nextId.IsValid)
        {
            _current = _store.Read(_nextId);
            if (_current.InUse) return true;
            _nextId = _current.NextPropertyId;
        }
        return false;
    }

    public PropertyReadHandle Current => _current;
    public void Dispose() { }
}
