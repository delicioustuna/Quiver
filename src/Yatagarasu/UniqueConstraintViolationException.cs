using Yatagarasu.Core;

namespace Yatagarasu;

/// <summary>label内で一意であるべき文字列プロパティが別Vertexと競合したことを表す。</summary>
public sealed class UniqueConstraintViolationException : YatagarasuException
{
    /// <summary>競合した一意制約を指定して例外を生成する。</summary>
    /// <param name="indexName">制約を保持するscalar index名。</param>
    /// <param name="scope">制約の対象label。</param>
    /// <param name="propertyKey">制約の対象プロパティキー。</param>
    public UniqueConstraintViolationException(
        string indexName,
        string scope,
        string propertyKey)
        : base(
            $"Unique index '{indexName}' rejected a duplicate value for " +
            $"Vertex label '{scope}' property '{propertyKey}'.")
    {
        IndexName = indexName;
        Scope = scope;
        PropertyKey = propertyKey;
    }

    /// <summary>制約を保持するscalar index名。</summary>
    public string IndexName { get; }

    /// <summary>制約の対象label。</summary>
    public string Scope { get; }

    /// <summary>制約の対象プロパティキー。</summary>
    public string PropertyKey { get; }
}
