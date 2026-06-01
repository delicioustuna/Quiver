using Quiver.Storage.Records;

namespace Quiver;

/// <summary>
/// <see cref="IGraphStorageBackend"/> が公開する任意のバルクロードエントリポイントの集合。
/// バックエンドがサポートしないケイパビリティは、対応するデリゲートを null のままにしておく。
/// </summary>
internal sealed class BulkLoadCapabilities
{
    /// <summary>
    /// バイナリバックエンド向けのバルクロード開始関数。bool 引数で
    /// <see cref="BulkLoader.Commit"/> が adj.db / adj_idx.dat を併せて構築するかを指定する。
    /// アクティブなバックエンドがバイナリバックエンドでない場合は null。
    /// </summary>
    public Func<bool, BulkLoader>? BeginBinaryBulkLoad { get; init; }

    /// <summary>バイナリバックエンドのバルクロードに対応していれば true。</summary>
    public bool SupportsBinaryBulkLoad => BeginBinaryBulkLoad is not null;

    /// <summary>
    /// PW-9: 1000 万エッジ超のインポートに適したストリーミングバイナリバルクロード開始関数。
    /// バックエンドは追記中にリレーションシップレコードを一時ファイルへストリーミングし、
    /// コミット時に dense <c>long[]</c> 配列でチェーンポインタを計算する
    /// <see cref="StreamingBulkLoader"/> を提供する必要がある。bool 引数で隣接インデックスを構築するかを指定する。
    /// アクティブなバックエンドにストリーミングバルクロード経路が無い場合は null。
    /// </summary>
    public Func<bool, StreamingBulkLoader>? BeginStreamingBinaryBulkLoad { get; init; }

    /// <summary>ストリーミングバイナリバルクロードに対応していれば true。</summary>
    public bool SupportsStreamingBinaryBulkLoad => BeginStreamingBinaryBulkLoad is not null;
}
