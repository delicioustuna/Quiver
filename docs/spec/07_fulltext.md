# 全文検索

> as-built 仕様 (v1 baseline)

## アーキテクチャ {#architecture}

各全文インデックスは、コンテナテナントを共有する 2 つの B+Tree から構成される:

| B+Tree | キー | 値 | 目的 |
|---|---|---|---|
| **Postings** | `(term, entityId)` 複合 | `tf` (uint16, 飽和) | 転置インデックス |
| **Norms** | `entityId` (int64) | `docLen`（文書長） | BM25 の長さ正規化 |

## トークナイザ {#tokenizer}

`MixedBigramTokenizer`（デフォルト、id = `mixed-bigram-v1`）:
- CJK 文字: bigram 分解
- Latin/ASCII: 空白区切り、小文字化
- 混在: CJK の bigram と Latin トークンをシームレスに切り替え

## Postings キーエンコーディング {#postings-key}

`PostingsKey.Encode(termUtf8, entityId)` は B+Tree 用の byte[] キーを生成する:
- term バイト列（可変長）の後にエンティティ ID (int64 big-endian) を続ける
- `PostingsKey.TermRange(termUtf8)` は、ある term に一致する全エンティティを prefix スキャンするための
  `(lower, upper)` 境界を返す

## BM25 スコアリング {#bm25}

標準パラメータの Okapi BM25:

| パラメータ | 値 |
|---|---|
| k1 | 1.2 |
| b | 0.75 |

### 数式 {#bm25-formulas}

```
IDF(term) = log(1 + (N - df + 0.5) / (df + 0.5))

Score(term, doc) = IDF × (tf × (k1 + 1)) / (tf + k1 × (1 - b + b × docLen / avgdl))
```

ここで:
- `N` = 総文書数
- `df` = 文書頻度（その term を含む文書数）
- `tf` = 文書内での term 頻度
- `docLen` = 文書長（norms から取得）
- `avgdl` = 平均文書長

### コーパス統計 {#corpus-stats}

`Bm25CorpusStats`: DocumentCount, AverageDocLength, term ごとの統計（任意）。
`Bm25TermStats`: `{term -> (df, maxTf)}`, MinDocLen。

## WAND Top-K {#wand}

`Bm25Scorer.RankWand()` は、効率的な top-k 取得のための Weighted AND (WAND) アルゴリズムを実装する:

1. 各クエリ term について、その寄与の **上限** を計算する:
   `ub = IDF × (maxTf × (k1 + 1)) / (maxTf + k1 × normMin)`
2. term カーソルを現在の文書 ID でソートする
3. **Pivot**: 整列したカーソルの累積上限が `theta`（これまでに見た k 番目に良いスコア）を
   超える最初の文書を探す
4. その pivot 文書を完全評価したスコアが theta を超えれば、top-k ヒープに挿入する
5. 候補でない文書をスキップするため、`SeekTo` で遅れているカーソルを前進させる

## Reciprocal Rank Fusion (RRF) {#rrf}

全文検索とベクトル検索の結果を組み合わせる（ハイブリッド検索）際、RRF はランク付きリストをマージする:

```
RRF_score(doc) = sum(1 / (k + rank_i(doc)))
```

ここで `k` は平滑化定数（デフォルト 60）であり、`rank_i` は結果リスト `i` における文書の順位である。

## 論理 WAL {#logical-wal}

全文 postings と norms は、リーフ更新に page-image ログではなく **論理 WAL レコード**
(`FtLeafMutation`) を用いる。これにより、大量のテキスト取り込み時の WAL 増幅を劇的に削減する。

### レコード種別 {#ft-wal-records}

| WAL 種別 | 値 | 目的 |
|---|---|---|
| `FtLeafMutation` | 17 | state-setting なリーフ mutation（Upsert または Delete） |
| `FtStructureImage` | 18 | 構造ページ（split/merge/root）の after-image |

### ジャーナリングモード {#ft-journaling}

| モード | リーフ | 構造 (SMO) |
|---|---|---|
| **Suppressed** | page-image なし。FtLeafMutation のみ | N/A |
| **RedoOnly** | N/A | eager な PageImage、nested top action（undo なし） |
| **Full** | 標準の page-image + CLR | 標準の page-image + CLR |

リーフ更新は Suppressed モードを用いる: FtLeafMutation レコードのみがログされる。
構造変更（split, merge, root の変更）は RedoOnly モードを用いる: after-image が `FtStructureImage`
レコードとして書き込まれ、リカバリ中に無条件で redo され、undo されることはない（nested top action のセマンティクス）。

### リカバリ {#ft-recovery}

リカバリ中:
- **Pass 2a**: `FtStructureImage` レコードは無条件に redo される（nested top action）
- **Pass 2b**: コミット済みトランザクションの `FtLeafMutation` レコードは `ApplyFtLeafRedo` で
  redo される（state-setting、冪等）
- **Pass 3**: loser トランザクションの `FtLeafMutation` レコードは逆操作で undo される
  （Upsert は Delete に、Delete は保存値での Upsert になる）

### WAL 増幅 {#wal-amplification}

論理 WAL は postings の WAL 増幅を ~50-100x（リーフに触れるたびの page-image）から
~5x（posting ごとの論理 mutation）へ削減する。batch=10 での実測値: 4.70x。
