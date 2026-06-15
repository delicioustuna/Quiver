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
| FTS-7 | 取込 WAL 増幅圧縮 (leaf logical postings WAL) ✅ | FTS-6 | P1 (GA 前必須) |
| FTS-8 | 検索 postings 枝刈り (WAND / block-max + 近似 df) ✅ | FTS-6 | P1 (GA 前必須) |
| FTS-9 | logical SMO (split/merge 論理化で全 tx 形状 ≤5×) **未/繰延** | FTS-7 | P2 |

並列性: FTS-1〜3 と RAG-1〜3 は独立 (RAG-3 まではベクトルのみで動作確認可)。合流点は RAG-4。
FTS-7 / FTS-8 は互いに独立、FTS-6 完了後ならいつでも着手可。FTS-9 は FTS-7 の残差クロージャ (繰延)。

## GA 前性能後続 (FTS-6 実測起点、2026-06-13 起票)

FTS-1〜6 / RAG-1〜5 は完了。FTS-6 の実測で正当性・耐久性は MVP 要件を満たしたが、
性能 2 項目が目標未達と判明し (実測の正本は design 13 §9.1)、性能のみの後続として正式起票した:

| ID | 実測 → 目標 | 根因 | アプローチ |
|---|---|---|---|
| FTS-7 ✅ | 取込 WAL 増幅 26.6×/chunk → **~11×** (commit `549b486`) | postings/norms B+Tree の page-image WAL 粒度。leaf が page-logging の 99.1%、CLR+PageImage でページ×tx 2 本 (FT-15/29 で畳み切り済み)。tx 形状依存: batch=10/200/1000 → 78.9×/26.6×/7.5× | **leaf logical postings WAL** 実装済: leaf 更新を論理レコード `FtLeafMutation`(17) 化 + 2 相 recovery (presume-committed) + logical undo、SMO は Full page-WAL 維持 (eager `FtStructureImage` は batch=1000 で 224× 爆発のため棄却)。FormatVersion V8→V9。**実測 26.6×→~11×、実 RAG 増分 batch=10 = 4.70× ≈ 目標** (GA ブロッカーは実ワークロードで充足)。spike の 3.70× は split 構造 image を 2–4× 過小評価で実測が refute。全形状 ≤5× は **FTS-9 へ繰延**。実測詳細は design 13 §9.2/§9.2.1/§9.2.2/§10 |
| FTS-8 ✅ | 検索 p50 @100k = 267ms → **7.77ms** (commit `3095306`) | term-at-a-time BM25 の全 postings 走査 (走査長 ∝ N、動的枝刈り無し) | WAND (素 WAND で目標達成、block-max 不要) + df を GraphStats 収集時の近似 snapshot へ移行 + B+Tree Seek を skip pointer として利用。text-first/graph-first の per-doc スコア一致規約 (FTS-4) は維持。実測詳細は design 13 §9.3/§9.3.1 |
| FTS-9 未/繰延 | 取込 WAL 増幅 ~11× → ≤5× (全 tx 形状) | FTS-7 が残した split/merge **構造ページの page-WAL** (~24KB/split、木成長率に比例し tx 形状非依存に残る)。batch=200=10.76× / batch=1000=8.98× (steady はさらに悪化) | **logical SMO (decision D)**: split/merge も論理レコード化し recovery で構造再構築。ARIES 大改修。費用対効果から **2026-06-15 繰延正式化** (実 RAG 経路は FTS-7 で既に達成のため P2)。案B BulkLoader (sequential fill) は初回/バルク取込の軽量代替 |

いずれも「実測先行 (kill criteria 数値固定) → 手段選択」の順を厳守。
実装手順の詳細は `.claude/skills/quiver-implement/tasks/fts.md` の FTS-7 / FTS-8 / FTS-9 節。
**2026-06-15 更新**: FTS-7 (leaf 論理化 ~11×) / FTS-8 (WAND 7.77ms) 完了。FTS-7 の残差 (全形状 ≤5×) は
split 構造 page-WAL の logical 化 = **FTS-9 (logical SMO)** として正式に切り出し、費用対効果から繰延
(実 RAG 増分経路 batch=10 = 4.70× で GA ブロッカーは実ワークロード上は充足)。

## 既存 backlog への影響 (roadmap 反映済み)

- HNSW 物理削除 / overwrite 再リンク → **VEC-13 候補に引き上げ** (RAG-3 で削除が常態化。RAG-3 後〜GA 前必須)
- Filtered HNSW ネイティブ化 → VEC-14 候補、P2 のまま (RRF が緩和)。FTS-5 後に再評価
- 配列プロパティ → FT-36 候補、据え置き (`metadataJson` で代替可)

## 完了の定義 (トラック全体)

- `dotnet run --project samples/Quiver.Samples.Rag` が取込→ハイブリッド検索→expansion まで完走
- FTS crash contract 100-iteration 両 backend PASS、`WAL bytes/chunk` sentinel 登録
- 13 番文書 §9 の性能目標 (検索 p50 < 10ms @10万チャンク、取込増幅 5× 以内) の実測値が文書に記録されている
  - 検索 p50: **7.77ms @100k** 達成 (FTS-8)。取込増幅: 実 RAG 増分経路 (per-doc upsert batch=10) = **4.70×** で達成、
    バルク/バッチ (batch=200/1000) は **~11×** で未達 → **FTS-9 (logical SMO) へ繰延** (2026-06-15、P2)。
    実ワークロード (RAG 取込) 上は GA ブロッカー充足の判断。
