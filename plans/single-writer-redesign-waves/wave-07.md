# Wave 7 着手指示書: vector property + vector segments

> 効力宣言: 本書と設計正本が食い違う場合は設計正本を優先し、食い違いをユーザへ報告する。
> 作成日: 2026-07-18
> 対応する正本のバージョン: `ede755edac004606e618bf8d77e3d8f01d7f2eb6`
> ステータス: 承認済み(2026-07-18)

## 1. 着手前チェック

- annotated tag `redesign-wave-6` が存在し、`develop` と `redesign/single-writer` は Wave 6 merge commit `e50e558e1f73950968468e9080ff264424bae2cb` に一致する。
- branch は `redesign/single-writer`、作業場所は専用 worktree `D:/csharp/Quiver-sw` であり、未コミット変更と `develop` 未マージ commit はない。
- 正本 §2.3〜§2.5、§4.5、§5.1、§5.3〜§5.5、§7.1〜§7.2、§7.5、§7.7、§8.1、§8.4、§9 Wave 7、§10.2〜§10.4、§11.2、§13 と review C-5、C-7、M-3、M-5、M-6 の境界が一致する。
- review C-5 は vector candidate と search result を full typed ID へ materializeし、owner Generation、property version visibility、payload ref を primary store で再検証する設計である。
- review C-7 は public `EntityCandidateSet` と filtered vector の direct raw-long contract を本 Wave で削除し、filtered KNN を traversalとplannerの typed candidate surfaceへ集約する設計である。
- review M-3 は immutable HNSW segmentを read snapshotからwriter lease外で構築し、source manifest generationの再検証とpublish commitだけをlease内に置く設計で解決済みである。
- review M-5 は public `IVectorStore`、`InMemoryVectorStore`、`JsonFileVectorCatalog`、旧vector catalog surfaceを本Waveのpublic cutoverで削除する設計である。
- review M-6 はprimary vector payloadと到達可能なproperty versionの整合性を保ち、derived segmentのstale refをcorruptionと誤認しない設計である。
- Wave 3のowner-bound property versionとprimary `VectorPayloadRef`、Wave 4のsnapshot visibilityとidentity materializer、Wave 5のtransaction-owned WAL、Wave 6のpublic read/write transactionとunified `IndexDefinitionCatalog`が実在する。
- 現行の`IVectorStore`、`VectorIndexSpec`、index名にvector値を束縛する`SetVector`、mutable global HNSW、raw-long candidate bridgeは、本Waveでtarget contractへ置換する入力であり、compatibility surfaceとして残さない。
- 本書の目標状態と検証計画についてユーザの着手承認を得てから、`ステータス: 承認済み(2026-07-18)`に更新し、doc-only commitをpushする。

## 2. 読む順序

1. 正本 §9 Wave 7。
2. 正本 §2.3〜§2.5、§4.5、§5.1、§5.3〜§5.5。
3. 正本 §7.1〜§7.2、§7.5、§7.7、§8.1、§8.4。
4. 正本 §10.2〜§10.4、§11.2、§13〜§16。
5. review C-5、C-7、M-3、M-5、M-6。
6. `docs/spec/00_overview.md`、`docs/spec/01_storage_paging.md`、`docs/spec/04_records_index.md`、`docs/spec/05_query.md`、`docs/spec/06_vector.md`、`docs/spec/08_known_limits.md`、`docs/design/development.md`。

## 3. 目標状態と検証計画

Wave 7はvector valueのprimary property contract、vector index definition、transaction-scoped KNN、immutable vector segment lifecycle、EmbeddingとRagの利用経路を単一の利用者contractへ変更する。

- **vector property contract**：`IWriteTransaction.SetVectorProperty(owner, propertyKey, vector)`はimmutable `VectorPayloadRef`を持つowner-bound property versionを作る。
  `IReadTransaction.TryGetVectorProperty(owner, propertyKey, destination)`は同じtransaction snapshotからproperty versionとpayload generation、element type、dimensions、length、checksumを検証して返す。
  vector propertyの保存と取得はvector indexの存在に依存せず、index未作成、drop中、rebuild中でもprimary valueを保持する。
