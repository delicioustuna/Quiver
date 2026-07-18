using System.Linq.Expressions;

namespace Quiver;

/// <summary>
/// SourceGenerator が <c>[Vertex]</c> 付与クラスに自動実装するスキーマ宣言 API。
/// 属性で宣言されたインデックス情報 (label / propertyName / 推論された <see cref="IndexKind"/>)
/// を実体 B+Tree インデックスとして登録する手段を提供する。
/// </summary>
/// <typeparam name="TSelf">自分自身の型 (CRTP)。</typeparam>
/// <remarks>
/// 通常は <see cref="QuiverDatabaseSchemaExtensions.EnsureIndexes{T}"/> /
/// <see cref="QuiverDatabaseSchemaExtensions.EnsureIndex{T}"/> 経由で呼ぶ。手動実装は不要。
/// </remarks>
public interface IGraphVertexSchema<TSelf> where TSelf : IGraphVertexSchema<TSelf>
{
    /// <summary>
    /// <c>[Indexed]</c> が付与された全プロパティについて、属性で指定された
    /// インデックス名 (省略時 <c>idx_{label}_{propertyName}</c>) と C# 型から推論した
    /// <see cref="IndexKind"/> で <see cref="ISchemaEditor.CreateIndex"/> を冪等に呼び出す。
    /// 起動時に一度呼べばよい。
    /// </summary>
    static abstract void EnsureIndexes(ISchemaEditor schema);

    /// <summary>
    /// 単一プロパティのインデックスを冪等に作成する。<paramref name="propertyName"/> は呼び出し側
    /// (典型的には <see cref="QuiverDatabaseSchemaExtensions.EnsureIndex{T}"/>) が
    /// ラムダから抽出した CLR プロパティ名。<paramref name="kindOverride"/> を渡すと
    /// 推論された既定 kind を上書きできる (例: 文字列プロパティに対し
    /// <see cref="IndexKind.StringRange"/> を使う)。
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="propertyName"/> が <c>[Indexed]</c> を持たない場合。
    /// </exception>
    static abstract void EnsureIndex(ISchemaEditor schema, string propertyName, IndexKind? kindOverride);
}

/// <summary>
/// <see cref="IGraphVertexSchema{TSelf}"/> を実装した型に対するスキーマ操作の糖衣 API。
/// </summary>
public static class SchemaEditorExtensions
{
    /// <summary>
    /// <c>[Indexed]</c> 付き全プロパティのインデックスを一括作成する。
    /// </summary>
    /// <example>
    /// <code>
    /// db.EnsureIndexes&lt;Person&gt;;   //起動時に一度
    /// </code>
    /// </example>
    public static void EnsureIndexes<T>(this ISchemaEditor schema) where T : IGraphVertexSchema<T>
        => T.EnsureIndexes(schema);

    /// <summary>
    /// 単一プロパティ用のインデックスを冪等に作成する (既存なら何もしない)。
    /// <paramref name="propertySelector"/> は <c>p =&gt; p.Name</c> 形式のメンバアクセス式に限る。
    /// <paramref name="kindOverride"/> を渡すと推論された既定 kind を上書きできる。
    /// </summary>
    /// <example>
    /// <code>
    /// db.EnsureIndex&lt;Person&gt;(p =&gt; p.Name);                              //推論 (StringEquality)
    /// db.EnsureIndex&lt;Person&gt;(p =&gt; p.Name, IndexKind.StringRange);        //上書き
    /// </code>
    /// </example>
    public static void EnsureIndex<T>(
        this ISchemaEditor schema,
        Expression<Func<T, object?>> propertySelector,
        IndexKind? kindOverride = null) where T : IGraphVertexSchema<T>
    {
        var name = ExtractMemberName(propertySelector.Body);
        T.EnsureIndex(schema, name, kindOverride);
    }

    private static string ExtractMemberName(Expression body)
    {
        // value type プロパティをラムダ戻り値 object? にすると Convert Vertexが挟まる。
        if (body is UnaryExpression u && (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
            body = u.Operand;
        if (body is MemberExpression m)
            return m.Member.Name;
        throw new ArgumentException(
            $"プロパティセレクタは 'p => p.Member' 形式のメンバアクセス式である必要があります。実際: {body}",
            nameof(body));
    }
}
