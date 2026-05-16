namespace Quiver.Stores;

/// <summary>
/// GC-5: MERGE-time value comparison. Two <see cref="PropertyValue"/>s match
/// when they represent the "same scalar" in the Cypher sense:
/// Bool/Int32/Int64 are interchangeable (all carry an integer scalar), Double
/// matches Double bit-exact (NaN never matches NaN, consistent with Cypher),
/// and String / Bytes match by byte-sequence equality. Type mismatch across
/// the numeric and non-numeric families is always a miss.
/// </summary>
public static class PropertyValueEqualityHelper
{
    public static bool AreEqual(in PropertyValue a, in PropertyValue b)
    {
        var ta = a.Type;
        var tb = b.Type;

        bool aIsInt = ta is PropertyValueType.Bool or PropertyValueType.Int32 or PropertyValueType.Int64;
        bool bIsInt = tb is PropertyValueType.Bool or PropertyValueType.Int32 or PropertyValueType.Int64;
        if (aIsInt && bIsInt) return a.Int64Value == b.Int64Value;

        if (ta != tb) return false;

        return ta switch
        {
            PropertyValueType.Double => a.Int64Value == b.Int64Value, // raw bits
            PropertyValueType.String => a.Utf8StringValue.SequenceEqual(b.Utf8StringValue),
            PropertyValueType.Bytes  => a.BytesValue.SequenceEqual(b.BytesValue),
            _ => false,
        };
    }
}
