namespace Quiver.Backend.Tests.Chaos;

/// <summary>
/// TS-4: 1 つの chaos シナリオ定義。<see cref="Seed"/> から workload を生成し、
/// <see cref="Fault"/> を注入し、kill → 再 open → consistency check を実行する。
///
/// 同じ scenario は (seed, fault, txCount) が同じなら毎回完全に同じ workload / 同じ
/// fault timing を再現する (verification 失敗時の minimal repro 用)。
/// </summary>
internal sealed record ChaosScenario(int Seed, int TxCount, FaultKind Fault)
{
    public override string ToString() => $"seed={Seed} txCount={TxCount} fault={Fault}";
}

/// <summary>
/// シナリオ実行結果。<see cref="Success"/> false の場合は <see cref="Trace"/> を
/// <c>chaos-trace.log</c> に追記して再現可能にする。
/// </summary>
internal sealed record ChaosResult(bool Success, string Trace, Exception? Failure = null);
