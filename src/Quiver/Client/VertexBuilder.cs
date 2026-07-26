using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api;

/// <summary>
/// <see cref="GraphMutationSource.AddVertex"/>から開始するVertex追加ビルダ。
/// <c>.P(key, value)</c> をチェーンしてプロパティを蓄積し、最後に <see cref="Next"/> で
/// 実際にVertexを作成・コミット (トランザクション内) する。
/// </summary>
public sealed class VertexBuilder
{
    private readonly IWriteTransaction _tx;
    private readonly string _label;
    private readonly List<Action<IWriteTransaction, VertexId>> _props = new();

    internal VertexBuilder(IWriteTransaction tx, string label) { _tx = tx; _label = label; }

    /// <summary>文字列プロパティを追加する。</summary>
    public VertexBuilder P(string key, string value)  { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromString(value))); return this; }

    /// <summary><see cref="int"/> プロパティを追加する。</summary>
    public VertexBuilder P(string key, int value)     { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromInt32(value)));  return this; }

    /// <summary><see cref="long"/> プロパティを追加する。</summary>
    public VertexBuilder P(string key, long value)    { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromInt64(value)));  return this; }

    /// <summary><see cref="double"/> プロパティを追加する。</summary>
    public VertexBuilder P(string key, double value)  { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromDouble(value))); return this; }

    /// <summary><see cref="bool"/> プロパティを追加する。</summary>
    public VertexBuilder P(string key, bool value)    { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromBool(value)));   return this; }

    /// <summary>蓄積されたプロパティを適用して実際にVertexを作成し、その ID を返す。</summary>
    public VertexId Next()
    {
        var id = _tx.CreateVertex(_label);
        foreach (var apply in _props) apply(_tx, id);
        return id;
    }
}
