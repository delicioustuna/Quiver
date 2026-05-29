using Xunit;

// FT-32: この assembly の crash-contract / chaos テストは、テスト専用のグローバル静的
// (Checkpointer.PhaseInjector など) と checkpoint/WAL マシナリを共有しながら独自 backend を
// open する。複数クラスを並列実行すると、あるクラスが PhaseInjector を仕込んでいる窓で別クラスの
// commit が checkpoint を誘発し、注入例外が別 backend で発火する競合が起きる。FT-32 で sidecar 分の
// per-tx WAL 量が増え checkpoint 発火頻度が上がったことでこの潜在競合が表面化したため、
// 本 assembly はクラス間並列を無効化して逐次実行する (production には PhaseInjector 相当は無く、
// これは純粋にテスト分離のための設定)。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