- **unified vector definition contract**：public `VectorIndexDefinition`を`IndexDefinition`の派生型として実装し、`PropertyTarget`、dimensions、element type、metric、HNSW parameters、segment policyを保持する。
  schema mutationは`IWriteTransaction.EditSchema.CreateIndex(IndexDefinition)`とdrop lifecycleに統一し、`VectorIndexSpec`、`CreateVectorIndex`、vector専用catalog APIを削除する。
  `SourcePropertyKeyId`をembedding元propertyから推測せず、各call siteがindex対象のvector property keyを明示する。
- **transaction-scoped search contract**：`IReadTransaction.KnnSearch`と`KnnSearchBatch`はtransaction snapshotからvisibleなvector definitionとmanifestを解決する。
  search resultはfull typed owner ID、score、検証済みproperty addressを表し、cursorはtransactionより長生きできない。
  schema参照はWave 6の`ISchemaCatalog.TryGetIndex`と`ListIndexes`に統合し、databaseまたはbackendからpublic vector storeを取得する経路を削除する。
- **filtered KNN contract**：filtered KNNはtyped owner candidateまたはpredicateを受けるtraversalとplannerのsurfaceに置く。
  public `EntityCandidateSet`とdirect raw-long入力を削除し、logical pipelineはfull `VertexId`、`EdgeId`、`NexusId`を保持する。
  physical lookupへ渡すSequenceはprimary `Read`成功直後の値に限定し、診断用raw longをquery、traversal、transaction入力へ流入させない。
- **derived candidate validation**：各candidateはowner Generation、entity visibility、`PropertyVersionRef` visibility、`VectorPayloadRef` generation、property targetをprimary storeで再検証してからlogical resultへ変換する。
  same-sequence/different-generation、deleted owner、old property version、stale payload、別targetのcandidateはskipする。
  derived indexをprimary valueまたはvisibilityの正本にできない理由をsource commentに残す。
- **segment write contract**：vector property mutationはvisibleな`VectorIndexDefinition`ごとにflat delta segment entryを同じcommit batchへ追加する。
  entryは`PropertyAddress`、`PropertyVersionRef`、`VectorPayloadRef`、owner Generationを参照し、property commitだけが成功してindex entryが欠落する中間contractを作らない。
  index未作成のvector property mutationはprimary payloadとproperty versionだけをcommitする。
- **immutable segment contract**：mutable global HNSWとindexごとのderived payload正本を、snapshot-visibleなflat delta segment、immutable HNSW segment、versioned manifestへ置換する。
  searchはvisibleな全segmentをfan-out検索し、segmentごとのtop-kをmergeした後にprimary validationを行う。
  old readerは旧manifest、新readerはpublish済み新manifestだけを見る。
- **mergeとrebuild contract**：重いHNSW構築はread snapshotからwriter lease外で行う。
  構築完了後に短いwrite transactionを開始し、source definition generationとmanifest generationが一致するときだけ旧manifestの`xmax`と新manifestの`xmin`を同じcommitでpublishする。
  generationが変わっていればartifactを公開せず破棄して、新しいsnapshotから再試行する。
  rebuildはprimary propertyとpayloadをscanするため、derived segmentを削除または破損させてもvector valueを失わない。
- **public surfaceの削除**：`IVectorStore`、`QuiverDatabase.Vectors`、backend SPIのpublic vector store、`AutocommitVectorStore`、public `InMemoryVectorStore`、`JsonFileVectorCatalog`、`VectorIndexSpec`、`VectorIndexCatalog`のpublic contract、EntityKindとindex名を受ける`SetVector`、vector専用`TryGetVector`、public `EntityCandidateSet`を削除する。
  algorithm test用fakeはtests supportに閉じ、製品public APIへ再導入しない。
- **EmbeddingとRagの追従**：`Quiver.Embedding`はvector property write APIと`VectorIndexDefinition`を使う。
  embedding元property、provider、normalization profile、task状態はembedding task metadataに保持し、vector index definitionの`SourcePropertyKeyId`から推測しない。
  `Quiver.Rag`はread transactionのKNNと同じsnapshotのgraph accessを使い、旧`db.Vectors`またはraw candidate APIを参照しない。
  score内訳とtop-k前candidate push-downの完成はWave 9へ残す。
- **補修方法**：最初にpublic transaction、schema、backend SPI、vector property mutation、search result、segment manifest、Embedding、Ragをtarget contractへ一括変更する。
  続けてsolution buildのcompiler errorを旧public call siteとmutable store dependencyの一覧として補修する。
  build成功後はPublicApi、primary payload、transaction atomicity、schema lifecycle、segment snapshot、operators、Embedding、Rag、crash、recall、publish stallの順で契約漏れを補修する。
