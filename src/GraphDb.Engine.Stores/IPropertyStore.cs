using GraphDb.Engine.Core;

namespace GraphDb.Engine.Stores;

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
    public PropertyValueType Type => default;
    public bool BoolValue => false;
    public int Int32Value => 0;
    public long Int64Value => 0L;
    public double DoubleValue => 0.0;
    public ReadOnlySpan<byte> Utf8StringValue => default;
    public ReadOnlySpan<byte> BytesValue => default;

    public static PropertyValue FromBool(bool v) => throw new NotImplementedException();
    public static PropertyValue FromInt32(int v) => throw new NotImplementedException();
    public static PropertyValue FromInt64(long v) => throw new NotImplementedException();
    public static PropertyValue FromDouble(double v) => throw new NotImplementedException();
    public static PropertyValue FromString(ReadOnlySpan<char> v) => throw new NotImplementedException();
    public static PropertyValue FromUtf8(ReadOnlySpan<byte> v) => throw new NotImplementedException();
    public static PropertyValue FromBytes(ReadOnlySpan<byte> v) => throw new NotImplementedException();
}

public readonly ref struct PropertyReadHandle
{
    private readonly PropertyId _id;
    private readonly PropertyKeyId _keyId;
    private readonly PropertyId _nextPropertyId;

    public PropertyId Id => _id;
    public PropertyKeyId KeyId => _keyId;
    public PropertyValue Value => default;
    public PropertyId NextPropertyId => _nextPropertyId;

    internal PropertyReadHandle(PropertyId id, PropertyKeyId keyId, PropertyId nextPropertyId)
    {
        _id = id;
        _keyId = keyId;
        _nextPropertyId = nextPropertyId;
    }

    public void Dispose() { }
}

public ref struct PropertyEnumerator
{
    public bool MoveNext() => throw new NotImplementedException();
    public PropertyReadHandle Current => throw new NotImplementedException();
    public void Dispose() { }
}
