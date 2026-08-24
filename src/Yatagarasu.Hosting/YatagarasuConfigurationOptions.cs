namespace Yatagarasu.Hosting;

/// <summary>
/// <c>Microsoft.Extensions.Configuration</c> から bind 可能な POCO 版の Yatagarasu 設定。
/// <see cref="GraphStoreOptions"/> を appsettings.json / 環境変数で表現できる
/// POCO として公開する。<see cref="ToGraphStoreOptions"/> で実体オプションに射影する。
/// </summary>
/// <remarks>
/// セクション名は <c>"Yatagarasu"</c> を推奨。環境変数では二重アンダースコア区切り
/// (例: <c>Yatagarasu__BufferPoolSize=536870912</c>) でオーバーライドできる。
/// bind 後の調整は <c>AddYatagarasu</c> の <c>postConfigure</c> で行える。
/// </remarks>
public sealed class YatagarasuConfigurationOptions
{
    /// <summary>データベースディレクトリの絶対パス。必須。</summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>バッファプールの目標サイズ (バイト単位)。既定 256 MB。</summary>
    public long BufferPoolSize { get; set; } = 256L * 1024 * 1024;

    /// <summary>チェックポイント契機の WAL 成長しきい値 (バイト単位)。既定 64 MB。</summary>
    public long CheckpointThresholdBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>チェックポイント threshold の運用ポリシー。</summary>
    public GraphCheckpointPolicy CheckpointPolicy { get; set; } = GraphCheckpointPolicy.Fixed;

    /// <summary>Adaptive 選択時の復旧時間目標。既定 5 秒。</summary>
    public TimeSpan TargetRecoveryTime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Adaptive 計算時の threshold 下限。既定 4 MB。</summary>
    public long MinimumCheckpointThresholdBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>Adaptive 計算時の threshold 上限。既定 1 GB。</summary>
    public long MaximumCheckpointThresholdBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Adaptive 移動平均のサンプル窓 (トランザクション数)。既定 1000。</summary>
    public int AdaptiveSampleWindow { get; set; } = 1000;

    /// <summary>writer lease 取得を待つ上限時間。既定 5 秒。</summary>
    public TimeSpan WriterWaitTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>writer lease が使用中だった場合の動作。既定は待機。</summary>
    public GraphWriterContentionMode WriterContentionMode { get; set; } = GraphWriterContentionMode.Wait;

    /// <summary>ページのチェックサム計算 / 検証を有効にするか。既定 <c>true</c>。</summary>
    public bool EnableChecksums { get; set; } = true;

    /// <summary>open 完了後に索引 orphan を自動修復するか。既定 <c>false</c>。</summary>
    public bool AutoRepairOrphansOnRecovery { get; set; }

    /// <summary>
    /// 現在の設定値を <see cref="GraphStoreOptions"/> に写像する。
    /// </summary>
    public GraphStoreOptions ToGraphStoreOptions()
    {
        return new GraphStoreOptions
        {
            BufferPoolSize = BufferPoolSize,
            CheckpointThresholdBytes = CheckpointThresholdBytes,
            CheckpointPolicy = CheckpointPolicy,
            TargetRecoveryTime = TargetRecoveryTime,
            MinimumCheckpointThresholdBytes = MinimumCheckpointThresholdBytes,
            MaximumCheckpointThresholdBytes = MaximumCheckpointThresholdBytes,
            AdaptiveSampleWindow = AdaptiveSampleWindow,
            WriterWaitTimeout = WriterWaitTimeout,
            WriterContentionMode = WriterContentionMode,
            EnableChecksums = EnableChecksums,
            AutoRepairOrphansOnRecovery = AutoRepairOrphansOnRecovery,
        };
    }
}
