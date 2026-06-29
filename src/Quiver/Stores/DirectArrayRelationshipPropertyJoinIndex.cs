using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// 実装 (a): <see cref="RelationshipId"/> をインデックスとする dense 直接配列。
/// リレーションシップ slot あたり <see cref="long"/> 1 個 + presence ビット 1 個を確保するため、
/// 占有率に関わらずメモリは slot あたり約 9 バイト。リレーションシップ ID が密な場合
/// (bulk-load / append-only ケース) でインデックス対象キーが大半のエッジをカバーする場合に適する。
/// </summary>
/// <remarks>
/// 疎な場合 (リレーションシップの一部のみにキーが設定) でも slot コストは発生する。エントリ数が
/// 閾値を下回った場合に <see cref="Dictionary{TKey, TValue}"/> へフォールバックする変種は将来課題。
/// 現状は dense 形式のみ — 重み付きトラバーサルでは「全エッジが重みを持つ」のが典型パターンのため。
///
/// 型不一致は欠落扱い — <see cref="IRelationshipPropertyJoinIndex.KeyId"/> のプロパティ値が
/// インデックス構築時と異なる <see cref="PropertyValueType"/> を持つリレーションシップはスキップされ、
/// 型不一致 = 述語 false の慣習に従う。
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
        long id = relationshipId.Sequence; // dense 配列 index は Sequence
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

        // slot 数: 最大の live relId + 1。HWM を発見するために一度走査し、
        // 値収集パスの前に配列を適切なサイズに確保する。
        long hwm = 0;
        foreach (var relId in relStore.Scan())
            if (relId.Sequence + 1 > hwm) hwm = relId.Sequence + 1; // 配列サイズは Sequence

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
            // scalar 値は rel record へ inline されるため、join index も
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
                int idx = (int)relId.Sequence; // dense 配列 index は Sequence
                bits[idx] = raw;
                presence[idx >> 6] |= 1UL << (idx & 63);
                entryCount++;
                break;
            }
        }

        return new DirectArrayRelationshipPropertyJoinIndex(keyId, expectedType, bits, presence, entryCount);
    }
}
