using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 重み付き最短経路オペレータ (<see cref="WeightedShortestPathOperator"/>) が
/// エッジ 1 本の重みを取得するための抽象。バックエンドごとに最適な経路
/// (property-chain 走査 / payload lane / join index) を差し替えられるようにする。
/// </summary>
internal interface IEdgeWeightProvider
{
    /// <summary>
    /// Edge <paramref name="edgeId"/> の重みを返す。
    /// </summary>
    /// <param name="tx">探索中のトランザクション。</param>
    /// <param name="edgeId">重みを引きたいエッジ。</param>
    /// <param name="weightRaw">
    /// <see cref="ExpandCursor.WeightRaw"/> が転送した 64 ビット生 payload。
    /// payload lane を持たないカーソルでは 0。
    /// </param>
    double GetWeight(ITransaction tx, EdgeId edgeId, long weightRaw);
}

/// <summary>
/// Edgeのプロパティチェーンを走査して重みを取得する既定の
/// <see cref="IEdgeWeightProvider"/>。セットアップ不要でどのバックエンドでも動くが、
/// エッジあたり O(P) (P = そのエッジのプロパティ数)。大規模ホットパスでは
/// <see cref="PayloadLaneWeightProvider"/> や join index 版に差し替えるとよい。
/// </summary>
internal sealed class PropertyChainWeightProvider : IEdgeWeightProvider
{
    private readonly PropertyKeyId _weightKey;
    private readonly double _defaultWeight;

    /// <param name="weightKey">重みを保持するEdgeプロパティのキー ID。</param>
    /// <param name="defaultWeight">
    /// 対象キーのプロパティを持たないエッジに適用する重み (既定 1.0 = 重み無しエッジ)。
    /// </param>
    public PropertyChainWeightProvider(PropertyKeyId weightKey, double defaultWeight = 1.0)
    {
        _weightKey = weightKey;
        _defaultWeight = defaultWeight;
    }

    /// <inheritdoc/>
    public double GetWeight(ITransaction tx, EdgeId edgeId, long weightRaw)
    {
        // edge weight も inline + overflow を結合列挙する。
        var e = tx.Edges.EnumerateProperties(edgeId, tx.Properties);
        while (e.MoveNext())
        {
            if (e.Current.KeyId == _weightKey)
                return ToDouble(e.Current.Value);
        }
        return _defaultWeight;
    }

    /// <summary>スカラ系 <see cref="PropertyValue"/> を <see cref="double"/> 重みに変換する。</summary>
    internal static double ToDouble(in PropertyValue v) => v.Type switch
    {
        PropertyValueType.Double => v.DoubleValue,
        PropertyValueType.Int64 => v.Int64Value,
        PropertyValueType.Int32 => v.Int32Value,
        PropertyValueType.Bool => v.BoolValue ? 1.0 : 0.0,
        _ => throw new InvalidOperationException(
            $"プロパティ値型 {v.Type} はエッジ重みとして解釈できません (数値型が必要)。"),
    };
}

/// <summary>
/// payload lane (<see cref="ExpandCursor.WeightRaw"/>) をそのままエッジ重みとして
/// 使う <see cref="IEdgeWeightProvider"/>。bulk load 時に
/// <c>PayloadLaneSpec.ForDouble</c> で構築したグラフ向けの、プロパティ参照不要な経路。
/// payload lane を持たないグラフでは全エッジ重み 0.0 になる点に注意。
/// </summary>
internal sealed class PayloadLaneWeightProvider : IEdgeWeightProvider
{
    /// <summary>状態を持たないため共有可能な単一インスタンス。</summary>
    public static readonly PayloadLaneWeightProvider Instance = new();

    /// <inheritdoc/>
    public double GetWeight(ITransaction tx, EdgeId edgeId, long weightRaw)
        => BitConverter.Int64BitsToDouble(weightRaw);
}
