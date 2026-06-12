# SIG トラック 親計画書 (ユーザー定義信号スコアリング)

> 起案日: 2026-06-12。設計の正本は [docs/design/15_signal_scoring.md](../docs/design/15_signal_scoring.md)。
> 本ファイルはトラックの経緯・決定事項・タスク列を記録する親計画書。
> FTS/RAG トラック ([rag-fts-track.md](rag-fts-track.md)) とは独立した新規トラック。

## 経緯と決定事項 (ユーザ確定)

ユーザ相談 (2026-06-12): RAG 想定のベクトル類似度計算を信号処理に拡張できないか。
「ユーザー定義の閉じた複数 Span<float>→float/double 静的関数をベースに、グラフ検索で
実行対象を絞りながら信号処理によるランク付けを行い、最終的にベクトル付随情報をグラフから
取得する」用途。検討の結果、以下を確定:

1. **組み込む。ただし RAG と逆の構図** — RAG はエンジン外の仕事 (チャンキング・取込) が
   大きいためサテライト層 (Quiver.Rag) が主役だった。信号処理で主役になるのは**コアの
   拡張ポイント** (カスタムスコアラを候補スキャン経路に差し込む口)。DSP はコアに持ち込まない。
   **サテライト `Quiver.Signal` もトラックに含める** (ユーザ指示 2026-06-12): 標準スコアラ
   (NCC/DTW/spectral) + DSP ユーティリティ (窓関数/正規化/FFT/フレーミング)。依存はコアのみ、
   「graph-first 信号ランク付けの最小キット」が線引きで完全 DSP フレームワーク化は非目標
   (設計書 §8)。
2. **索引加速は原理的に不可能と明記** — 任意関数は距離公理を満たさず HNSW の navigability
   前提が崩壊する。カスタムスコアラは graph-first brute 限定 (+P2 で HNSW オーバーサンプル
   → rerank)。オプティマイザは SignalRankOp を常に filtered-brute へ物理化する。
3. **制約はコンパイル時強制を最優先** — static abstract interface member で「閉じた静的関数」を
   型システムで強制。ref struct コンテキストでスパン非エスケープ + エンジン非再入を強制。
   純粋性のみ文書契約 (機械強制不能)。設計書 §4.5 の強制マトリクスが正本。
4. **サブトラバーサル入力は採用** — (a) クエリ側参照集合 (uncorrelated、Open 時 1 回評価) と
   (b) per-candidate 1-hop gather (correlated、maxFanOut 必須) の 2 形態に分けて段階導入。
   multi-hop は非目標。
5. **既存判断との整合** — 13 番文書の「score 非公開・RRF は rank のみ」は変更しない。
   スコア露出は RankBySignal + WithScore の opt-in に限定。

## 設計上の重要発見 (調査時、2026-06-12)

- 実行骨格は 9 割既存: `FilterByKnn` → `FilteredKnnNodeSourceOperator` →
  `PersistentVectorStore.KnnSearchFiltered` の小候補 brute 経路 (gather + 逐次スコア) が
  そのまま流用できる。
- `TupleSlotType.Double` が既存のため、スコア列露出に新スロット型は不要。
- **brute 経路は `_gate` ロック内でスコアリングしている** — ユーザーコードをロック下で
  呼べない (ブロッキング無制限 + 再入デッドロック)。gather/score の 2 相分離
  (チャンク 256 件、ロック内 gather → ロック外 score) が必須要件 (設計書 §5.1)。
- ベクトル読み戻し公開 API (`TryGetVector`) が存在しない — 信号処理に限らず欠けている穴。
  SIG-1 で最初に埋める。
- クエリ側信号は次元検証しない (短いテンプレート vs 固定長フレームを許す)。格納側は
  固定次元 float のまま (可変長格納は非目標)。
- 信号用途では HNSW 構築コストが無駄 → `VectorIndexKind.FlatOnly` を追加
  (catalog 1 byte、未リリースなのでクリーンブレイク)。

## タスク列

| ID | 内容 | 依存 | 優先 |
|---|---|---|---|
| SIG-1 | `TryGetVector` 読み戻し API | — | P0 |
| SIG-2 | スコアラ基盤 (ISignalScorer / SignalScoreContext / SignalQuery / レジストリ / FlatOnly) | — | P0 |
| SIG-3 | 実行経路 (gather/score 2 相 + SignalRankOperator + RankBySignal/WithScore + スコア列) | SIG-1, SIG-2 | P0 |
| SIG-4 | サブトラバーサル入力 (refs + neighbors 1-hop gather) | SIG-3 | P1 |
| SIG-5 | HNSW オーバーサンプル → カスタム rerank (vector-first 補完) | SIG-3 | P2 |
| SIG-6 | `Quiver.Signal` アセンブリ (標準スコアラ NCC/DTW/spectral + DSP ユーティリティ + 一括登録) | SIG-2 (契約のみ。テストは SIG-3 後) | P1 |
| SIG-7 | サンプル (Quiver.Signal 使用) + ベンチ sentinel + ドキュメント | SIG-3, SIG-6 (+4) | P1 |

並列性: SIG-1 と SIG-2 は独立並行可。SIG-6 は SIG-2 の契約確定後コアと並行実装可。
FTS/RAG トラックとはコード接点が薄く (FilteredKnn 周辺 + VEC-13/14 のみ)、
FTS-6 / RAG-5 の完了を待たずに着手可能。着手順はユーザ判断 (FTS/RAG 残タスク優先か SIG 先行か)。

## 未実施 (トラック開始時に行うこと)

- [ ] `.claude/skills/quiver-implement/` への登録 (SKILL.md タスク表 + roadmap +
      `tasks/signal.md` 作成)。スキルは empirical tuning 済みのため、編集は
      トラック開始を決めてから慎重に行う (この親計画書と設計書 15 を参照させる)
- [ ] `docs/roadmap.md` への SIG-1〜7 追記
- [ ] VEC-13 (HNSW 物理削除) / VEC-14 (Filtered HNSW) との順序調整 —
      同じ `PersistentVectorStore` を触るため、並行着手は避ける

## 完了の定義 (トラック全体)

設計書 15 §13 と同一:

- 信号マッチングサンプル (テンプレート → graph 絞り込み → Quiver.Signal の NCC で
  ランク付け → メタデータ取得) 完走
- 性能目標 (設計書 §10: エンジン側オーバーヘッド ≤2µs/候補、走査中 0 alloc、
  並行書き込み p99 ≤3×) の実測値が設計書に記録
- 制約強制マトリクス各行に対応するテストが存在
- 全スイート緑
