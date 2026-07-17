using System.Runtime.InteropServices;
using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Storage.Records;

internal interface IPropertyStore
{
    PropertyVersionRef Create(
        PropertyAddress address,
        PropertyCardinality cardinality,
        in PropertyValue value,
        PropertyVersionRef currentFirst);
    PropertyVersionRef Delete(EntityRef owner, PropertyVersionRef version, PropertyVersionRef currentFirst);
    PropertyVersionRecord Read(EntityRef owner, PropertyVersionRef version);
    PropertyCursor Enumerate(EntityRef owner, PropertyVersionRef firstVersion);
}

internal interface ITransactionPropertyStore
{
    PropertyVersionRef Create(
        PropertyAddress address,
        PropertyCardinality cardinality,
        in PropertyValue value,
        PropertyVersionRef currentFirst,
        TransactionId transactionId,
        VersionVisible visibility);
    PropertyVersionRef Delete(
        EntityRef owner,
        PropertyVersionRef version,
        PropertyVersionRef currentFirst,
        TransactionId transactionId,
        VersionVisible visibility);
    PropertyVersionRecord Read(
        EntityRef owner,
        PropertyVersionRef version,
        VersionVisible visibility);
}

internal readonly record struct PropertyVersionRef(long Value)
{
    internal static readonly PropertyVersionRef Invalid = new(-1);

    internal bool IsValid => Value >= 0;

    internal long Sequence => Value < 0 ? -1 : EntityRef.UnpackSequence(Value);

    internal int Generation => Value < 0 ? 0 : EntityRef.UnpackGeneration(Value);

    internal static PropertyVersionRef Create(long sequence, int generation)
        => new(EntityRef.PackLocal(sequence, generation));

    internal static PropertyVersionRef FromSequence(long sequence)
        => sequence < 0 ? Invalid : new(sequence);
}

/// <summary>プロパティ値の物理型。オンディスクのプロパティレコードに 1 バイトで格納される。</summary>
public enum PropertyValueType : byte
{
    /// <summary>真偽値 (<see cref="bool"/>)。</summary>
    Bool = 1,
    /// <summary>32bit 整数 (<see cref="int"/>)。</summary>
    Int32 = 2,
    /// <summary>64bit 整数 (<see cref="long"/>)。日時系 CLR 型も Int64 に正準化して格納する。</summary>
    Int64 = 3,
    /// <summary>倍精度浮動小数点 (<see cref="double"/>)。</summary>
    Double = 4,
    /// <summary>UTF-8 文字列。</summary>
    String = 5,
    /// <summary>任意のバイト列。</summary>
    Bytes = 6,
    /// <summary>単精度浮動小数点配列 (<see cref="float"/>[]）。内部表現はバイト列 (<see cref="System.Runtime.InteropServices.MemoryMarshal"/> 経由)。</summary>
    FloatArray = 7,
}

