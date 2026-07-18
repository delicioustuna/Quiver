using Quiver.Core;
using Quiver.Index;
using Quiver.Transactions;

namespace Quiver.Storage.Records;

/// <summary>
/// B+Tree 索引が返すプロパティ版参照を、現在の読み取りスナップショットで可視な頂点へ解決する。
/// プロパティキー、値、所有者種別、所有者世代、ラベルスコープを再検証し、
/// 古い索引エントリや slot 再利用後の別エンティティを結果から除外する。
/// </summary>
internal static class IndexValueResolver
{
    internal static IEnumerable<long> ResolveVisibleVertexPropertyOwners(
        IEnumerable<long> propertyVersions,
        ITransaction transaction,
        PropertyKeyId propertyKey,
        LabelId? scope,
        ScalarRangeFilter filter)
    {
        foreach (long value in propertyVersions)
        {
            PropertyVersionRecord property = transaction.Properties.Read(
                new PropertyVersionRef(value));
            PropertyValue propertyValue = property.Value;
            if (!property.InUse
                || property.Address.Key != propertyKey
                || property.Address.Owner.Kind != EntityKind.Vertex
                || !filter.Matches(in propertyValue))
                continue;

            var owner = new VertexId(property.Address.Owner.Value);
            VertexReadHandle vertex = transaction.Vertices.Read(owner);
            if (vertex.InUse && (scope is null || vertex.Label == scope.Value))
                yield return owner.Value;
        }
    }

    internal static IEnumerable<VertexId> SeekVisibleVertexPropertyOwners(
        ITransaction transaction,
        ScalarIndexDefinition definition,
        PropertyKeyId propertyKey,
        LabelId? scope,
        in PropertyValue key)
    {
        ScalarRangeFilter filter = ScalarRangeFilter.Equal(
            definition.Kind,
            in key);
        ScalarIndexMetadata metadata = transaction.Indexes.ListIndexDefinitions()
            .FirstOrDefault(candidate =>
                candidate.Definition.Name == definition.Name);

        IEnumerable<long> versions;
        if (metadata.Definition is not null
            && metadata.State != IndexLifecycleState.Ready)
        {
            versions = ScanVisibleVertexPropertyVersions(
                transaction,
                propertyKey,
                scope,
                filter);
        }
        else
        {
            versions = definition.Kind switch
            {
                IndexKind.Int32Equality when key.Type == PropertyValueType.Int32
                    => transaction.Indexes.CreateInt32Index(definition.Name)
                        .SeekValues(key.Int32Value),
                IndexKind.Int64Equality when key.Type is
                    PropertyValueType.Bool or PropertyValueType.Int32 or PropertyValueType.Int64
                    => transaction.Indexes.CreateInt64Index(definition.Name)
                        .SeekValues(key.Int64Value),
                IndexKind.DoubleEquality when key.Type == PropertyValueType.Double
                    => transaction.Indexes.CreateDoubleIndex(definition.Name)
                        .SeekValues(key.DoubleValue),
                IndexKind.StringEquality or IndexKind.StringRange
                    when key.Type == PropertyValueType.String
                    => transaction.Indexes.CreateStringIndex(definition.Name)
                        .SeekValues(System.Text.Encoding.UTF8.GetString(
                            key.Utf8StringValue)),
                _ => [],
            };
        }

        return ResolveVisibleVertexPropertyOwners(
                versions,
                transaction,
                propertyKey,
                scope,
                filter)
            .Select(static value => new VertexId(value));
    }

    private static IEnumerable<long> ScanVisibleVertexPropertyVersions(
        ITransaction transaction,
        PropertyKeyId propertyKey,
        LabelId? scope,
        ScalarRangeFilter filter)
    {
        var versions = new List<long>();
        foreach (VertexId vertexId in transaction.Vertices.Scan())
        {
            VertexReadHandle vertex = transaction.Vertices.Read(vertexId);
            if (!vertex.InUse || scope is { } label && vertex.Label != label)
                continue;

            PropertyCursor properties = transaction.Vertices.EnumerateProperties(
                vertexId,
                transaction.Properties);
            while (properties.MoveNext())
            {
                PropertyEntry property = properties.Current;
                PropertyValue value = property.Value;
                if (property.KeyId == propertyKey && filter.Matches(in value))
                    versions.Add(properties.CurrentVersion.Value);
            }
        }
        return versions;
    }

