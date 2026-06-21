using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// <see cref="RelationshipId"/> からスカラプロパティ値への SID 風ジョインインデックス。
/// </summary>
/// <remarks>
/// 一度構築してスナップショットと共に保持することで、重み付きトラバーサル・エッジフィルタ・
/// アルゴリズムカーネルが lookup 毎にプロパティチェーンを辿らずに O(1) で値を取得できる。
/// 対象は <see cref="RelationshipId"/> を既に保持しているホットパスに限定し、
/// string / bytes / array は範囲外。
/// </remarks>
public interface IRelationshipPropertyJoinIndex
{
    /// <summary>
    /// <paramref name="relationshipId"/> / <paramref name="keyId"/> のスカラ値を引く。
    /// </summary>
    /// <returns>
    /// エントリが無い場合 (範囲外、プロパティ未設定、キー非対応) は <c>false</c>。
    /// <c>true</c> のとき <paramref name="type"/> は観測されたスカラ型、
    /// <paramref name="scalarBits"/> は生ビットパターン
    /// (<see cref="PropertyValueType.Double"/> なら <see cref="BitConverter.Int64BitsToDouble"/> で再解釈)。
    /// </returns>
    bool TryGetScalar(
        RelationshipId relationshipId,
        PropertyKeyId keyId,
        out PropertyValueType type,
        out long scalarBits);

    /// <summary>このインデックスが対象とするプロパティキー。</summary>
    PropertyKeyId KeyId { get; }

    /// <summary>このインデックスが対象とするスカラ型。</summary>
    PropertyValueType ValueType { get; }

    /// <summary>値が記録されたリレーションシップの件数。</summary>
    long EntryCount { get; }
}
