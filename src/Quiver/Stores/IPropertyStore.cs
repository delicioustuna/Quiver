using System.Runtime.InteropServices;
using Quiver.Core;

namespace Quiver.Storage.Records;

internal interface IPropertyStore
{
    PropertyId Create(PropertyKeyId keyId, in PropertyValue value, PropertyId currentFirst);
    PropertyId Delete(PropertyId propId, PropertyId currentFirst);
    PropertyReadHandle Read(PropertyId propId);
    PropertyEnumerator Enumerate(PropertyId firstPropId);
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

/// <summary>
/// プロパティチェーンの 1 エントリを読み出したハンドル。ID / キー / 値と、チェーン上の次エントリ
/// への参照、および MVCC 可視性フラグを保持する。
/// </summary>
public readonly ref struct PropertyReadHandle
{
    private readonly PropertyId _id;
    private readonly PropertyKeyId _keyId;
    private readonly PropertyId _nextPropertyId;
    private readonly PropertyValue _value;
    private readonly bool _inUse;

    // inUse 既定 true で旧呼出元 (BulkLoader 等) と互換。
    internal PropertyReadHandle(PropertyId id, PropertyKeyId keyId, PropertyId nextPropId, PropertyValue value, bool inUse = true)
    {
        _id = id; _keyId = keyId; _nextPropertyId = nextPropId; _value = value; _inUse = inUse;
    }

    /// <summary>このプロパティレコードの ID。</summary>
    public PropertyId Id => _id;
    /// <summary>プロパティキーの ID。</summary>
    public PropertyKeyId KeyId => _keyId;
    /// <summary>プロパティ値。</summary>
    public PropertyValue Value => _value;
    /// <summary>同一エンティティのプロパティチェーン上の次エントリ ID (終端は <see cref="PropertyId.Invalid"/>)。</summary>
    public PropertyId NextPropertyId => _nextPropertyId;
    /// <summary>
    /// MVCC visibility 判定の結果。false の場合は論理削除 / 不可視で、enumerate は skip すべき。
    /// </summary>
    public bool InUse => _inUse;
    /// <summary>ハンドルを破棄する (現状は no-op)。</summary>
    public void Dispose() { }
}

/// <summary>
/// 単一エンティティのプロパティを inline 領域 → overflow チェーンの順に列挙する前方イテレータ。
/// MVCC 不可視のチェーンエントリは自動的にスキップする。
/// </summary>
public ref struct PropertyEnumerator
{
    private readonly IPropertyStore _store;
    private PropertyId _nextId;
    private PropertyReadHandle _current;
    private bool _chainStarted;

    // entity の inline property 領域を chain より先に列挙する (chain-only は空)。
    private readonly ReadOnlySpan<byte> _inline;
    private readonly int _inlineCount;
    private int _inlineIndex;
    private int _inlinePos;

    internal PropertyEnumerator(IPropertyStore store, PropertyId firstId)
        : this(default, store, firstId, InlinePropertyCodec.VertexFixedSize) { }

    internal PropertyEnumerator(ReadOnlySpan<byte> inlinePayload, IPropertyStore store, PropertyId firstId, int fixedSize)
    {
        _store = store; _nextId = firstId; _chainStarted = false; _current = default;
        _inline = inlinePayload;
        _inlineCount = InlinePropertyCodec.Count(inlinePayload, fixedSize);
        _inlineIndex = 0;
        _inlinePos = InlinePropertyCodec.BaseSize(fixedSize);
    }

    /// <summary>次のプロパティへ進む。可視なエントリがあれば <c>true</c>、列挙完了で <c>false</c>。</summary>
    public bool MoveNext()
    {
        // Phase 1: inline entries (すべて visible 版由来なので skip 不要)。
        if (_inlineIndex < _inlineCount)
        {
            var (keyId, type, nextPos) = InlinePropertyCodec.ReadEntryHeader(_inline, _inlinePos);
            var val = InlinePropertyCodec.ValueAt(_inline, _inlinePos);
            _inlinePos = nextPos;
            _inlineIndex++;
            _current = new PropertyReadHandle(
                PropertyId.Invalid, new PropertyKeyId(keyId), PropertyId.Invalid,
                InlinePropertyCodec.Decode(type, val), inUse: true);
            return true;
        }

        // Phase 2: overflow チェーン。論理削除 / invisible はチェーンを進める。
        if (_chainStarted) _nextId = _current.NextPropertyId;
        _chainStarted = true;
        while (_nextId.IsValid)
        {
            _current = _store.Read(_nextId);
            if (_current.InUse) return true;
            _nextId = _current.NextPropertyId;
        }
        return false;
    }

    /// <summary>現在指しているプロパティの読み取りハンドル。</summary>
    public PropertyReadHandle Current => _current;
    /// <summary>イテレータを破棄する (現状は no-op)。</summary>
    public void Dispose() { }
}

/// <summary>
/// 特定キーのプロパティ値のみを列挙する前方イテレータ (Set cardinality 用)。
/// <see cref="PropertyEnumerator"/> をラップし、指定 <see cref="PropertyKeyId"/> に一致する
/// エントリだけを返す。
/// </summary>
public ref struct PropertyValuesEnumerator
{
    private PropertyEnumerator _inner;
    private readonly PropertyKeyId _keyId;

    internal PropertyValuesEnumerator(PropertyEnumerator inner, PropertyKeyId keyId)
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
