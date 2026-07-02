using Quiver.Transactions;

namespace Quiver.Hosting;

/// <summary>
/// <c>Microsoft.Extensions.Configuration</c> から bind 可能な POCO 版の Quiver 設定。
/// <see cref="GraphDatabaseOptions"/> のうち interface / factory / 任意デリゲートのような
/// configuration バインダで扱えないメンバーを除いた、appsettings.json / 環境変数で表現できる
/// サブセットを公開する。<see cref="ToGraphDatabaseOptions"/> で実体オプションに射影する。
/// </summary>
/// <remarks>
/// セクション名は <c>"Quiver"</c> を推奨。環境変数では二重アンダースコア区切り
/// (例: <c>Quiver__BufferPoolSize=536870912</c>) でオーバーライドできる。
/// バックエンドファクトリ・LogicalMutationSink を差し込みたい場合は
/// <see cref="QuiverServiceCollectionExtensions.AddQuiver(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.Configuration.IConfiguration, System.Action{GraphDatabaseOptions}?)"/>
/// の <c>postConfigure</c> から実体 <see cref="GraphDatabaseOptions"/> を直接編集する。
/// </remarks>
public sealed class QuiverConfigurationOptions
{
    /// <summary>データベースディレクトリの絶対パス。必須。</summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>バッファプールの目標サイズ (バイト単位)。既定 256 MB。</summary>
    public long BufferPoolSize { get; set; } = 256L * 1024 * 1024;

    /// <summary>WAL 1 セグメントのサイズ (バイト単位)。既定 64 MB。</summary>
    public int WalSegmentSize { get; set; } = 64 * 1024 * 1024;

    /// <summary>チェックポイント契機の WAL 成長しきい値 (バイト単位)。既定 64 MB。</summary>
    public long CheckpointThresholdBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>チェックポイント threshold の運用ポリシー。</summary>
    public CheckpointPolicy CheckpointPolicy { get; set; } = CheckpointPolicy.Fixed;

    /// <summary>Adaptive 選択時の復旧時間目標。既定 5 秒。</summary>
    public TimeSpan TargetRecoveryTime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Adaptive 計算時の threshold 下限。既定 4 MB。</summary>
    public long MinCheckpointThresholdBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>Adaptive 計算時の threshold 上限。既定 1 GB。</summary>
    public long MaxCheckpointThresholdBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Adaptive 移動平均のサンプル窓 (トランザクション数)。既定 1000。</summary>
    public int AdaptiveSampleWindow { get; set; } = 1000;

    /// <summary>ロック取得のタイムアウト。既定 5 秒。</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>ロック戦略。</summary>
    public LockingMode LockingMode { get; set; } = LockingMode.ExclusiveOnly;

    /// <summary>ページのチェックサム計算 / 検証を有効にするか。既定 <c>true</c>。</summary>
    public bool EnableChecksums { get; set; } = true;

    /// <summary>使用するバックエンド種別。既定 <see cref="BackendKind.Binary"/>。</summary>
    public BackendKind Backend { get; set; } = BackendKind.Binary;

    /// <summary>open 完了後に索引 orphan を自動修復するか。既定 <c>false</c>。</summary>
    public bool AutoRepairOrphansOnRecovery { get; set; }

    /// <summary>デッドロック検出器の周期。null または 0 以下で無効 (既定)。</summary>
    public TimeSpan? DeadlockDetectionInterval { get; set; }

    /// <summary>WAL グループコミットの coalesce window。既定 0 (無効)。</summary>
    public TimeSpan GroupCommitWindow { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// 現在の設定値を <see cref="GraphDatabaseOptions"/> に写像する。
    /// </summary>
    public GraphDatabaseOptions ToGraphDatabaseOptions()
    {
        return new GraphDatabaseOptions
        {
            BufferPoolSize = BufferPoolSize,
            WalSegmentSize = WalSegmentSize,
            CheckpointThresholdBytes = CheckpointThresholdBytes,
            CheckpointPolicy = CheckpointPolicy,
            TargetRecoveryTime = TargetRecoveryTime,
            MinCheckpointThresholdBytes = MinCheckpointThresholdBytes,
            MaxCheckpointThresholdBytes = MaxCheckpointThresholdBytes,
            AdaptiveSampleWindow = AdaptiveSampleWindow,
            LockTimeout = LockTimeout,
            LockingMode = LockingMode,
            EnableChecksums = EnableChecksums,
            Backend = Backend,
            AutoRepairOrphansOnRecovery = AutoRepairOrphansOnRecovery,
            DeadlockDetectionInterval = DeadlockDetectionInterval,
            GroupCommitWindow = GroupCommitWindow,
        };
    }
}
