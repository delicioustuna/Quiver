# SIG トラック 親計画書 (ユーザー定義ダイアディック演算)

> 起案日: 2026-06-12。全面改訂: 2026-06-19 (IDyadicOperator ベースへ移行)。
> 設計の正本は [docs/design/15_signal_scoring.md](../docs/design/15_signal_scoring.md)。
> 本ファイルはトラックの経緯・決定事項・タスク列を記録する親計画書。

## 経緯と決定事項

### 初期設計 (2026-06-12)

ユーザ相談: RAG 想定のベクトル類似度計算を信号処理に拡張できないか。
`ISignalScorer` (static abstract) + `SignalScoreContext` (ref struct) +
文字列名レジストリ + `Quiver.Signal` サテライトの設計書を起案。

### 改訂 (2026-06-18〜19、ユーザ確定)

設計書 15 を全面改訂。主要決定:

1. **`IDyadicOperator<TResult>` が主契約** — `ISignalScorer` / `SignalScoreContext` /
   文字列名レジストリは廃止。入力は常に `ReadOnlySpan<float>`、`TResult` が戻り値型。
   `ReadOnlySpan<Range> regions` で演算領域を絞れる。`IMonadicOperator<TResult>` も同時定義。
2. **`float[]` は `[Property]` 型** — `PropertyValueType.FloatArray = 7` を追加。
   SourceGenerator が `float[]` プロパティを認識し `IVectorStore` 経路にルーティング。
   専用属性 (`[Vector]`) は不要。
3. **トラバーサル統合はラムダセレクタ** — `ApplyDyadic<TOp>(s => s.Waveform, b, regions, k)`。
   `a` は暗黙的に現在の候補から取得 (型付きトラバーサルの `T` から自明)。
   `b` は `float[]` 直渡し (静的) or `GraphTraversal<float[]>` (uncorrelated、Open 時 1 回評価)。
   C# オーバーロードで区別。`SignalOperand` 型階層は不要。
4. **`Quiver.Signal` サテライトは廃止** — 組み込み演算子 (Dot/Cosine/Euclidean) はコア同梱。
   DSP (FFT/DTW/窓関数等) はユーザーコード側の責務。
5. **索引加速は原理的に不可能** — 任意関数は距離公理を満たさず HNSW の navigability が崩壊。
   `ApplyDyadic` は常に graph-first brute へ物理化。`VectorIndexKind.FlatOnly` で HNSW スキップ可。
6. **gather/score 2 相分離は維持** — brute 経路は `_gate` ロック内でスコアリングしている。
   ユーザーコードをロック下で呼べない (ブロッキング無制限 + 再入デッドロック)。

### 整合性検証で確認した実装ギャップ (2026-06-19)

- `PropertyValueType` に空きスロット (7) あり → `FloatArray = 7` で解決
- `Values<TProp>()` は `GraphTraversal<string>` 固定 → `float[]` 対応を追加
- `GraphTraversal<float[]>` の搬送 → Bytes スロット + `MemoryMarshal.Cast` で解決
- `ref struct` 演算子は LogicalOp record に格納不能 → 物理層で遅延生成

## タスク列

| ID | 内容 | 依存 | 状態 |
|---|---|---|---|
| SIG-1 | `TryGetVector` 読み戻し API (access methods + tx 公開) | — | **完了** (2026-06-19) |
| SIG-2 | `IDyadicOperator` / `IMonadicOperator` / 組み込み演算子 (Dot/Cosine/Euclidean) | — | **完了** (2026-06-18) |
| SIG-3 | `PropertyValueType.FloatArray` + SourceGenerator `float[]` ルーティング + `Values<float[]>` | — | **完了** (2026-06-19, commit `0a07257`) |
| SIG-4 | `ApplyDyadicOp` 論理 IR + `ApplyDyadicOperator` 物理 + gather/score 2 相 + DSL | SIG-1, SIG-3 | **完了** (2026-06-19) |
| SIG-5 | `GraphTraversal<float[]>` (Bytes スロット搬送) — トラバーサル版 b | SIG-3 | 未着手 |
| SIG-6 | `VectorIndexKind.FlatOnly` + `VectorIndexSpec.IndexKind` | — | **完了** (2026-06-20, commit `3e049ce`) |
| SIG-7 | HNSW オーバーサンプル → カスタム rerank (P2) | SIG-4 | **完了** (2026-06-20) |
| SIG-8 | サンプル + ベンチ sentinel + ドキュメント | SIG-4, SIG-5 | 未着手 |

並列性: SIG-1, SIG-3, SIG-6 は独立並行可。SIG-2 は完了済み。

## 未実施 (トラック開始時に行うこと)

- [ ] `.claude/skills/quiver-implement/` への登録
- [ ] `docs/roadmap.md` への SIG-1〜8 追記
- [ ] VEC-13 (HNSW 物理削除) / VEC-14 (Filtered HNSW) との順序調整

## 完了の定義 (トラック全体)

設計書 15 §13 と同一:

- `ApplyDyadic` サンプル (テンプレート → graph 絞り込み → CosineSimilarityOp でランク付け
  → メタデータ取得) 完走
- 性能目標 (エンジン側オーバーヘッド ≤2µs/候補、走査中 0 alloc、並行書き込み p99 ≤3×) の実測値が記録
- 制約強制マトリクス各行に対応するテストが存在
- 全スイート緑
