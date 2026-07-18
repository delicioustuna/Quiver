# Wave 8 着手指示書: full-text segments

> 効力宣言: 本書と設計正本が食い違う場合は設計正本を優先し、食い違いをユーザへ報告する。
> 作成日: 2026-07-18
> 対応する正本のバージョン: `ede755edac004606e618bf8d77e3d8f01d7f2eb6`
> ステータス: 承認済み(2026-07-18)

## 1. 着手前チェック

- annotated tag `redesign-wave-7` が存在し、`develop` と `redesign/single-writer` は Wave 7 merge commit `cf0b2c90c3d3d0420aad500512b20a9ce48da5d4` に一致する。
- branch は `redesign/single-writer`、作業場所は専用 worktree `D:/csharp/Quiver-sw` であり、未コミット変更と `develop` 未マージ commit はない。
- 正本 §2.3〜§2.5、§4.2、§4.6、§5.1、§5.3〜§5.4、§7.5〜§7.7、§8.1、§8.4、§9 Wave 8、§10.2〜§10.4、§11.2、§13 と review C-5、C-7、M-3、M-4、M-5 の境界が一致する。
- review C-5 は全文 candidate と search result を full typed ID へ materializeし、owner Generationとproperty version visibilityをprimary storeで再検証する設計である。
- review C-7 はfiltered full-textのlogical pipelineがfull typed IDを保持し、physical lookupへ渡すSequenceをprimary `Read`成功直後の値に限定する設計である。
- review M-3 はimmutable full-text segmentをread snapshotからwriter lease外で構築し、source manifest generationの再検証とpublish commitだけをlease内に置く設計で解決済みである。
- review M-4 はfull-text 4 segment p50とsegment WAL amplificationの出所を`clean-slate` baselineへ分離し、本Waveの性能gateを8.55 ms以下かつ11.74x以下に固定している。
- review M-5 は旧`CreateFullTextIndex`とfull-text専用schema surfaceを`CreateIndex(FullTextIndexDefinition)`へ置換する設計である。
- Wave 3のowner-bound text property version、Wave 4のsnapshot visibilityとidentity materializer、Wave 5のtransaction-owned page-image WAL、Wave 6のunified `IndexDefinitionCatalog`とtyped query pipeline、Wave 7のimmutable segment manifestとhybrid同一snapshotの基盤が実在する。
- 現行のmutable postings/norms B+Tree、`FullTextIndexInfo`、`FullTextIndexOptions`、旧`CreateFullTextIndex`、full-text専用catalog APIは、本Waveでtarget contractへ置換する入力であり、compatibility surfaceとして残さない。
- `plans/fulltext-vector-segment-contract.md`は性能spike前のhistorical contractである。
  同文書の「既存public API形状を変えない」という記述は、後発の設計正本 §5.4、§7.5、§8.1、§9 Wave 8によって置換済みであり、実装指示として使わない。
- 本書の目標状態と検証計画についてユーザの着手承認を得てから、`ステータス: 承認済み(2026-07-18)`に更新し、doc-only commitをpushする。

## 2. 読む順序

1. 正本 §9 Wave 8。
2. 正本 §2.3〜§2.5、§4.2、§4.6、§5.1、§5.3〜§5.4。
3. 正本 §7.5〜§7.7、§8.1、§8.4。
4. 正本 §10.2〜§10.4、§11.2、§13〜§16。
5. review C-5、C-7、M-3、M-4、M-5。
6. `plans/fulltext-vector-segment-contract.md`。
7. `docs/spec/00_overview.md`、`docs/spec/01_storage_paging.md`、`docs/spec/02_wal_recovery.md`、`docs/spec/03_mvcc.md`、`docs/spec/04_records_index.md`、`docs/spec/05_query.md`、`docs/spec/07_fulltext.md`、`docs/spec/08_known_limits.md`、`docs/design/development.md`。

## 3. 目標状態と検証計画

Wave 8は全文propertyのdefinition、commit maintenance、snapshot検索、immutable segment lifecycle、hybrid検索を一つの利用者contractへ変更する。

- **unified full-text definition contract**：public `FullTextIndexDefinition`を`IndexDefinition`の派生型として実装し、`PropertyTarget`、tokenizer、normalizer、filters、BM25 parameters、segment policyを保持する。
  schema mutationは`IWriteTransaction.EditSchema.CreateIndex(IndexDefinition)`とdrop lifecycleに統一し、`CreateFullTextIndex`、`FullTextIndexOptions`、`FullTextIndexInfo`、`ListFullTextIndexes`を削除する。
  definitionはWave 6のtransactional catalogへ永続化し、reopen後も一致するtext property mutationをindex commit batchへ送る。
