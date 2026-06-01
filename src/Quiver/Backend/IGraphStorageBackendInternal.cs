using Quiver.Core;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// ARCH-2: バックエンドの内部 SPI。公開 <see cref="IGraphStorageBackend"/> から外した
/// 「トランザクションマネージャ / access methods / バルクロード ケイパビリティ」を担う。
/// これらは内部実装型 (<see cref="ITransactionManager"/> / <see cref="IGraphAccessMethods"/> /
/// <see cref="BulkLoadCapabilities"/>) を露出するため API 利用者には見せない。
/// 各バックエンド (binary / SQLite) はこの内部 SPI を実装し、<see cref="GraphDatabase"/> が駆動する。
/// </summary>
internal interface IGraphStorageBackendInternal : IGraphStorageBackend
{
    /// <summary>このバックエンドのトランザクションマネージャ。</summary>
    ITransactionManager Transactions { get; }

    /// <summary>access methods 抽象 (BA-3)。スキャン / シーク / KNN などの物理アクセス経路を提供する。</summary>
    IGraphAccessMethods Access { get; }

    /// <summary>バルクロード関連の機能ケイパビリティ。</summary>
    BulkLoadCapabilities BulkLoad { get; }
}
