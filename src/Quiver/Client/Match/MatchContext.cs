using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using System.Text;

namespace Quiver.Api.Match;

/// <summary>
/// Match DSL の <see cref="ReturnClause{TResult}"/> 内で射影クロージャに渡される
/// 行コンテキスト。パターン変数名から <see cref="MatchContextRow"/> を解決する。
/// </summary>
internal sealed class MatchContext
{
    private readonly QueryRow _row;
    private readonly IReadTransaction _tx;
    private readonly Dictionary<string, int> _varToColumn;

    internal MatchContext(QueryRow row, IReadTransaction tx, Dictionary<string, int> varToColumn)
    {
        _row = row; _tx = tx; _varToColumn = varToColumn;
    }

    /// <summary>パターン変数名でその行のVertex参照を取得する。</summary>
    public MatchContextRow this[string variable]
    {
        get
        {
            var col = _varToColumn[variable];
            var vertexId = _row.GetVertexId(col);
            return new MatchContextRow(vertexId, _tx);
        }
    }

    /// <summary>パターン変数名のVertexを <typeparamref name="T"/> インスタンスに復元する。</summary>
    public T Load<T>(string variable) where T : IGraphVertex<T>
    {
        var col = _varToColumn[variable];
        return T.Load(_tx, _row.GetVertexId(col));
    }

    /// <summary>パターン変数名に束縛されたVertex ID を取り出す。</summary>
    public VertexId Vertex(string variable) => _row.GetVertexId(_varToColumn[variable]);

    /// <summary>パターン変数名に束縛されたNexus ID を取り出す。</summary>
    public NexusId Nexus(string variable) => _row.GetNexusId(_varToColumn[variable]);

    /// <summary>
    /// Nexus変数のプロパティ <paramref name="key"/> を型 <typeparamref name="T"/> で取り出す。
    /// 対応型は <see cref="MatchContextRow.Get{T}(string)"/> と同じ。
    /// </summary>
    public T NexusGet<T>(string variable, string key)
    {
        var value = _tx.GetProperty(Nexus(variable), key);
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
        throw new NotSupportedException($"型 {typeof(T)} は MatchContext.NexusGet<T> でサポートされていません。");
    }
}

/// <summary><see cref="MatchContext.this[string]"/> から取り出される個別Vertexへの参照。</summary>
internal readonly struct MatchContextRow
{
    private readonly VertexId _vertexId;
    private readonly IReadTransaction _tx;

    internal MatchContextRow(VertexId vertexId, IReadTransaction tx)
    {
        _vertexId = vertexId; _tx = tx;
    }

    /// <summary>
    /// このVertexのプロパティ <paramref name="key"/> を型 <typeparamref name="T"/> で取り出す。
    /// 対応型: <see cref="string"/> / <see cref="long"/> / <see cref="int"/> /
    /// <see cref="double"/> / <see cref="bool"/>。
    /// </summary>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> がサポート外の場合。</exception>
    public T Get<T>(string key)
    {
        var value = _tx.GetProperty(_vertexId, key);
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