- **text analysis contract**：`ITextNormalizer`、`ITokenizer`、`ITokenFilter`、`MixedBigramTokenizer`、`FilteredTokenizer`、built-in filters、normalizer、registryを再利用する。
  tokenizerとfilter pipelineはdefinitionから決定的に復元し、writeとsearchで同じ正規化結果を使う。
  `PostingsKey`のterm codecと`Bm25Scorer`の採点規則はsegment-local dictionary、postings、norms、statsへ適合させて再利用する。
- **segment write contract**：一致するtext propertyのset、update、remove、owner deleteを、commit-local immutable delta segmentのposting、norm、統計、tombstoneへ変換する。
  segment entryはfull typed owner identity、`PropertyAddress`、`PropertyVersionRef`、term frequency、document lengthを保持し、property versionとmanifestを同じcommitで可視化する。
  typed CRUD、fluent mutation、SourceGen、bulk loadの全入口は同じdefinition matchingを通る。
- **immutable segment contract**：mutable postings/norms B+Treeを、snapshot-visibleなimmutable delta segment、immutable merged segment、versioned manifestへ置換する。
  segmentはterm dictionary、postings、norms、segment-local corpus stats、checksumを持つ。
  commit済みsegmentをin-place更新せず、更新と削除は新document generationとtombstoneで表す。
  old readerは旧manifest、新readerはpublish済みmanifestを選ぶ。
- **snapshot stats contract**：BM25の`N`、`df`、総document length、WAND上界は、同じread transactionからvisibleなsegment統計を合算する。
  tombstoneまたは不可視property versionはtop-k確定前のcandidate validationで除外し、同じownerとproperty addressの複数versionはsnapshotからvisibleな最新versionだけを採用する。
  segment fan-outとmergeの前後でstrict BM25 scanのscore順、同点処理、top-k結果を一致させる。
- **transaction-scoped search contract**：`Search`、prefix、fuzzy、boolean、graph-first filtered full-textはtransaction snapshotからvisibleなdefinitionとmanifestを解決する。
  public resultはfull typed owner IDだけを返し、owner Generation、entity visibility、property version、property targetをprimary storeで再検証する。
  same-sequence/different-generation、deleted owner、old text version、別targetのpostingはlogical outputへ出さない。
- **WAND contract**：WANDはvisible segmentごとにpostings cursorと保守的なscore上界を使い、segment-local top-kをglobal top-kへmergeする。
  上界またはstatsの鮮度が枝刈りの正しさを証明できない場合はstrict scanへfallbackする。
  strict scanとの一致を性能より優先し、candidate validationで除外したhitの後続候補を補充してtop-kを満たす。
- **filtered full-text contract**：graph-first経路はtyped owner candidateまたはpredicateを受け、logical pipelineではfull `VertexId`、`EdgeId`、`NexusId`を保持する。
  physical posting lookupへ渡すSequenceはprimary `Read`成功直後の値に限り、raw longをlogical identity、query入力、transaction入力として再利用しない。
  text-firstとgraph-firstは同じsnapshotとcandidate predicateで同じvisible result setを返す。
- **mergeとrebuild contract**：重いsegment構築はread snapshotからwriter lease外で行う。
  構築完了後に短いwrite transactionを開始し、source definition generationとmanifest generationが一致するときだけ旧manifestの`xmax`と新manifestの`xmin`を同じcommitでpublishする。
  generationが変わっていればartifactを公開せず破棄し、新しいsnapshotから再試行する。
  rebuildはprimary text propertyをscanするため、derived segmentの欠落またはchecksum不整合でproperty値を失わない。
- **hybrid same-snapshot contract**：hybrid検索はfull-textとvectorのdefinition、manifest、property visibilityを同じread transactionのsnapshotから解決し、異なるmanifest generationの結果を混ぜない。
  mergeが片方で進行しても既存readerのRRF入力は固定され、新しいreaderだけがpublish後のmanifestを使う。
  score内訳とtop-k前candidate push-downの利用者contractはWave 9へ残す。
- **WALとrecovery contract**：`FtLeafMutation`、`FtStructureImage`、full-text専用logical redo、compensation、loser undo、recovery passを削除する。
  segment body、manifest、definition catalogはWave 5の通常page-image transactionとstrict `Commit` recordでdurableにする。
  recovery後にmanifestが指すsegmentはchecksumまで完全でなければならず、未参照segment bodyは不可視のorphanとしてrebuildまたはmaintenanceの回収対象にする。
