using Quiver.Client;
using Quiver.Core;
using Quiver.Stores;
using System.Text;

namespace Quiver.Client.Match;

/// <summary>
/// Match DSL の <see cref="ReturnClause{TResult}"/> 内で射影クロージャに渡される
/// 行コンテキスト。パターン変数名から <see cref="MatchContextRow"/> を解決する。
/// </summary>
public sealed class MatchContext
{
    private readonly QueryRow _row;
    private readonly IGraphTransaction _tx;
    private readonly Dictionary<string, int> _varToColumn;

    internal MatchContext(QueryRow row, IGraphTransaction tx, Dictionary<string, int> varToColumn)
    {
        _row = row; _tx = tx; _varToColumn = varToColumn;
    }

    /// <summary>パターン変数名でその行のノード参照を取得する。</summary>
    public MatchContextRow this[string variable]
    {
        get
        {
            var col = _varToColumn[variable];
            var nodeId = _row.GetNodeId(col);
            return new MatchContextRow(nodeId, _tx);
        }
    }

    /// <summary>パターン変数名のノードを <typeparamref name="T"/> インスタンスに復元する。</summary>
    public T Load<T>(string variable) where T : IGraphNode<T>
    {
        var col = _varToColumn[variable];
        return T.Load(_tx, _row.GetNodeId(col));
    }
}

/// <summary><see cref="MatchContext.this[string]"/> から取り出される個別ノードへの参照。</summary>
public readonly struct MatchContextRow
{
    private readonly NodeId _nodeId;
    private readonly IGraphTransaction _tx;

    internal MatchContextRow(NodeId nodeId, IGraphTransaction tx)
    {
        _nodeId = nodeId; _tx = tx;
    }

    /// <summary>
    /// このノードのプロパティ <paramref name="key"/> を型 <typeparamref name="T"/> で取り出す。
    /// 対応型: <see cref="string"/> / <see cref="long"/> / <see cref="int"/> /
    /// <see cref="double"/> / <see cref="bool"/>。
    /// </summary>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> がサポート外の場合。</exception>
    public T Get<T>(string key)
    {
        var value = _tx.GetProperty(_nodeId, key);
        if (typeof(T) == typeof(string))
            return (T)(object)Encoding.UTF8.GetString(value.Utf8StringValue);
        if (typeof(T) == typeof(long))
            return (T)(object)value.Int64Value;
        if (typeof(T) == typeof(int))
            return (T)(object)value.Int32Value;
        if (typeof(T) == typeof(double))
            return (T)(object)value.DoubleValue;
        if (typeof(T) == typeof(bool))
            return (T)(object)value.BoolValue;
        throw new NotSupportedException($"型 {typeof(T)} は MatchContextRow.Get<T> でサポートされていません。");
    }
}
