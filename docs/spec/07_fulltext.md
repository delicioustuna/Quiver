# 全文検索

> as-built 仕様（QUIVER-SW family version 2、2026-08-03）

## Definition {#definition}

全文インデックスは `FullTextIndexDefinition` として統一 schema catalog に登録する。

作成には `IWriteTransaction.EditSchema.CreateIndex(IndexDefinition)` を使い、削除には `DropIndex` を使う。

definition は `PropertyTarget`、tokenizer、filter pipeline、BM25 の `K1` と `B`、`FullTextSegmentPolicy` を保持する。

全文専用のlegacy schema surfaceは公開 API に存在しない。

## Immutable segment {#immutable-segment}

全文 artifact は mutable B+Tree ではなく、commit-local delta segment と merged segment から構成する。

各 document entry は full typed owner identity、`PropertyVersionRef`、term frequency、document length、tombstone を保持する。

property の set と update は新しい document entryを追加し、property remove と owner delete は tombstone を追加する。

commit 済み segment は in-place 更新しない。

manifest は transaction ID の `xmin` と `xmax` で version 化し、read transaction の snapshot から可視な版を選ぶ。

segment body は `*.yata-ftseg/` ディレクトリへ checksum 付き immutable artifact file として保存する。
body を fsync した後、artifact ID、length、checksum、source committed high-water を持つ manifest だけを catalog page に書く。
manifest と primary property mutation は同じ strict `Commit` で可視になる。

old reader は開始時に可視だった manifest と property version を読み続け、新 reader だけが publish 後の manifest を選ぶ。

同じ manifest generation から materialize した postings、norms、corpus stats は読み取り専用 snapshot として再利用する。

## Merge と rebuild {#merge-rebuild}

segment policy の document 変更数、segment 数、tombstone 比率を超えると background merge を要求する。

primary text property の scan と immutable artifact 構築は read transaction で行い、writer lease を保持しない。

構築後に短い write transaction を開始し、source manifest generation と current definition が一致する場合だけ publish する。

構築中に delta または definition が変わった場合は stale artifact を破棄し、新しい snapshot から再試行する。

正常 reopen は catalog の persisted manifest と checksum が一致する segment body を直接開き、primary property を scan しない。

referenced body の欠損または checksum 不一致を検出した場合は definition を `RebuildRequired` にし、検索は同じ transaction の primary property scan から結果を復元する。

transaction-local fallback artifact を global manifest として公開しない。

## トークナイザ {#tokenizer}

`MixedBigramTokenizer` はユニグラム併用モード `mixed-bigram-unigram-v1` とバイグラム専用モード `mixed-bigram-v1` を持つ。

既定はユニグラム併用モードである。

入力は NFKC と ASCII 小文字化で正規化する。

Latin と ASCII は空白区切りの word token とし、CJK の連続は重なり bigram として処理する。

ユニグラム併用モードは CJK の補足 unigram も放出するが、補足 unigram を document length に含めない。

`LowercaseFilter`、`StopWordFilter`、`JapaneseOrthographicVariantFilter` の設定は versioned definition payload に保存し、
reopen 後に同じ pipeline を再構築する。filter の種類だけでなく stop word と異字体写像も definition の同一性に含め、
同じ index 名へ異なる設定を指定した場合は拒否する。

`JapaneseOrthographicVariantFilter` は既定 pipeline へ自動追加しない opt-in filter である。parameterless 構成は
`邉/邊↔辺`、`髙↔高`、`﨑↔崎`、`濵/濱↔浜`、`齋↔斎`、`齊↔斉`、`德↔徳`、`澤↔沢`、
`瀨↔瀬`、`眞↔真`、`廣↔広`、`國↔国`、`學↔学`、`櫻↔桜` を同一字形グループとして扱う。
利用者は一対一の異字体→標準字体写像を constructor へ渡し、必要な組だけへ限定できる。
原文 property は変更せず、各 token の原形、標準形、同じ標準形へ結び付く登録異字体を索引時と検索時の双方で
補足 token として放出する。一般の類義語や意味的な同義性は推測しない。

永続表現を持たない custom `ITokenFilter` は schema definition として受け付けない。

## BM25 と WAND {#bm25}

BM25 は definition の `K1` と `B` を使う。

既定値は `K1 = 1.2`、`B = 0.75` である。

```text
IDF(term) = log(1 + (N - df + 0.5) / (df + 0.5))

Score(term, doc) = IDF × (tf × (K1 + 1)) / (tf + K1 × (1 - B + B × docLen / avgdl))
```

`N`、`df`、総 document length、WAND 上界は、検索と同じ visible segment snapshot から求める。

document length は UTF-8 byte 数や UTF-16 code unit 数ではなく `INormTokenCounter` が返す token 数である。
CJK bigram では run 長 `n` に対して `n - 1`、孤立 CJK 文字は 1 とする。補足 unigram と異字体展開 token は
postings と term frequency には参加するが document length へ重ねて加算しない。

WAND 上界は `idf × (K1 + 1)` とし、上界の保守性を証明できない場合は strict scan へ fallback する。

strict scan と WAND は score 降順、同点時 packed owner ID 昇順で決定論的に並べる。

## Candidate validation {#candidate-validation}

segment candidate は top-k 確定前に primary snapshot で再検証する。

検証対象は owner kind、owner Generation、entity visibility、`PropertyVersionRef`、property owner である。

deleted owner、slot 再利用後の別 owner、old property version、tombstone、別 target の entry は結果へ出さない。

candidate を除外した後に次点を補充してから `Take(k)` を適用する。

graph-first 経路は上流の full `VertexId` を primary `Read` で検証した後にだけ Sequence を physical posting lookup へ渡す。

`Yatagarasu.Rag` の `MetadataEquals` も一致文書の chunk candidate を BM25 scorer へ渡し、候補集合内で top-k を確定する。
BM25 の累積 score は順位から再計算せず、scorer が生成した値を `RagHit.Score.Bm25Score` へ渡す。

## Query {#query}

`Search`、prefix、fuzzy、boolean、`FilterByText` は transaction snapshot から definition と manifest を解決する。

prefix と fuzzy は visible snapshot の term dictionary だけを展開対象にする。

hybrid search は全文と vector を同じ read transaction から評価し、共通の RRF 実装で順位を統合する。
RAG hit は BM25 score、vector similarity、RRF score、融合方式、rank 定数 60 を返す。

## WAL と recovery {#wal-recovery}

全文専用 WAL record、logical redo、compensation、loser undo、recovery pass は存在しない。

definition catalog と segment manifest の変更は通常の transaction-owned `PageImage` と strict `Commit` で durable にする。

segment body は manifest commit より前に fsync し、page-image WAL へ複製しない。
body fsync 後かつ manifest commit 前の crash は未参照 orphan を残すだけである。
maintenance は committed manifest の参照集合を作り、未参照 orphan file と reader horizon を越えた旧世代 file を削除する。
現在または active reader が参照できる manifest の artifact は削除しない。

commit のない definition publish は recovery winner にならない。

詳細は [WAL とリカバリ](02_wal_recovery.md) を参照する。
