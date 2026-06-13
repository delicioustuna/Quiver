# FTS / RAG トラック 親計画書 (ローカル RAG バックエンド化)

> 承認日: 2026-06-10。設計の正本は docs/design/ 側にあり、本ファイルはトラック全体の
> 経緯・決定事項・タスク列を記録する親計画書。個別タスクの実装手順は
> `.claude/skills/quiver-implement/tasks/fts.md` / `tasks/rag.md` を参照。

## 経緯と決定事項 (ユーザ確定、再検討不要)

プロジェクト評価 (2026-06-10) で「技術は A、目的が未定義」と指摘され、ユーザが
**「ローカル RAG のバックエンドを担う DB エンジンに育てる」**と決定。以下を確定:

1. **エンジン内 BM25**: 転置インデックス + BM25 + RRF ハイブリッド融合をエンジン内に実装。
   日本語 (bigram) 対応。これが「組み込み RAG DB」の核となる差別化要素。
2. **RAG 層は別アセンブリ** `Quiver.Rag`: エンジン本体は汎用のまま。Document→Chunk スキーマ +
   取込/再取込 + hybrid 検索 + graph expansion を提供。取込側 (PdfTools) との契約もここで定義。
3. **ポジショニング**: 「.NET 向け組み込みグラフ+ベクトル+全文検索 DB。ローカル RAG は
   メインユースケースであって DB の定義ではない」。Neptune/TinkerPop は物差しから外す
   (CLAUDE.md 書き換え済み。README は DB の定義を変えず、ユースケース節を追加)。

## 設計文書 (正本)

| 文書 | 内容 |
|---|---|
| [docs/design/12_rag_backend_direction.md](../docs/design/12_rag_backend_direction.md) | 北極星: ポジショニング・要件・責務境界・非目標 |
| [docs/design/13_fulltext_search.md](../docs/design/13_fulltext_search.md) | エンジン側: B+Tree 上 postings・Tx 内維持・混合 bigram/word・BM25 (df オンザフライ)・FullTextScanOp/FusionOp (RRF)・V8 bump |
| [docs/design/14_rag_layer.md](../docs/design/14_rag_layer.md) | Quiver.Rag: スキーマ・IngestedDocument 契約・チャンカー・UpsertDocument・RagSearcher |

主要な設計判断の根拠 (要約):

- **postings は既存 B+Tree 複合キー** — WAL/ARIES/crash 耐性 (BA-9)/orphan sweep を無償継承。専用ページは時期尚早。
- **Tx 内維持 (post-commit にしない)** — HNSW の post-commit は ANN グラフの Tx 化困難ゆえの例外。B+Tree にその制約はなく、read-your-own-writes と crash consistency が既存機構で成立。
- **df は永続カウンタを持たない** — 高頻度 term のホットキー化で取込が SI write-write conflict により直列化するため。term-at-a-time 走査中に数える (追加コストほぼゼロ)。
- **RRF のみ (weighted-sum 非目標)** — `KnnNodeSourceOperator.cs:14` で score は意図的に非公開。RRF は rank のみで計算でき、score 配管なしで融合できる。
- **形態素解析は非目標** — 外部辞書が「Pure C# 組み込み」と衝突。bigram の精度低下はベクトル側 + RRF が補償。

## タスク列 (roadmap / SKILL.md に登録済み)

