namespace Quiver.Core;

/// <summary>
/// プロパティ値がとり得るランタイム型の集合表現。スキャン / オプティマイザ /
/// インデックス / 統計の hot path で使われ、述語評価とインデックス参照が
/// 型互換でない値をマテリアライズせずに排除できるようにする。
/// Array 系ビットは将来の LPG 配列プロパティ用に予約。埋め込みベクトルはここでは
/// 表現しない (詳細は <c>IVectorStore</c> を参照)。
/// </summary>
[Flags]
public enum PropertyTypeFlags : ulong
{
    /// <summary>型情報なし。</summary>
    None        = 0,

    // スカラビット — Quiver.Storage.Records.PropertyValueType の値 1..6 に対応。

    /// <summary><see cref="bool"/>。</summary>
    Bool        = 1UL << 1,
    /// <summary><see cref="int"/>。</summary>
    Int32       = 1UL << 2,
    /// <summary><see cref="long"/>。</summary>
    Int64       = 1UL << 3,
    /// <summary><see cref="double"/>。</summary>
    Double      = 1UL << 4,
    /// <summary>UTF-8 文字列。</summary>
    String      = 1UL << 5,
    /// <summary>任意バイト列。</summary>
    Bytes       = 1UL << 6,
    /// <summary>単精度浮動小数点配列 (<see cref="float"/>[]）。</summary>
    FloatArray  = 1UL << 7,

    // Array 系ビット — スカラレーンと 1 対 1 対応で、将来の LPG 配列プロパティ用に予約 (埋め込みベクトルではない)。

    /// <summary><see cref="bool"/> 配列 (予約)。</summary>
    BoolArray   = 1UL << 17,
    /// <summary><see cref="int"/> 配列 (予約)。</summary>
    Int32Array  = 1UL << 18,
    /// <summary><see cref="long"/> 配列 (予約)。</summary>
    Int64Array  = 1UL << 19,
    /// <summary><see cref="double"/> 配列 (予約)。</summary>
    DoubleArray = 1UL << 20,
    /// <summary>文字列配列 (予約)。</summary>
    StringArray = 1UL << 21,
    /// <summary>バイト列配列 (予約)。</summary>
    BytesArray  = 1UL << 22,

    // 複合マスク

    /// <summary>数値スカラ全般。</summary>
    Numeric         = Int32 | Int64 | Double,
    /// <summary>スカラ全般。</summary>
    Scalar          = Bool | Int32 | Int64 | Double | String | Bytes,
    /// <summary>数値配列全般。</summary>
    NumericArray    = Int32Array | Int64Array | DoubleArray,
    /// <summary>配列全般。</summary>
    Array           = BoolArray | Int32Array | Int64Array | DoubleArray | StringArray | BytesArray,
    /// <summary>可変長型 (文字列 / バイト列 / float 配列 / LPG 配列)。</summary>
    Variable        = String | Bytes | FloatArray | Array,
    /// <summary>順序比較可能型 (数値 / 文字列)。</summary>
    Comparable      = Numeric | String,
}
