using Quiver.Transactions;

namespace Quiver.Backend.Tests.Chaos;

/// <summary>
/// TS-4: chaos runner が利用する障害注入の薄い抽象。各 backend (binary, SQLite) は
/// 自身のファイルレイアウトに合った torn-write / checksum / sidecar 削除を提供する。
///
/// 個々の注入 API は実体ファイルが存在しない場合は no-op (workload が WAL を 1 度も
/// 書いていないケース等)。kill 自身は scenario runner 側が
/// <see cref="Faults.KillProcessSimulator"/> を直接呼ぶので、ここには含めない。
/// </summary>
internal interface IFaultInjector
{
    /// <summary>最終 WAL セグメント末尾 <paramref name="tailBytes"/> を zero-fill する。</summary>
    void InjectTornWriteAtTail(int tailBytes);

    /// <summary>checksum / 検出ストラクチャの 1 bit を flip する。</summary>
    void InjectChecksumCorruption();

    /// <summary>sidecar (rebuild 可能な補助ファイル) を 1 つ削除する。</summary>
    void DeleteSidecarFile();

    /// <summary>
    /// <see cref="Checkpointer.PhaseInjector"/> を仕込み、指定 phase 完了直後で
    /// <see cref="System.InvalidOperationException"/> を投げる。返り値の <see cref="System.IDisposable"/>
    /// で元に戻す (テストが他に影響しないように)。
    /// </summary>
    System.IDisposable ArmCheckpointPhaseKill(CheckpointPhase phase);
}