| ID | 内容 | 依存 | 優先 |
|---|---|---|---|
| FTS-1 | トークナイザ + ITextNormalizer コア移動 | — | P0 |
| FTS-2 | 全文索引永続化 (postings/norms、V8 bump) | FTS-1 | P0 |
| FTS-3 | BM25 検索 (`g.Search`) | FTS-2 | P0 |
| FTS-4 | graph-first (`.FilterByText` + pushdown) | FTS-3 | P1 |
| FTS-5 | RRF 融合 (`g.HybridSearch`) | FTS-3 | P0 |
| FTS-6 | 耐久テスト + ベンチ | FTS-2〜5 | P2 |
| RAG-1 | Quiver.Rag 骨格 | — (FTS と並行可) | P0 |
| RAG-2 | チャンカー | RAG-1 | P0 |
| RAG-3 | 取込/再取込 | RAG-2 | P0 |
| RAG-4 | RagSearcher (hybrid + expansion) | RAG-3 + FTS-5 | P0 |
| RAG-5 | サンプル + cookbook + 契約検証 | RAG-4 | P1 |
| FTS-7 | 取込 WAL 増幅圧縮 (logical postings WAL、2026-06-13 路線承認) | FTS-6 | P1 (GA 前必須) |
| FTS-8 | 検索 postings 枝刈り (WAND / block-max + 近似 df) | FTS-6 | P1 (GA 前必須) |

並列性: FTS-1〜3 と RAG-1〜3 は独立 (RAG-3 まではベクトルのみで動作確認可)。合流点は RAG-4。
FTS-7 / FTS-8 は互いに独立、FTS-6 完了後ならいつでも着手可。

## GA 前性能後続 (FTS-6 実測起点、2026-06-13 起票)

FTS-1〜6 / RAG-1〜5 は完了。FTS-6 の実測で正当性・耐久性は MVP 要件を満たしたが、
性能 2 項目が目標未達と判明し (実測の正本は design 13 §9.1)、性能のみの後続として正式起票した:

| ID | 実測 → 目標 | 根因 | アプローチ |
|---|---|---|---|
| FTS-7 | 取込 WAL 増幅 ~33×/chunk → ≤5× (全 tx 形状) | postings/norms B+Tree の page-image WAL 粒度。leaf が page-logging の 99.1%、CLR+PageImage でページ×tx 2 本 (FT-15/29 で畳み切り済み)。tx 形状依存: batch=10/200/1000 → 78.9×/26.6×/7.5× | **logical postings WAL** (2026-06-13 承認): leaf 更新を論理レコード (~25–40B/キー) 化 + logical CLR undo、SMO は page-WAL 維持。本実装前に spike ゲート (kill criteria: batch=200 ≤5× 見込み、机上 ~3.5–5.5×)。案A (tx 内ソート一括適用) は実測棄却 — ソートは touch leaf 集合を変えず FT-29 が coalesce 済み。案B (BulkLoader) は到達後の任意補完。実測詳細は design 13 §9.2 |
| FTS-8 | 検索 p50 @100k = 267ms → <10ms | term-at-a-time BM25 の全 postings 走査 (走査長 ∝ N、動的枝刈り無し) | WAND (必要時のみ block-max) + df を GraphStats 収集時の近似 snapshot へ移行 + B+Tree Seek を skip pointer として利用。text-first/graph-first の per-doc スコア一致規約 (FTS-4) は維持 |

いずれも「実測先行 (kill criteria 数値固定) → 手段選択」の順を厳守。
実装手順の詳細は `.claude/skills/quiver-implement/tasks/fts.md` の FTS-7 / FTS-8 節。

## 既存 backlog への影響 (roadmap 反映済み)

- HNSW 物理削除 / overwrite 再リンク → **VEC-13 候補に引き上げ** (RAG-3 で削除が常態化。RAG-3 後〜GA 前必須)
- Filtered HNSW ネイティブ化 → VEC-14 候補、P2 のまま (RRF が緩和)。FTS-5 後に再評価
- 配列プロパティ → FT-36 候補、据え置き (`metadataJson` で代替可)

## 完了の定義 (トラック全体)

- `dotnet run --project samples/Quiver.Samples.Rag` が取込→ハイブリッド検索→expansion まで完走
- FTS crash contract 100-iteration 両 backend PASS、`WAL bytes/chunk` sentinel 登録
- 13 番文書 §9 の性能目標 (検索 p50 < 10ms @10万チャンク、取込増幅 5× 以内) の実測値が文書に記録されている
