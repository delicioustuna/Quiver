using Quiver.Core;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// Quiver ストレージエンジンのバックエンド側コントラクト。各バックエンド
/// (バイナリ、SQLite 等) がこのインタフェースを実装することで、
/// <see cref="GraphDatabase"/> は薄いファサードに留まり、ストレージレイアウトを
/// ファクトリレベルで差し替え可能にする。
/// </summary>
public interface IGraphStorageBackend : IDisposable
{
    /// <summary>このバックエンドのトランザクションマネージャ。</summary>
    ITransactionManager Transactions { get; }

    /// <summary>このバックエンドのスキーマ API。</summary>
    ISchemaApi Schema { get; }

    /// <summary>このバックエンドの診断 API。</summary>
    IDiagnosticsApi Diagnostics { get; }

    /// <summary>access methods 抽象 (BA-3)。スキャン / シーク / KNN などの物理アクセス経路を提供する。</summary>
    IGraphAccessMethods Access { get; }

    /// <summary>バルクロード関連の機能ケイパビリティ。</summary>
    BulkLoadCapabilities BulkLoad { get; }

    /// <summary>
    /// VEC-5: <see cref="IGraphAccessMethods.KnnSearch"/> が利用するベクトルストア。
    /// ユーザにも <c>CreateVectorIndex</c> / <c>SetVector</c> 用に公開される。
    /// ベクトルの永続化に未対応のバックエンド (バイナリ、SQLite MVP) ではインメモリストアが既定。
    /// </summary>
    IVectorStore Vectors { get; }

    /// <summary>
    /// アクティブなバックエンドに整合したトークン解決を伴う形で
    /// 新しいトランザクションを <see cref="IGraphTransaction"/> でラップして開始する。
    /// </summary>
    IGraphTransaction BeginGraphTransaction(IsolationLevel level, bool readOnly);
}
