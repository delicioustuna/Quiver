namespace Quiver.Transactions;

/// <summary>
/// チェックポイント threshold の運用ポリシー。
/// </summary>
public enum CheckpointPolicy
{
    /// <summary>
    /// <c>GraphDatabaseOptions.CheckpointThresholdBytes</c> の固定値を使い続ける既定挙動。
    /// </summary>
    Fixed,

    /// <summary>
    /// 直近 N 件のトランザクション WAL bytes 移動平均から threshold を自動再計算する。
    /// </summary>
    Adaptive,
}

/// <summary>
/// Adaptive ポリシー時のチェックポイント threshold を計算するコントローラ。
///
/// モデル:
///   <c>recommended = clamp(Min, Max, TargetBytes_RecoveryBound × BaselineBytesPerTx / observed_avg_bytes_per_tx)</c>
///
/// ここで <c>TargetBytes_RecoveryBound = TargetRecoveryTime.TotalSeconds × RecoveryBytesPerSecond</c>。
/// 1 tx の amplification (WAL bytes / tx) が大きいほど threshold を小さくし checkpoint を
/// 頻繁化する (per-tx パスの WAL 暴走を抑える)。逆に bulk path のように small bytes/tx の
/// ワークロードでは threshold を緩めて throughput を優先する。
///
/// EWMA ではなく単純移動平均 (リングバッファ) を採用しているのは、起動直後の少数サンプル
/// で threshold が極端な値を取らないようにするため (warmup 期は <see cref="HasEnoughSamples"/>
/// が <c>false</c> を返し、コール側は initial threshold を維持する)。
/// </summary>
internal sealed class AdaptiveCheckpointController
{
    // 内部キャリブレーション定数。実環境ベンチで再評価する余地はあるが、
    // 当面は経験則ベース (SSD 上での WAL 再生速度の概算)。
    private const long RecoveryBytesPerSecond = 50L * 1024 * 1024; // 50 MB/s
    private const long BaselineBytesPerTx = 1024;                  // 1 KB
    // delta がこれ未満のサンプル (= readonly tx の Begin + Commit overhead) は除外。
    private const long MinSampleBytes = 128;

    private readonly long _minThresholdBytes;
    private readonly long _maxThresholdBytes;
    private readonly long _targetBytesRecoveryBound;
    private readonly long[] _samples;
    private readonly object _gate = new();
    private int _writeIndex;
    private int _count;
    private long _sumBytes;
    private long _currentThreshold;

    public AdaptiveCheckpointController(
        long initialThresholdBytes,
        TimeSpan targetRecoveryTime,
        long minThresholdBytes,
        long maxThresholdBytes,
        int sampleWindow)
    {
        if (minThresholdBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(minThresholdBytes));
        if (maxThresholdBytes < minThresholdBytes)
            throw new ArgumentOutOfRangeException(nameof(maxThresholdBytes));
        if (sampleWindow <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleWindow));
        if (targetRecoveryTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(targetRecoveryTime));

        _minThresholdBytes = minThresholdBytes;
        _maxThresholdBytes = maxThresholdBytes;
        _targetBytesRecoveryBound = (long)(targetRecoveryTime.TotalSeconds * RecoveryBytesPerSecond);
        if (_targetBytesRecoveryBound <= 0) _targetBytesRecoveryBound = minThresholdBytes;
        _samples = new long[sampleWindow];
        _currentThreshold = Clamp(initialThresholdBytes);
    }

    /// <summary>現在採用中の threshold (バイト単位)。</summary>
    public long CurrentThresholdBytes
    {
        get
        {
            lock (_gate) return _currentThreshold;
        }
    }

    /// <summary>
    /// サンプルが極端に少ない (warmup) 間は <c>false</c>。コール側は initial threshold を
    /// 使い続ける。
    /// </summary>
    public bool HasEnoughSamples
    {
        get
        {
            lock (_gate) return _count >= Math.Max(16, _samples.Length / 64);
        }
    }

    /// <summary>
    /// 観測 hook。各コミットで「前回コミット以降に WAL に追記されたバイト数」を渡す。
    /// readonly tx 等の小さなサンプル (= <see cref="MinSampleBytes"/> 未満) は除外する。
    /// </summary>
    public void RecordTxBytes(long bytes)
    {
        if (bytes < MinSampleBytes) return;
        lock (_gate)
        {
            // リングバッファ更新 (古い値を sum から引き、新しい値を加算)。
            long old = _samples[_writeIndex];
            _samples[_writeIndex] = bytes;
            _writeIndex = (_writeIndex + 1) % _samples.Length;
            if (_count < _samples.Length) _count++;
            _sumBytes = _sumBytes - old + bytes;
            _currentThreshold = ComputeFromAverageLocked();
        }
    }

    /// <summary>
    /// ホットスワップ: window と threshold limits を変更する。現在のサンプルは保持しない
    /// (新しい window でサンプルを取り直す)。
    /// </summary>
    public AdaptiveCheckpointController Reconfigure(
        long initialThresholdBytes,
        TimeSpan targetRecoveryTime,
        long minThresholdBytes,
        long maxThresholdBytes,
        int sampleWindow)
        => new(initialThresholdBytes, targetRecoveryTime, minThresholdBytes, maxThresholdBytes, sampleWindow);

    private long ComputeFromAverageLocked()
    {
        if (_count == 0) return _currentThreshold;
        long avg = _sumBytes / _count;
        if (avg <= 0) return _currentThreshold;
        // recommended = target_bytes × baseline / avg。
        // 大きな数値の overflow を避けるため、target_bytes × baseline を long で計算してから割る。
        // baseline=1024、target_bytes 上限 ~5MB×N → 50MB×5s=250MB×1024=2.5e11、long で安全。
        long numerator;
        try { numerator = checked(_targetBytesRecoveryBound * BaselineBytesPerTx); }
        catch (OverflowException) { numerator = long.MaxValue; }
        long recommended = numerator / avg;
        return Clamp(recommended);
    }

    private long Clamp(long value)
    {
        if (value < _minThresholdBytes) return _minThresholdBytes;
        if (value > _maxThresholdBytes) return _maxThresholdBytes;
        return value;
    }

    /// <summary>テスト用: 内部サンプル数。</summary>
    internal int SampleCount
    {
        get { lock (_gate) return _count; }
    }
}