/// <summary>
/// プロパティの 1 値を型タグ付きで保持する読み取り専用ビュー。スカラ値はインラインの long に、
/// 文字列 / バイト列は <see cref="ReadOnlySpan{T}"/> に保持する (アロケーションを避けるため ref struct)。
/// </summary>
public readonly ref struct PropertyValue
{
    private readonly long _scalar;
    private readonly ReadOnlySpan<byte> _span;
    private readonly PropertyValueType _type;

    private PropertyValue(PropertyValueType type, long scalar = 0, ReadOnlySpan<byte> span = default)
    {
        _type = type; _scalar = scalar; _span = span;
    }

    /// <summary>この値の物理型。各 <c>XxxValue</c> アクセサは対応する型のときのみ有効。</summary>
    public PropertyValueType Type => _type;
    /// <summary>真偽値として解釈した値 (<see cref="PropertyValueType.Bool"/> 用)。</summary>
    public bool BoolValue => _scalar != 0;
    /// <summary>32bit 整数として解釈した値 (<see cref="PropertyValueType.Int32"/> 用)。</summary>
    public int Int32Value => (int)_scalar;
    /// <summary>64bit 整数として解釈した値 (<see cref="PropertyValueType.Int64"/> 用)。</summary>
    public long Int64Value => _scalar;
    /// <summary>倍精度浮動小数点として解釈した値 (<see cref="PropertyValueType.Double"/> 用)。</summary>
    public double DoubleValue => BitConverter.Int64BitsToDouble(_scalar);
    /// <summary>UTF-8 バイト列としての文字列値 (<see cref="PropertyValueType.String"/> 用)。</summary>
    public ReadOnlySpan<byte> Utf8StringValue => _span;
    /// <summary>生のバイト列値 (<see cref="PropertyValueType.Bytes"/> 用)。</summary>
    public ReadOnlySpan<byte> BytesValue => _span;
    /// <summary>単精度浮動小数点配列として解釈した値 (<see cref="PropertyValueType.FloatArray"/> 用)。</summary>
    public ReadOnlySpan<float> FloatArrayValue => MemoryMarshal.Cast<byte, float>(_span);

    /// <summary><see cref="bool"/> から <see cref="PropertyValue"/> を生成する。</summary>
    public static PropertyValue FromBool(bool v) => new(PropertyValueType.Bool, v ? 1L : 0L);
    /// <summary><see cref="int"/> から <see cref="PropertyValue"/> を生成する。</summary>
    public static PropertyValue FromInt32(int v) => new(PropertyValueType.Int32, v);
    /// <summary><see cref="long"/> から <see cref="PropertyValue"/> を生成する。</summary>
    public static PropertyValue FromInt64(long v) => new(PropertyValueType.Int64, v);
    /// <summary><see cref="double"/> から <see cref="PropertyValue"/> を生成する。</summary>
    public static PropertyValue FromDouble(double v) => new(PropertyValueType.Double, BitConverter.DoubleToInt64Bits(v));
    /// <summary>文字列を UTF-8 にエンコードして <see cref="PropertyValue"/> を生成する。</summary>
    public static PropertyValue FromString(ReadOnlySpan<char> v)
    {
        int len = System.Text.Encoding.UTF8.GetByteCount(v);
        byte[] buf = new byte[len];
        System.Text.Encoding.UTF8.GetBytes(v, buf);
        return new(PropertyValueType.String, 0, buf);
    }
    /// <summary>既に UTF-8 エンコード済みのバイト列を文字列値として包む。</summary>
    public static PropertyValue FromUtf8(ReadOnlySpan<byte> v) => new(PropertyValueType.String, 0, v);
    /// <summary>任意のバイト列を <see cref="PropertyValueType.Bytes"/> 値として包む。</summary>
    public static PropertyValue FromBytes(ReadOnlySpan<byte> v) => new(PropertyValueType.Bytes, 0, v);
    /// <summary>単精度浮動小数点配列を <see cref="PropertyValueType.FloatArray"/> 値として包む。</summary>
    public static PropertyValue FromFloatArray(ReadOnlySpan<float> v) => new(PropertyValueType.FloatArray, 0, MemoryMarshal.AsBytes(v));

    // 日時系 — 物理は Int64。正準化は TemporalCodec に集約 (TimeZone 契約はそこ参照)。
    /// <summary><see cref="DateTime"/> を UTC ticks に正準化して格納する。</summary>
    public static PropertyValue FromDateTime(DateTime v) => new(PropertyValueType.Int64, TemporalCodec.ToUtcTicks(v));
    /// <summary><see cref="DateTimeOffset"/> を UTC ticks に正準化して格納する。</summary>
    public static PropertyValue FromDateTimeOffset(DateTimeOffset v) => new(PropertyValueType.Int64, TemporalCodec.OffsetToUtcTicks(v));
    /// <summary><see cref="DateOnly"/> を day number として格納する。</summary>
    public static PropertyValue FromDateOnly(DateOnly v) => new(PropertyValueType.Int64, TemporalCodec.ToDayNumber(v));
    /// <summary><see cref="TimeOnly"/> を ticks として格納する。</summary>
    public static PropertyValue FromTimeOnly(TimeOnly v) => new(PropertyValueType.Int64, TemporalCodec.ToTicks(v));
    /// <summary><see cref="TimeSpan"/> を ticks として格納する。</summary>
    public static PropertyValue FromTimeSpan(TimeSpan v) => new(PropertyValueType.Int64, TemporalCodec.ToTicks(v));

    /// <summary>UTC 正準化された <see cref="DateTime"/> (<see cref="DateTimeKind.Utc"/>)。</summary>
    public DateTime DateTimeValue => TemporalCodec.FromUtcTicks(_scalar);
    /// <summary>オフセット 0 (UTC) の <see cref="DateTimeOffset"/>。</summary>
    public DateTimeOffset DateTimeOffsetValue => TemporalCodec.FromUtcTicksToOffset(_scalar);
    /// <summary>格納された day number から復元した <see cref="DateOnly"/>。</summary>
    public DateOnly DateOnlyValue => TemporalCodec.FromDayNumber(_scalar);
    /// <summary>格納された ticks から復元した <see cref="TimeOnly"/>。</summary>
    public TimeOnly TimeOnlyValue => TemporalCodec.ToTimeOnly(_scalar);
    /// <summary>格納された ticks から復元した <see cref="TimeSpan"/>。</summary>
    public TimeSpan TimeSpanValue => TemporalCodec.ToTimeSpan(_scalar);

    /// <summary>この値をレコードに格納したときのバイト数 (可変長型は span 長)。</summary>
    public int EncodedSize => _type switch
    {
        PropertyValueType.Bool => 1,
        PropertyValueType.Int32 => 4,
        PropertyValueType.Int64 => 8,
        PropertyValueType.Double => 8,
        _ => _span.Length,
    };
}