- **補修方法**：最初にpublic definition、schema catalog、property maintenance、segment manifest、BM25/WAND、filtered operator、hybrid snapshot、WAL/recoveryをtarget contractへ一括変更する。
  続けてsolution buildのcompiler errorを旧schema API、mutable index dependency、raw candidate dependencyの一覧として補修する。
  build成功後はPublicApi、tokenization、definition reopen、segment commit、strict BM25、old/new snapshot、tombstone、candidate validation、merge/rebuild、operators、Rag、crash、性能、publish stallの順で契約漏れを補修する。
- **契約保証**：definition reopen、全mutation入口、read-your-writes、old/new snapshot、update/delete、same-sequence/different-generation、WAND strict scan一致、segment merge中のwriter wait、source generation change retry、derived corruption rebuild、crash、hybrid same-snapshotを回帰testへ追加または更新する。
  as-builtはunified full-text definition、immutable segment、snapshot stats、candidate validation、manifest publish、通常WAL recoveryを実装済みcontractとして更新する。

## 4. 本Wave固有の落とし穴

- mutable postingsまたはnorms B+Treeをsegmentの内部実装として残し、commit済みsegmentをin-place更新しない。
  immutable artifactというcontractが破れると、old readerが開始後のupdateとdeleteを観測する。
- segment-local owner keyをraw Sequenceだけにしない。
  full typed identityとprimary property versionの再検証がなければ、slot再利用後の別entityへpostingがretargetする。
- segment統計をdatabase全体のmutable singletonから読まない。
  `N`、`df`、総document length、WAND上界は検索と同じsnapshotでvisibleなsegment集合から決める。
- tombstoneをsearch後の単純な件数削減として処理しない。
  除外後に次点candidateを補充しなければ、top-kが不足し、text-firstとgraph-firstの結果がずれる。
- WANDの古い上界で枝刈りしない。
  上界の保守性を証明できない場合はstrict scanへfallbackし、誤った高速化を採用しない。
- segmentごとのtop-kを連結した結果をそのまま返さない。
  同じownerとproperty addressの旧versionを重複排除し、primary snapshotで可視性を再検証してからglobal top-kを確定する。
- segment構築中にwriter leaseを保持しない。
  lease内に置くのはsource generationの再検証とmanifest publish commitだけであり、publish p99は`WriterWaitTimeout`既定値の10%以内に収める。
- stale artifactを無条件にpublishしない。
  definitionまたはmanifest generationが構築中に変化した場合はartifactを破棄し、新しいread snapshotから再構築する。
- old manifestとsegment bodyをpublish直後に物理削除しない。
  Wave 9のsegment GCがreader horizonを越えるまで保持する。
- derived segmentの欠落またはchecksum不整合をprimary text propertyのcorruptionとして扱わない。
  definitionとprimary propertyが健全なら`RebuildRequired`へ遷移し、base scanから再構築する。
- full-text専用WAL recordとrecovery passを互換目的で残さない。
  target formatは通常page imageとstrict `Commit`だけでwinnerを決める。
- `FullTextIndexDefinition`を旧`FullTextIndexOptions`の名前変更だけで済ませない。
  `PropertyTarget`とtransactional lifecycleへ参加させ、旧label固定APIを削除する。
- `plans/fulltext-vector-segment-contract.md`の旧public API維持規則を復活させない。
  現行の設計正本はunified `CreateIndex(IndexDefinition)`へのclean breakを要求している。
- tokenizerの回帰とsegment化を混同しない。
  既存tokenizer、normalizer、filterの出力は維持し、保存先とsnapshot lifecycleだけを変更する。
- Wave 9のsegment GC、candidate push-downのRAG利用者contract、score内訳、maintenance schedulingを前倒ししない。
  該当contract変更が必要ならprocess §5へ戻る。

## 5. 完了確認

