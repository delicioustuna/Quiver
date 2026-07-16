# 全文検索

> as-built 仕様（QUIVER-SW family version 1、2026-07-15）

## アーキテクチャ {#architecture}

各全文インデックスは、コンテナテナントを共有する 2 つの B+Tree から構成される:

| B+Tree | キー | 値 | 目的 |
|---|---|---|---|
| **Postings** | `(term, entityId)` 複合 | `tf` (uint16, 飽和) | 転置インデックス |
| **Norms** | `entityId` (int64) | `docLen`（文書長） | BM25 の長さ正規化 |

## トークナイザ {#tokenizer}

`MixedBigramTokenizer` は 2 つのモードを持つ。インデックス作成時に `TokenizerId` で選択する。

### ユニグラム併用モード (デフォルト) {#tokenizer-unigram}

id = `mixed-bigram-unigram-v1`（`FullTextIndexOptions` の既定値）

CJK 連続 2 文字以上のランで、バイグラムに加えて各文字の**補足ユニグラム**を放出する。
1 文字の CJK 検索クエリが、隣接文字に関わらずヒットする。

| 入力 | 放出トークン |
|---|---|
| `粉体工学` | バイグラム: `粉体` `体工` `工学`、ユニグラム: `粉` `体` `工` `学` |
| `猫`（孤立 CJK 1 文字） | ユニグラム: `猫` |
| `Hello` | ワード: `hello` |

**Norms 計算**: 補足ユニグラムは `docLen` に含めない（バイグラム + ワード + 孤立ユニグラムのみ）。
これにより BM25 の長さ正規化パラメータ (k1, b) がバイグラム専用モードと同一のチューニングで機能する。

**Fuzzy 展開制限**: CJK ユニグラム同士の置換展開を禁止する。全 CJK 文字が相互に
Levenshtein 距離 1 となり N² 爆発するため。

### バイグラム専用モード {#tokenizer-bigram}

id = `mixed-bigram-v1`

CJK 連続は重なりバイグラムのみ。孤立 CJK 1 文字はユニグラムとして放出する。
補足ユニグラムは生成しないため、インデックスサイズが小さく CJK 頻出文字の
postings 肥大がない。1 文字検索は `粉*`（プレフィクス展開）で代替する。

### 共通仕様

- 入力は NFKC + ASCII 小文字化で正規化
- Latin/ASCII: 空白区切り、小文字化したワードトークン
- CJK / Latin の混在: Unicode スクリプト境界でシームレスに切り替え
- `TokenizerRegistry` に両バリアントが自動登録される

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

## WAL とリカバリ {#wal-recovery}

Postings と Norms の B+Tree は、他の永続 B+Tree と同じ `PageImage` WAL を使う。
リーフ更新、split、merge、root 更新を区別する全文専用 record は持たない。
同じ transaction で同じページを複数回更新した場合は、commit 時に最終 image へ集約する。

commit 前の abort と savepoint rollback は、transaction-owned write set の before-image を使う。
crash recovery は明示的な `Commit` を持つ transaction の `PageImage` だけを redo する。
全文専用の論理 redo、compensation、loser undo は実行しない。

詳細は [WAL とリカバリ](02_wal_recovery.md) を参照する。