    /// <summary>
    /// パック値が「現在生きている Vertex」を指すか (Kind が Vertex かつ slot 世代一致)。
    /// の WAND は top-k を確定する前に dead/再利用エントリを弾く必要があるため、
    /// スコアリングループ内でこの述語を使う (resolve 後 Take(k) と同じ可視性規約)。
    /// </summary>
    public static bool IsLiveVertex(long packed, IVertexStore vertices)
        => EntityRef.UnpackKind(packed) == EntityKind.Vertex
           && vertices.CurrentGeneration(EntityRef.UnpackSequence(packed)) == EntityRef.UnpackGeneration(packed);

    /// <summary>
    /// パック値の列挙を世代照合しつつ local packed <c>VertexId.Value</c> へ unpack する。
    /// Kind が Vertex でないエントリ、世代不一致エントリは除外する。
    /// </summary>
    public static IEnumerable<long> ResolveLiveVertexSequences(IEnumerable<long> packedValues, IVertexStore vertices)
    {
        foreach (var packed in packedValues)
        {
            if (!IsLiveVertex(packed, vertices)) continue;
            yield return EntityRef.PackLocal(
                EntityRef.UnpackSequence(packed),
                EntityRef.UnpackGeneration(packed));
        }
    }

    /// <summary>解決済み局所 ID 列挙を <see cref="VertexId"/> でラップする。</summary>
    public static IEnumerable<VertexId> ResolveLiveVertexIds(IEnumerable<long> packedValues, IVertexStore vertices)
    {
        foreach (var value in ResolveLiveVertexSequences(packedValues, vertices))
            yield return new VertexId(value);
    }
}

internal readonly record struct ScalarRangeFilter(
    IndexKind Kind,
    long FromScalar,
    long ToScalar,
    double FromDouble,
    double ToDouble,
    string? FromString,
    string? UpperString,
    bool FromInclusive,
    bool ToInclusive)
{
    internal static ScalarRangeFilter Equal(
        IndexKind kind,
        in PropertyValue value)
        => value.Type switch
        {
            PropertyValueType.Bool => new(
                kind,
                value.BoolValue ? 1 : 0,
                value.BoolValue ? 1 : 0,
                0,
                0,
                null,
                null,
                true,
                true),
            PropertyValueType.Int32 => new(
                kind,
                value.Int32Value,
                value.Int32Value,
                0,
                0,
                null,
                null,
                true,
                true),
            PropertyValueType.Int64 => new(
                kind,
                value.Int64Value,
                value.Int64Value,
                0,
                0,
                null,
                null,
                true,
                true),
            PropertyValueType.Double => new(
                kind,
                0,
                0,
                value.DoubleValue,
                value.DoubleValue,
                null,
                null,
                true,
                true),
            PropertyValueType.String => EqualString(
                kind,
                System.Text.Encoding.UTF8.GetString(value.Utf8StringValue)),
            _ => default,
        };

    private static ScalarRangeFilter EqualString(
        IndexKind kind,
        string value)
        => new(
            kind,
            0,
            0,
            0,
            0,
            value,
            value,
            true,
            true);

    internal bool Matches(in PropertyValue value)
    {
        int lower;
        int upper;
        switch (Kind)
        {
            case IndexKind.Int32Equality when value.Type == PropertyValueType.Int32:
                lower = value.Int32Value.CompareTo((int)FromScalar);
                upper = value.Int32Value.CompareTo((int)ToScalar);
                break;
            case IndexKind.Int64Equality when value.Type is
                PropertyValueType.Bool or PropertyValueType.Int32 or PropertyValueType.Int64:
                lower = value.Int64Value.CompareTo(FromScalar);
                upper = value.Int64Value.CompareTo(ToScalar);
                break;
            case IndexKind.DoubleEquality when value.Type == PropertyValueType.Double:
                lower = value.DoubleValue.CompareTo(FromDouble);
                upper = value.DoubleValue.CompareTo(ToDouble);
                break;
            case IndexKind.StringEquality or IndexKind.StringRange
                when value.Type == PropertyValueType.String:
                string current = System.Text.Encoding.UTF8.GetString(
                    value.Utf8StringValue);
                lower = string.CompareOrdinal(current, FromString);
                upper = string.CompareOrdinal(current, UpperString);
                break;
            default:
                return false;
        }

        return (FromInclusive ? lower >= 0 : lower > 0)
            && (ToInclusive ? upper <= 0 : upper < 0);
    }
}