| gate | 適用/N/A | コマンドまたは差分根拠 | 合格条件 |
|---|---|---|---|
| 機能test | 適用 | `dotnet build Quiver.slnx -v minimal`、`dotnet test tests/Quiver.PublicApi.Tests/Quiver.PublicApi.Tests.csproj --no-build`、`dotnet test tests/Quiver.Index.Tests/Quiver.Index.Tests.csproj --no-build`、`dotnet test tests/Quiver.Operators.Tests/Quiver.Operators.Tests.csproj --no-build`、`dotnet test tests/Quiver.PropertyTests/Quiver.PropertyTests.csproj --no-build`、`dotnet test tests/Quiver.Tests/Quiver.Tests.csproj --no-build`、`dotnet test tests/Quiver.Rag.Tests/Quiver.Rag.Tests.csproj --no-build`、solution全test project | 0 errors、0 warnings、全対象test成功。unified definition、全mutation入口、immutable segment snapshot、strict BM25/WAND、candidate validation、hybrid same-snapshotがbinaryとin-memory両backendで成立する |
| crashtest | 適用 | binary backendでdelta segment body、manifest publish、update tombstone、merge publish、rebuild publishのcommit境界kill matrixを実行し、full-text chaos filterとconsistency testを実行する | `Commit`なしsegmentとmanifestは不可視で、`Commit`済みmanifestは完全なsegmentだけを参照する。delete済みdocumentが復活せず、入力segmentとmerge出力が同じsnapshotで二重可視にならない。derived欠落または破損はprimary openを壊さずrebuildへ収束する |
| baseline gate | 適用 | `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --clean-slate-segment-spike`、product full-text segmentの4 segment検索、merge前後、publish stallを測るrunner、`dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --fts6`を実行する | 4 segment p50が8.55 ms以下、BM25 top-kがstrict scanと一致する。RAG ingest WAL amplificationが11.74x以下でpayload/index別内訳を記録する。lease保持中publish p99は`WriterWaitTimeout`既定値の10%以内であり、重いartifact構築時間を含めない。環境、commit、生出力を`docs/benchmarks/`へ保存する |
| as-built更新 | 適用 | `docs/spec/00_overview.md`、`docs/spec/01_storage_paging.md`、`docs/spec/02_wal_recovery.md`、`docs/spec/03_mvcc.md`、`docs/spec/04_records_index.md`、`docs/spec/05_query.md`、`docs/spec/07_fulltext.md`、`docs/spec/08_known_limits.md`、`docs/design/development.md`、README、samplesのactive contract scan | mutable postings/norms、旧full-text schema API、full-text専用WAL/recovery、snapshot外statsの記述が残らず、unified definition、immutable segment、manifest visibility、candidate validation、通常WAL recoveryと一致する |

追加条件は次のとおりである。

- `FullTextIndexDefinition`、segment policy、full-text search resultに新規public surfaceがある場合は日本語XML docがあり、PublicApi approvalが最終contractだけを含む。
- production source、tests、benchmarks、samples、Rag、active docsにpublic `CreateFullTextIndex`、`FullTextIndexOptions`、`FullTextIndexInfo`、`ListFullTextIndexes`、mutable `FullTextIndex`、full-text専用WAL recordとrecovery passの意図しない参照が0件である。
- reopen後も`FullTextIndexDefinition`と`PropertyTarget`がprimary schema catalogから復元され、再作成なしで該当text property mutationがsegment commit batchへ入る。
- typed CRUD、fluent mutation、SourceGen、bulk loadのset、update、remove、owner deleteが同じdefinition matchingとtombstone規則を通る。
- commit前、abort後、commit後、read-your-writesのsegment visibilityがtransaction contractと一致する。
- old readerはupdate、delete、segment publish前のmanifestとproperty versionを読み続け、新readerはpublish後のmanifestとcurrent property versionだけを見る。
- same-sequence/different-generation、deleted owner、old property version、別property targetのpostingがlogical outputへ出ない。
- full-text resultはfull typed owner IDを返し、raw Sequenceまたはdiagnostic raw longがquery、traversal、transaction入力へ流入しない。
- tokenization、normalization、filter pipeline、prefix、fuzzy、boolean queryの既存意味がsegment化の前後で一致する。
- 4 segmentのBM25 strict scan、WAND、merge後searchが同じscore順、同点処理、top-k結果を返す。
- tombstoneとcandidate validationでhitを除外した後も次点candidateを補充し、要求されたtop-kを可能な範囲で満たす。
- text-firstとgraph-first filtered full-textが同じsnapshotとcandidate predicateで同じvisible result setを返す。
- segment artifact構築中に通常writerが進行し、lease保持中publish p99が性能gateを満たす。
- source definitionまたはmanifest generationが構築中に変化した場合、stale artifactはpublishされず、新しいsnapshotから再試行される。
- segmentの削除またはchecksum不整合がprimary text propertyを失わせず、`RebuildRequired`とbase scanからのrebuildでsearch pathへ戻る。
- full-textとvectorを融合するoperatorとRagは、同じread transactionのsnapshotから両manifestとproperty visibilityを解決する。
- full-text専用logical WAL record、compensation、loser undo、recovery passが0件であり、strict `Commit`の有無だけでsegment manifestのwinnerを決める。
- product-pathの4 segment p50、ingest WAL amplification、publish stallが正本 §10.4のgateを満たし、測定環境、commit、生出力、payload/index別内訳が記録される。
- staged pathに対する`scripts/agent-guardrails/check-track-markers.ps1`と`git diff --check`が成功する。
- branch tipは完成または検証済み補修commitであり、topic branchへpush済みである。

mergeとtagはユーザの明示承認を別々に得る。