- **契約保証**：index未作成round-trip、index dropとrebuild中のproperty保持、commit atomicity、old/new snapshot、update/delete、same-sequence/different-generation、segment merge中のwriter wait、source generation change retry、rebuild、recall@10、hybrid candidate validationを回帰testへ追加または更新する。
  as-builtはvector property、unified definition、transaction-scoped KNN、immutable segment、candidate validationを実装済みcontractとして更新する。

## 4. 本Wave固有の落とし穴

- vector propertyの書き込みにindex名を要求しない。
  indexはpropertyを観測するderived dataであり、値のownerではない。
- vector payloadをproperty recordへinlineしない。
  generation付きimmutable payload refを常に使うことで、payload reuse後のstale refを拒否する。
- `VectorIndexDefinition`を旧`VectorIndexSpec`の名前変更だけで済ませない。
  targetは`PropertyTarget`で表し、definitionはWave 6のtransactional schema catalogとlifecycle stateに参加させる。
- `PersistentVectorStore`のindex別payload copyをprimary valueとして残さない。
  primary readとrebuildのsourceはproperty versionが参照する`VectorPayloadStore`に限定する。
- mutable HNSWへcommitごとに直接insertしない。
  old readerのsnapshotを壊さないため、commit-local deltaとimmutable segmentをversioned manifestで公開する。
- HNSW構築中にwriter leaseを保持しない。
  lease内に置くのはsource generation再検証とmanifest publish commitだけであり、publish p99は`WriterWaitTimeout`既定値の10%以内に収める。
- stale artifactを無条件にpublishしない。
  source definitionまたはmanifest generationが変わった場合はartifactを破棄し、新しいread snapshotから再構築する。
- segment top-kを結合した結果をそのまま返さない。
  owner、property version、payload refをprimary snapshotで再検証しなければ、update、delete、Generation変更後のcandidateが漏れる。
- filtered KNNのraw-long bridgeをinternal aliasとして残さない。
  `EntityCandidateSet`とdirect raw-long contractは本Waveの削除対象であり、typed candidateからSequenceを取り出せるのはprimary `Read`成功直後だけである。
- `InMemoryVectorStore`をproduction public surfaceに残さない。
  binaryとin-memory backendは同じtransactionとsnapshot contractを満たし、algorithm単体testのfakeはtests supportへ隔離する。
- Embeddingのsource text propertyとvector target propertyを同一視しない。
  自動変換するとrepositoryごとのvector property schemaを復元できないため、call siteとtask metadataで別々に保持する。
- index drop、rebuild、derived corruptionをprimary payloadのdeleteまたはcorruptionとして扱わない。
  horizon上で到達可能なproperty versionがpayloadを欠く場合だけprimary corruptionである。
- full-text segment、Wave 9のcandidate push-downとRAG score内訳、segment GCとvacuum、relationship reuse coordinatorを前倒ししない。
  該当contract変更が必要ならprocess §5へ戻る。

## 5. 完了確認

