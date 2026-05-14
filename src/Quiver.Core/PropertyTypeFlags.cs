namespace Quiver.Core;

/// <summary>
/// Set-valued representation of the runtime types a property value can take.
/// Used by scan / optimizer / index / statistics hot paths so predicate evaluation
/// and index lookups can reject type-incompatible values without materializing them.
///
/// BA-8. Array bits are reserved for future LPG array properties; embedding vectors
/// are not represented here (see <c>IVectorStore</c> / VEC-1).
/// </summary>
[Flags]
public enum PropertyTypeFlags : ulong
{
    None        = 0,

    // Scalar bits — mirror Quiver.Stores.PropertyValueType values 1..6.
    Bool        = 1UL << 1,
    Int32       = 1UL << 2,
    Int64       = 1UL << 3,
    Double      = 1UL << 4,
    String      = 1UL << 5,
    Bytes       = 1UL << 6,

    // Array bits — bit-for-bit parallel to the scalar lane, reserved for future
    // LPG array property storage (not embedding vectors).
    BoolArray   = 1UL << 17,
    Int32Array  = 1UL << 18,
    Int64Array  = 1UL << 19,
    DoubleArray = 1UL << 20,
    StringArray = 1UL << 21,
    BytesArray  = 1UL << 22,

    // Composite masks
    Numeric         = Int32 | Int64 | Double,
    Scalar          = Bool | Int32 | Int64 | Double | String | Bytes,
    NumericArray    = Int32Array | Int64Array | DoubleArray,
    Array           = BoolArray | Int32Array | Int64Array | DoubleArray | StringArray | BytesArray,
    Variable        = String | Bytes | Array,
    Comparable      = Numeric | String,
}
