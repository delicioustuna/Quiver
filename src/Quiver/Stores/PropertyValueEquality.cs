namespace Quiver.Storage.Records;

/// <summary>
/// MERGE 時の値比較。2 つの <see cref="PropertyValue"/> は Cypher の意味で「同じスカラ」を
/// 表すときに一致する:
/// Bool/Int32/Int64 は相互互換 (すべて整数スカラを保持)。Double は bit-exact で比較
/// (NaN 同士は一致しない — Cypher 準拠)。String / Bytes はバイト列一致で比較する。
/// 数値系と非数値系の型不一致は常にミス。
/// </summary>
public static class PropertyValueEqualityHelper
{
    /// <summary>2 つのプロパティ値が MERGE の意味で等しいかを判定する。</summary>
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
            PropertyValueType.Bytes      => a.BytesValue.SequenceEqual(b.BytesValue),
            PropertyValueType.FloatArray => a.FloatArrayValue.SequenceEqual(b.FloatArrayValue),
            _ => false,
        };
    }
}