internal readonly ref struct PropertyVersionRecord
{
    internal PropertyVersionRecord(
        PropertyVersionRef version,
        PropertyAddress address,
        PropertyCardinality cardinality,
        PropertyValue value,
        PropertyVersionRef previousVersion,
        PropertyVersionRef nextOwnedProperty,
        long xmin,
        long xmax,
        bool inUse)
    {
        Version = version;
        Address = address;
        Cardinality = cardinality;
        Value = value;
        PreviousVersion = previousVersion;
        NextOwnedProperty = nextOwnedProperty;
        Xmin = xmin;
        Xmax = xmax;
        InUse = inUse;
    }

    internal PropertyVersionRef Version { get; }
    internal PropertyAddress Address { get; }
    internal PropertyCardinality Cardinality { get; }
    internal PropertyValue Value { get; }
    internal PropertyVersionRef PreviousVersion { get; }
    internal PropertyVersionRef NextOwnedProperty { get; }
    internal long Xmin { get; }
    internal long Xmax { get; }
    internal bool InUse { get; }
}

/// <summary>所有者に属するプロパティのキー、多重度、値を表します。物理レコードIDは公開しません。</summary>
public readonly ref struct PropertyEntry
{
    internal PropertyEntry(PropertyKeyId keyId, PropertyCardinality cardinality, PropertyValue value)
    {
        KeyId = keyId;
        Cardinality = cardinality;
        Value = value;
    }

    /// <summary>プロパティキー。</summary>
    public PropertyKeyId KeyId { get; }

    /// <summary>プロパティキーの多重度。</summary>
    public PropertyCardinality Cardinality { get; }

    /// <summary>プロパティ値。</summary>
    public PropertyValue Value { get; }
}

/// <summary>単一エンティティの可視なプロパティを列挙する前方カーソルです。</summary>
public ref struct PropertyCursor
{
    private readonly IPropertyStore _store;
    private readonly EntityRef _owner;
    private PropertyVersionRef _next;
    private PropertyVersionRecord _record;
    private PropertyEntry _current;
    private bool _started;
    private TransactionUsageGuard? _usageGuard;

    internal PropertyCursor(IPropertyStore store, EntityRef owner, PropertyVersionRef firstVersion)
    {
        _store = store;
        _owner = owner;
        _next = firstVersion;
        _record = default;
        _current = default;
        _started = false;
        _usageGuard = null;
    }

    internal void AttachUsage(TransactionUsageLease usage)
    {
        _usageGuard = usage.Guard;
        usage.Dispose();
    }

    /// <summary>次の可視なプロパティへ進みます。</summary>
    public bool MoveNext()
    {
        using var usage = _usageGuard?.Enter() ?? default;
        if (_started)
            _next = _record.NextOwnedProperty;
        _started = true;

        while (_next.IsValid)
        {
            _record = _store.Read(_owner, _next);
            if (_record.InUse)
            {
                _current = new PropertyEntry(
                    _record.Address.Key,
                    _record.Cardinality,
                    _record.Value);
                return true;
            }
            _next = _record.NextOwnedProperty;
        }
        return false;
    }

    /// <summary>現在のプロパティ。</summary>
    public PropertyEntry Current => _current;

    internal PropertyVersionRef CurrentVersion => _record.Version;

    /// <summary>カーソルを破棄します。現在の実装では処理を行いません。</summary>
    public void Dispose() { }
}

/// <summary>
/// 特定キーのプロパティ値のみを列挙する前方イテレータ (Set cardinality 用)。
/// <see cref="PropertyCursor"/> をラップし、指定 <see cref="PropertyKeyId"/> に一致する
/// エントリだけを返す。
/// </summary>
public ref struct PropertyValuesEnumerator
{
    private PropertyCursor _inner;
    private readonly PropertyKeyId _keyId;

    internal PropertyValuesEnumerator(PropertyCursor inner, PropertyKeyId keyId)
    {
        _inner = inner;
        _keyId = keyId;
    }

    /// <summary>次の一致するプロパティ値へ進む。</summary>
    public bool MoveNext()
    {
        while (_inner.MoveNext())
        {
            if (_inner.Current.KeyId == _keyId)
                return true;
        }
        return false;
    }

    /// <summary>直近の <see cref="MoveNext"/> で取得した値。</summary>
    public PropertyValue Current => _inner.Current.Value;

    /// <summary>内部イテレータを破棄する。</summary>
    public void Dispose() => _inner.Dispose();
}
