# FTS-2: 全文索引の永続化 (postings/norms + Tx 内維持)

設計正本: `docs/design/13_fulltext_search.md` §3 (物理表現) / §4 (更新タイミング) / §8 (FormatVersion)。
前提: FTS-1 ✅ (`Quiver.Text`: ITokenizer/MixedBigramTokenizer)。

## 物理表現 (確定)

| 索引 | backing | キー | 値 |
|---|---|---|---|
| postings | `CreateBytesIndex` (byte[]) | `varlen(term_utf8) ‖ term_utf8 ‖ entityId(8B BE sign-flip)` | `tf` (u16 飽和、long に格納) |
| norms | `CreateInt64Index` | `entityId` (long) | `docLen` (u32、long に格納) |

- term postings 列挙 = `Range([len‖term‖0x00..], incl, [len‖term‖0xFF..], incl)` の prefix range scan。
- 長さプレフィックスで "ab"/"abc" の prefix 衝突を防ぐ。

## カタログ / FormatVersion (確定)

- `IndexManager` のカタログを拡張し FT 索引メタを永続化: `{postingsTenantId, normsTenantId, label, propertyKey, tokenizerId}`。
- 既存の secondary-index カタログ (name→{tenantId,typeFlags}) とは別レコード種別として持つ (FT は 2 テナント + 文字列メタを伴うため)。
- `FormatVersion.Current` を V7→**V8** bump。旧 V7 は `FormatVersionMismatchException` で reject (移行コードなし)。

## トークナイザ registry (確定・FTS-1 引き継ぎ)

- `ITokenizerRegistry` + `TokenizerRegistry`: `TokenizerId`→`ITokenizer` を解決。既定で `mixed-bigram-v1`=`MixedBigramTokenizer` を登録。
- クエリ時 (FTS-3) も索引時も **catalog の tokenizerId を registry で解決** する。ハードコード禁止 → `mixed-bigram-v2` は純追加で共存可能。

## 増分

### 増分 1: 純粋基盤 (write-path 非接触)
- `Quiver.Text`: `ITokenizerRegistry` / `TokenizerRegistry`。
- `Quiver.Index` (internal): postings 複合キー codec (encode/decode + term range bounds)。
- `FullTextIndexOptions` (public record: TokenizerId 既定 "mixed-bigram-v1"、k1=1.2/b=0.75 は FTS-3 で使用)。
- ビルドのみ (テストは codec 単体)。

### 増分 2: 索引作成 + カタログ + V8
- `IIndexManager` に FT 索引の作成/列挙/materialize/drop を追加 (postings=bytes, norms=int64 の 2 テナントを 1 論理 FT 索引として束ねる)。
- カタログ直列化に FT レコードを追加、再 open で materialize。
- `ISchemaApi.CreateFullTextIndex(indexName, label, propertyKey, FullTextIndexOptions)` + `SchemaApi` 実装 + backend 配線。
- `FormatVersion.Current = V8`。
- PublicApi approval 更新。
- テスト: 作成→再 open→ListIndexes に出る / tokenizerId 往復。

### 増分 3: Tx 内維持 (write-path、**要・決定 A**)
- `SetProperty(node)` の維持フック (決定 A の結論次第で transparent / explicit)。
- 挿入: 新値を registry 解決トークナイザで tokenize → postings (tf 集計) + norms (docLen) を同一 Tx で書く。
- 更新: before-image (旧プロパティ値) を読んで再 tokenize → 旧 postings/norms を削除 → 新値を書く。
- 削除 (`DeleteNode`): 旧テキスト由来の postings/norms を全削除。
- abort/crash 巻き戻しは既存 B+Tree ARIES (FT-17/19) が自動適用 (postings/norms は通常の B+Tree テナント)。
- テスト: read-your-own-writes (同 Tx 挿入→検索可能は FTS-3 待ち、ここでは postings 直接検証) / 更新で旧 term 消える / rollback で postings 残らない。

### 増分 4: orphan sweep + 契約テスト
- postings-aware orphan 収集 (key から entityId をデコードして `isLive` 判定)、norms は既存経路 (key=entityId)。
- `IndexManager.CollectOrphans` 系に postings 経路を追加。
- テスト: 取りこぼし postings を sweep が修復。両 backend 契約テスト (SQLite は決定 B 次第)。

## 決定済み (ユーザ承認)

- **決定 A (維持トリガ) = transparent**: `SetProperty(node)` 内で、ノードの label+key に FT 索引が bound されている時だけ自動維持。before-image を読んで旧 postings を削除し新値を tokenize。非索引キーの書き込みは dict 参照のみで素通り (design 13 §4 通り)。
- **決定 B (SQLite backend) = binary 限定 MVP**: SQLite backend は `CreateFullTextIndex` を `NotSupportedException`、`ListFullTextIndexes` は空。FTS-6/後続で再検討。

## 進捗

- 増分 1-2 ✅ (commit 70446d2): 永続化層 + API + V8 bump。
- 増分 3 ✅: 透過維持 (SetProperty/DeleteNode フック + before-image 再 tokenize + rollback 整合)。
  付随修正: abort の before-image undo 後に全 B+Tree 索引の in-memory ヘッダ (root/entryCount/height)
  を読み直す `IIndexManager.ReloadAll` を `ReloadStoreMeta` に追加 (既存 secondary 索引の潜在
  EntryCount 陳腐化 / split-during-abort 不整合も同時に解消)。
- 増分 4 (orphan sweep) 未。
