using System.Text;
using Quiver.Storage.Records;

namespace Quiver;

/// <summary>公開 API のプロパティ値種別。</summary>
public enum GraphValueKind : byte
{
    /// <summary>真偽値。</summary>
    Boolean = 1,

    /// <summary>32 bit 整数。</summary>
    Int32 = 2,

    /// <summary>64 bit 整数。日時値も正準化された ticks または day number として格納される。</summary>
    Int64 = 3,

    /// <summary>倍精度浮動小数点数。</summary>
    Double = 4,

    /// <summary>文字列。</summary>
    String = 5,

    /// <summary>バイト列。</summary>
    Bytes = 6,

    /// <summary>単精度浮動小数点ベクトル。</summary>
    FloatVector = 7,
}

/// <summary>
/// callback の外へ安全に保持できる、所有権付きのプロパティ値。
/// 可変長データは生成時にコピーされる。
/// </summary>
public readonly struct GraphValue : IEquatable<GraphValue>
{
    private readonly long _scalar;
    private readonly object? _reference;

    private GraphValue(GraphValueKind kind, long scalar = 0, object? reference = null)
    {
        Kind = kind;
        _scalar = scalar;
        _reference = reference;
    }

    /// <summary>値の種別。</summary>
    public GraphValueKind Kind { get; }

    /// <summary>真偽値を生成する。</summary>
    public static GraphValue FromBoolean(bool value) => new(GraphValueKind.Boolean, value ? 1 : 0);

    /// <summary>32 bit 整数を生成する。</summary>
    public static GraphValue FromInt32(int value) => new(GraphValueKind.Int32, value);

    /// <summary>64 bit 整数を生成する。</summary>
    public static GraphValue FromInt64(long value) => new(GraphValueKind.Int64, value);

    /// <summary>倍精度浮動小数点数を生成する。</summary>
    public static GraphValue FromDouble(double value) =>
        new(GraphValueKind.Double, BitConverter.DoubleToInt64Bits(value));

    /// <summary>文字列を生成する。</summary>
    public static GraphValue FromString(string value) =>
        new(GraphValueKind.String, reference: value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>コピーされたバイト列を生成する。</summary>
    public static GraphValue FromBytes(ReadOnlySpan<byte> value) =>
        new(GraphValueKind.Bytes, reference: value.ToArray());

    /// <summary>コピーされた単精度浮動小数点ベクトルを生成する。</summary>
    public static GraphValue FromFloatVector(ReadOnlySpan<float> value) =>
        new(GraphValueKind.FloatVector, reference: value.ToArray());

    /// <summary><see cref="DateTime"/> を UTC ticks へ正準化した値を生成する。</summary>
    public static GraphValue FromDateTime(DateTime value) =>
        FromInt64(TemporalCodec.ToUtcTicks(value));

    /// <summary><see cref="DateTimeOffset"/> を UTC ticks へ正準化した値を生成する。</summary>
    public static GraphValue FromDateTimeOffset(DateTimeOffset value) =>
        FromInt64(TemporalCodec.OffsetToUtcTicks(value));

    /// <summary><see cref="DateOnly"/> を day number へ正準化した値を生成する。</summary>
    public static GraphValue FromDateOnly(DateOnly value) => FromInt64(TemporalCodec.ToDayNumber(value));

    /// <summary><see cref="TimeOnly"/> を ticks へ正準化した値を生成する。</summary>
    public static GraphValue FromTimeOnly(TimeOnly value) => FromInt64(TemporalCodec.ToTicks(value));

    /// <summary><see cref="TimeSpan"/> を ticks へ正準化した値を生成する。</summary>
    public static GraphValue FromTimeSpan(TimeSpan value) => FromInt64(TemporalCodec.ToTicks(value));

    /// <summary>真偽値として取得する。</summary>
    public bool AsBoolean() => Kind == GraphValueKind.Boolean
        ? _scalar != 0
        : throw WrongKind(GraphValueKind.Boolean);

    /// <summary>32 bit 整数として取得する。</summary>
    public int AsInt32() => Kind == GraphValueKind.Int32
        ? checked((int)_scalar)
        : throw WrongKind(GraphValueKind.Int32);

    /// <summary>64 bit 整数として取得する。</summary>
    public long AsInt64() => Kind == GraphValueKind.Int64
        ? _scalar
        : throw WrongKind(GraphValueKind.Int64);

    /// <summary>倍精度浮動小数点数として取得する。</summary>
    public double AsDouble() => Kind == GraphValueKind.Double
        ? BitConverter.Int64BitsToDouble(_scalar)
        : throw WrongKind(GraphValueKind.Double);

    /// <summary>文字列として取得する。</summary>
    public string AsString() => Kind == GraphValueKind.String
        ? (string)_reference!
        : throw WrongKind(GraphValueKind.String);

    /// <summary>読み取り専用バイト列として取得する。</summary>
    public ReadOnlyMemory<byte> AsBytes() => Kind == GraphValueKind.Bytes
        ? (byte[])_reference!
        : throw WrongKind(GraphValueKind.Bytes);

    /// <summary>読み取り専用の単精度浮動小数点ベクトルとして取得する。</summary>
    public ReadOnlyMemory<float> AsFloatVector() => Kind == GraphValueKind.FloatVector
        ? (float[])_reference!
        : throw WrongKind(GraphValueKind.FloatVector);

    /// <summary>UTC <see cref="DateTime"/> として取得する。</summary>
    public DateTime AsDateTime() => TemporalCodec.FromUtcTicks(AsInt64());

    /// <summary>UTC <see cref="DateTimeOffset"/> として取得する。</summary>
    public DateTimeOffset AsDateTimeOffset() => TemporalCodec.FromUtcTicksToOffset(AsInt64());

    /// <summary><see cref="DateOnly"/> として取得する。</summary>
    public DateOnly AsDateOnly() => TemporalCodec.FromDayNumber(checked((int)AsInt64()));

    /// <summary><see cref="TimeOnly"/> として取得する。</summary>
    public TimeOnly AsTimeOnly() => TemporalCodec.ToTimeOnly(AsInt64());

    /// <summary><see cref="TimeSpan"/> として取得する。</summary>
    public TimeSpan AsTimeSpan() => TemporalCodec.ToTimeSpan(AsInt64());

    /// <summary>真偽値から暗黙変換する。</summary>
    public static implicit operator GraphValue(bool value) => FromBoolean(value);

    /// <summary>32 bit 整数から暗黙変換する。</summary>
    public static implicit operator GraphValue(int value) => FromInt32(value);

    /// <summary>64 bit 整数から暗黙変換する。</summary>
    public static implicit operator GraphValue(long value) => FromInt64(value);

    /// <summary>倍精度浮動小数点数から暗黙変換する。</summary>
    public static implicit operator GraphValue(double value) => FromDouble(value);

    /// <summary>文字列から暗黙変換する。</summary>
    public static implicit operator GraphValue(string value) => FromString(value);

    /// <inheritdoc />
    public bool Equals(GraphValue other)
    {
        if (Kind != other.Kind || _scalar != other._scalar)
            return false;
        return Kind switch
        {
            GraphValueKind.Bytes => AsBytes().Span.SequenceEqual(other.AsBytes().Span),
            GraphValueKind.FloatVector => AsFloatVector().Span.SequenceEqual(other.AsFloatVector().Span),
            GraphValueKind.String => string.Equals(AsString(), other.AsString(), StringComparison.Ordinal),
            _ => true,
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GraphValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(_scalar);
        if (Kind == GraphValueKind.String)
            hash.Add(AsString(), StringComparer.Ordinal);
        else if (Kind == GraphValueKind.Bytes)
            foreach (byte value in AsBytes().Span) hash.Add(value);
        else if (Kind == GraphValueKind.FloatVector)
            foreach (float value in AsFloatVector().Span) hash.Add(value);
        return hash.ToHashCode();
    }

    /// <summary>二つの値が等しいかを返す。</summary>
    public static bool operator ==(GraphValue left, GraphValue right) => left.Equals(right);

    /// <summary>二つの値が異なるかを返す。</summary>
    public static bool operator !=(GraphValue left, GraphValue right) => !left.Equals(right);

    internal PropertyValue ToCore() => Kind switch
    {
        GraphValueKind.Boolean => PropertyValue.FromBool(AsBoolean()),
        GraphValueKind.Int32 => PropertyValue.FromInt32(AsInt32()),
        GraphValueKind.Int64 => PropertyValue.FromInt64(AsInt64()),
        GraphValueKind.Double => PropertyValue.FromDouble(AsDouble()),
        GraphValueKind.String => PropertyValue.FromString(AsString()),
        GraphValueKind.Bytes => PropertyValue.FromBytes(AsBytes().Span),
        GraphValueKind.FloatVector => PropertyValue.FromFloatArray(AsFloatVector().Span),
        _ => throw new InvalidOperationException($"未初期化または未知の GraphValueKind: {Kind}"),
    };

    internal static GraphValue FromCore(PropertyValue value) => value.Type switch
    {
        PropertyValueType.Bool => FromBoolean(value.BoolValue),
        PropertyValueType.Int32 => FromInt32(value.Int32Value),
        PropertyValueType.Int64 => FromInt64(value.Int64Value),
        PropertyValueType.Double => FromDouble(value.DoubleValue),
        PropertyValueType.String => FromString(Encoding.UTF8.GetString(value.Utf8StringValue)),
        PropertyValueType.Bytes => FromBytes(value.BytesValue),
        PropertyValueType.FloatArray => FromFloatVector(value.FloatArrayValue),
        _ => throw new NotSupportedException($"PropertyValueType {value.Type} は公開値へ変換できません。"),
    };

    private InvalidOperationException WrongKind(GraphValueKind expected) =>
        new($"{Kind} の値を {expected} として取得することはできません。");
}
