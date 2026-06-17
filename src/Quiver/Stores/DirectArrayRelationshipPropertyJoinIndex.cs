using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// implementation (a). Dense direct array
/// indexed by <see cref="RelationshipId"/>. Allocates one
/// <see cref="long"/> + one presence bit per relationship slot, so memory
/// is ~9 bytes per slot regardless of population; suitable when relationship
/// ids are dense (the bulk-load / append-only case) and the indexed key
/// covers most edges.
/// </summary>
/// <remarks>
/// Sparse populations (key set on a small fraction of relationships) still
/// pay the slot cost. A future variant could fall back to a
/// <see cref="Dictionary{TKey, TValue}"/> when entry count drops below a
/// threshold; for now the dense form is the only one shipped because
/// weighted-traversal workloads tend to be "all edges have a weight."
///
/// Type mismatches are treated as missing — a relationship whose property
/// value for <see cref="IRelationshipPropertyJoinIndex.KeyId"/> has a
/// different <see cref="PropertyValueType"/> than the index was built
/// against is skipped, matching the type-mismatch predicate-false convention.
/// </remarks>
internal sealed class DirectArrayRelationshipPropertyJoinIndex : IRelationshipPropertyJoinIndex
{
    private readonly PropertyKeyId _keyId;
    private readonly PropertyValueType _type;
    private readonly long[] _bits;
    private readonly ulong[] _presence;
    private readonly long _entryCount;

    private DirectArrayRelationshipPropertyJoinIndex(
        PropertyKeyId keyId, PropertyValueType type,
        long[] bits, ulong[] presence, long entryCount)
    {
        _keyId = keyId;
        _type = type;
        _bits = bits;
        _presence = presence;
        _entryCount = entryCount;
    }

    public PropertyKeyId KeyId => _keyId;
    public PropertyValueType ValueType => _type;
    public long EntryCount => _entryCount;

    public bool TryGetScalar(
        RelationshipId relationshipId,
        PropertyKeyId keyId,
        out PropertyValueType type,
        out long scalarBits)
    {
        type = default;
        scalarBits = 0;
        if (keyId != _keyId) return false;
        long id = relationshipId.Sequence; // ARCH-5b: dense 配列 index は Sequence
        if ((ulong)id >= (ulong)_bits.LongLength) return false;
        int word = (int)(id >> 6);
        ulong mask = 1UL << (int)(id & 63);
        if ((_presence[word] & mask) == 0) return false;
        type = _type;
        scalarBits = _bits[(int)id];
        return true;
    }

    /// <summary>
    /// <paramref name="relStore"/> 内のすべての生存中リレーションシップを走査し、
    /// プロパティチェーンを辿って <paramref name="keyId"/> かつ <paramref name="expectedType"/> に
    /// 一致するスカラ値をスナップショットする。構築後のミューテーションは可視化されないため、
    /// 厳密な鮮度が必要な場合はグラフ変更後に再構築する。
    /// </summary>
    /// <param name="expectedType">
    /// インラインスカラ型のいずれか (<see cref="PropertyValueType.Bool"/>、
    /// <see cref="PropertyValueType.Int32"/>、<see cref="PropertyValueType.Int64"/>、
    /// <see cref="PropertyValueType.Double"/>)。String / Bytes は単一 <see cref="long"/> に収まらず
    /// プロパティチェーン側で扱うため、ここでは拒否する。
    /// </param>
    public static DirectArrayRelationshipPropertyJoinIndex Build(
        IRelationshipStore relStore,
        IPropertyStore propStore,
        PropertyKeyId keyId,
        PropertyValueType expectedType)
    {
        if (expectedType is not (PropertyValueType.Bool or PropertyValueType.Int32
                              or PropertyValueType.Int64 or PropertyValueType.Double))
        {
            throw new ArgumentException(
                $"Join index supports scalar inline types only; got {expectedType}.",
                nameof(expectedType));
        }

        // Slot count: highest live relId + 1. Scan once to discover hwm so
        // we can right-size the arrays before the value-collecting pass.
        long hwm = 0;
        foreach (var relId in relStore.Scan())
            if (relId.Sequence + 1 > hwm) hwm = relId.Sequence + 1; // ARCH-5b: 配列サイズは Sequence

        if (hwm > int.MaxValue)
        {
            throw new NotSupportedException(
                $"DirectArrayRelationshipPropertyJoinIndex caps at int.MaxValue relationships (saw hwm={hwm}).");
        }

        var bits = hwm == 0 ? [] : new long[hwm];
        var presence = hwm == 0 ? [] : new ulong[(hwm + 63) >> 6];
        long entryCount = 0;

        foreach (var relId in relStore.Scan())
        {
            // ARCH-5c Phase 4: scalar 値は rel record へ inline されるため、join index も
            // inline + overflow を結合列挙する (join index 対象型はすべて inline 対象)。
            var pe = relStore.EnumerateProperties(relId, propStore);
            while (pe.MoveNext())
            {
                var cur = pe.Current;
                if (cur.KeyId != keyId) continue;
                if (cur.Value.Type != expectedType) break; // type mismatch — predicate false
                long raw = cur.Value.Type switch
                {
                    PropertyValueType.Bool => cur.Value.BoolValue ? 1L : 0L,
                    PropertyValueType.Int32 => cur.Value.Int32Value,
                    PropertyValueType.Int64 => cur.Value.Int64Value,
                    PropertyValueType.Double => BitConverter.DoubleToInt64Bits(cur.Value.DoubleValue),
                    _ => 0L,
                };
                int idx = (int)relId.Sequence; // ARCH-5b: dense 配列 index は Sequence
                bits[idx] = raw;
                presence[idx >> 6] |= 1UL << (idx & 63);
                entryCount++;
                break;
            }
        }

        return new DirectArrayRelationshipPropertyJoinIndex(keyId, expectedType, bits, presence, entryCount);
    }
}
