using Quiver.Core;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// バックエンドの内部 SPI。公開 <see cref="IGraphStorageBackend"/> から外した
/// 「トランザクションマネージャ / access methods / バルクロード ケイパビリティ」を担う。
/// これらは内部実装型 (<see cref="ITransactionManager"/> / <see cref="IGraphAccessMethods"/> /
/// <see cref="BulkLoadCapabilities"/>) を露出するため API 利用者には見せない。
/// バックエンドはこの内部 SPI を実装し、<see cref="QuiverDatabase"/> が駆動する。
/// </summary>
internal interface IGraphStorageBackendInternal : IGraphStorageBackend
{
    /// <summary>このバックエンドのトランザクションマネージャ。</summary>
    ITransactionManager Transactions { get; }

    /// <summary>内部 adapter が token を参照する read-only catalog。</summary>
    ISchemaCatalog SchemaCatalog { get; }

    /// <summary>access methods 抽象。スキャン / シーク / KNN などの物理アクセス経路を提供する。</summary>
    IGraphAccessMethods Access { get; }

    /// <summary>バルクロード関連の機能ケイパビリティ。</summary>
    BulkLoadCapabilities BulkLoad { get; }

    /// <summary>
    /// このバックエンドのデータが置かれているディレクトリ。
    /// backend-local artifact の保存先解決に使う。
    /// binary backend は <c>*.quiver</c> の親ディレクトリ。
    /// </summary>
    string DataDirectory { get; }
}