| gate | 適用/N/A | コマンドまたは差分根拠 | 合格条件 |
|---|---|---|---|
| 機能test | 適用 | `dotnet build Quiver.slnx -v minimal`、`dotnet test tests/Quiver.PublicApi.Tests/Quiver.PublicApi.Tests.csproj --no-build`、`dotnet test tests/Quiver.Backend.Tests/Quiver.Backend.Tests.csproj --no-build --filter "Category!=Chaos"`、`dotnet test tests/Quiver.Stores.Tests/Quiver.Stores.Tests.csproj --no-build`、`dotnet test tests/Quiver.Index.Tests/Quiver.Index.Tests.csproj --no-build`、`dotnet test tests/Quiver.Operators.Tests/Quiver.Operators.Tests.csproj --no-build`、`dotnet test tests/Quiver.Tests/Quiver.Tests.csproj --no-build`、`dotnet test tests/Quiver.Rag.Tests/Quiver.Rag.Tests.csproj --no-build`、solution全test project | 0 errors、0 warnings、全対象test成功。index非依存のvector property、transaction-scoped KNN、immutable segment snapshot、candidate validation、EmbeddingとRagの新API追従がbinaryとin-memory両backendで成立する |
| crashtest | 適用 | binary backendでpayload page、property ref、flat delta、manifest publish、rebuild publishのcommit境界kill matrixを実行し、`dotnet test tests/Quiver.Backend.Tests/Quiver.Backend.Tests.csproj --no-build --filter "Category=Chaos"`とvector consistency testを実行する | Commitなしpropertyとmanifestは不可視で、Commit済みpropertyとpayloadはreopen後も残る。derived segmentの欠落または破損はprimary openを壊さずrebuildへ収束し、到達可能なpropertyが欠損payloadを指さない |
| baseline gate | 適用 | `dotnet run -c Release --project benchmarks/Quiver.Benchmarks.RecallCheck`、`dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --clean-slate-segment-spike`、product vector segmentのmerge前後とpublish stallを測るrunnerを実行する | recall@10が0.95以上、merge前後の結果集合が一致する。lease保持中publish p99は`WriterWaitTimeout`既定値の10%以内であり、重いartifact構築時間を含めない。環境、commit、生出力を`docs/benchmarks/`へ保存する |
| as-built更新 | 適用 | `docs/spec/00_overview.md`、`docs/spec/01_storage_paging.md`、`docs/spec/04_records_index.md`、`docs/spec/05_query.md`、`docs/spec/06_vector.md`、`docs/spec/08_known_limits.md`、`docs/design/development.md`、README、samplesのactive contract scan | 旧vector store、index名binding、mutable global HNSW、raw-long candidate、旧catalog記述が残らず、vector property、unified definition、transaction-scoped KNN、immutable segment、manifest publishと一致する |

追加条件は次のとおりである。

- `VectorIndexDefinition`、transactionのvector property API、KNN API、search resultのpublic surfaceに日本語XML docがあり、PublicApi approvalが最終contractだけを含む。
- production source、tests、benchmarks、samples、Embedding、Rag、active docsにpublic `IVectorStore`、`QuiverDatabase.Vectors`、`VectorIndexSpec`、`CreateVectorIndex`、EntityKindとindex名を受ける`SetVector`、public `InMemoryVectorStore`、`JsonFileVectorCatalog`、public `EntityCandidateSet`の意図しない参照が0件である。
- indexを一度も作成しないvector propertyがcommit、abort、reopenを跨いで通常propertyと同じ規則で読み書きできる。
- vector indexをdropまたはrebuildしてもprimary propertyとpayloadが保持され、rebuild後のKNNが同じvisible owner集合を返す。
- vector property、payload ref、flat delta entry、manifest updateが同じcommit boundaryに入り、各crash境界でwinnerとloserがstrict Commit規則へ収束する。
- old readerはupdate、delete、segment publish前のmanifestとproperty versionを読み続け、新readerはpublish後のmanifestとcurrent property versionだけを見る。
- same-sequence/different-generation、deleted owner、old property version、stale payload ref、別property targetのcandidateがlogical outputへ出ない。
- logical KNN resultはfull typed owner IDを返し、raw Sequenceまたはdiagnostic raw longがquery、traversal、transaction入力へ流入しない。
- flat deltaとimmutable HNSW segmentのfan-out、top-k merge、primary validationが同じread transactionのsnapshotを使う。
- HNSW artifact構築中に通常writerが進行し、lease保持中publish p99が性能gateを満たす。
- source definitionまたはmanifest generationが構築中に変化した場合、stale artifactはpublishされず、新しいsnapshotから再試行される。
- segmentの削除またはchecksum不整合がprimary vector propertyを失わせず、rebuild後にsearch pathへ戻る。
- RecallCheckの既定scenarioとdelete後scenarioがrecall@10 0.95以上であり、segment merge前後のresult setが一致する。
- filtered KNNとhybrid operatorはtyped candidateを使い、owner Generation、property visibility、payload refをprimaryで再検証する。
- Embeddingはsource propertyとvector target propertyをtask metadataで区別し、index名をvector valueの保存先として使わない。
- Ragはread transactionのKNNとgraph readを同じsnapshotで実行し、旧vector storeまたはraw candidate APIを参照しない。
- staged pathに対する`scripts/agent-guardrails/check-track-markers.ps1`と`git diff --check`が成功する。
- branch tipは完成または検証済み補修commitであり、topic branchへpush済みである。

mergeとtagはユーザの明示承認を別々に得る。
