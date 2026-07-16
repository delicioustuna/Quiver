# Wave 3 着手指示書: primary entity/property/vector payload stores

> 効力宣言: 本書と設計正本が食い違う場合は設計正本を優先し、食い違いをユーザへ報告する。
> 作成日: 2026-07-16
> 対応する正本のバージョン: 本書と同じ forward-fix commit に含まれる正本
> ステータス: 承認済み(2026-07-16、C-8 forward-fix)

## 1. 着手前チェック

- annotated tag `redesign-wave-2` が存在し、`develop` と `redesign/single-writer` は Wave 2 merge commit `ede8bec370963c98f5d6275e7aeb81c85adb356e` に一致する。
- branch は `redesign/single-writer`、作業場所は専用 worktree `D:/csharp/Quiver-sw` であり、未コミット変更と `develop` 未マージ commit はない。
- 正本 §5.2〜§5.5、§7.1〜§7.3、§9 Wave 3/4、§10.3〜§10.4、§13 と review C-4〜C-6、C-8、M-1、M-6 の境界が一致する。
- Wave 1 の generation-safe typed ID と Wave 2 の `QUIVER-SW` format、適応 file allocation、strict page/WAL codec が実在し、本 Wave の primary store 置換に利用できる。
- review M-1 に従い、store-level clean reopen と stale generation rejection だけを本 Wave で扱う。crash reopen は Wave 5、vacuum による実 slot reuse と relationship sequence の release は Wave 9 へ残す。
- 本書の目標状態と検証計画についてユーザの着手承認を得る。

## 2. 読む順序

1. 正本 §9 Wave 3。
2. 正本 §2.3〜§2.4、§5.2〜§5.5。
3. 正本 §7.1〜§7.2、§8.1〜§8.2。
4. 正本 §10.3〜§10.4、§11.2、§13。
5. review C-4〜C-6、C-8、M-1、M-6。
6. `docs/spec/01_storage_paging.md`、`docs/spec/03_mvcc.md`、`docs/spec/04_records_index.md`、`docs/spec/06_vector.md` と `docs/design/00_conventions.md`。

## 3. 目標状態と検証計画

Wave 3 は entity、property、payload、incidence、adjacency、tenant catalog を別タスクへ分解せず、primary data の保存境界全体を一つの target state へ変更する。

- **あるべき姿**: Vertex、Edge、Nexus の header は identity、`xmin`、`xmax`、property head を保持し、Generation の正本は一箇所だけにある。property は `PropertyAddress(Owner: EntityRef, Key: PropertyKeyId)` で識別され、owner を焼き込んだ `PropertyVersionStore` が Single/Set の version chain を保持する。大きな string/bytes は checksum 付き immutable blob ref、vector は generation と checksum を検証する immutable `VectorPayloadRef` を必ず使う。incidence は非 entity のまま、member vertex の削除と参加 nexus の論理削除に dangling incidence を残さない。adjacency は一つの `AdjacencySegmentStore` format だけを持つ。
- **削除済みであるべき旧構造**: public `PropertyId`、property ID を露出する read handle/enumerator、property entity chain、entity record 内の copy-on-write inline property、`InlinePropertyCodec`、旧 fixed-slot `VertexStore` / `EdgeStore`、adjacency V1 reader/writer/descriptor、旧 vector catalog reader、primary payload と derived HNSW catalog の混在を残さない。互換 shim と旧 format fallback は作らない。
- **一括変更範囲**: Core の property address/ref/value contract、Versioned Vertex/Edge/Nexus store、EntityVersionStore、Property/Blob/VectorPayload store、Incidence と owner head、adjacency segment、tenant catalog、binary/in-memory backend の store wiring、transaction store adapter、logical export、Source Generator/client/RAG/vector adapter の property cursor call site、PublicApi approval、store/transaction/backend/operator tests、active as-built docs。
- **補修方法**: 最初に public/internal property identity と primary record layout を target contract へ一括変更し、旧 API の alias は作らない。続けて payload、incidence、adjacency、tenant catalog と backend wiring を同じ contract に揃える。その後に solution build の compiler error を不足 call site の一覧へ変換し、store focused test、transaction/backend contract test、logical export、全 test、性能比較の順で契約漏れを補修する。
- **契約保証**: `PropertyOwnershipTests` は cross-owner chain、Single/Set、owner delete、same-sequence/different-generation の stale ref rejection を保証する。payload test は inline/blob/vector 境界、dimension/element type/length/checksum/ref generation、orphan scan、到達可能 version の欠落だけを corruption とする境界を保証する。entity/incidence test は全 primary CRUD と member vertex delete の cascade を保証する。clean reopen は正常 shutdown 済み primary page、tenant catalog、checksum、logical export の復元だけを扱う。PublicApi と as-built は ID 非露出 cursor と owner-bound property model に一致させる。

## 4. 本 Wave 固有の落とし穴

- 正本の表に残る Node/Relationship/Hyperedge は設計概念の旧表記であり、実装 identifier と public docs は Wave 2 で確定した Vertex/Edge/Nexus を維持する。
- Wave 4 の `WriterLease`、one-writer snapshot manager、read/write transaction cutoverを前倒ししない。store API は `xmin` / `xmax` と snapshot/self tx の入力を受けられる最終形にするが、既存 facade adapter の全面置換はしない。
- review C-8 に従い、`EntityVersionMeta` の pstamp/sstamp lane と更新 API は既存 SSN のため一時維持する。Generation の正本だけを一箇所へ固定し、metadata の物理縮約は SSN と call site を削除する Wave 4 へ残す。
- Wave 5 の winner redo、checkpoint、crash recovery を前倒ししない。reopen gate は clean shutdown に限定し、process kill や torn WAL からの primary state 復元を本 Wave の成功根拠にしない。
- Wave 9 の vacuum、reader horizon 後の実 slot reuse、relationship free release を前倒ししない。stale ref test は generation の異なる ref/sidecar を直接構成し、別 incarnation へ alias しないことを検証する。
- owner sequence は参照 incidence/edge が不可視かつ horizon 超過になるまで再利用しない。member vertex delete は同じ logical delete 境界で参加 nexus と incidence を無効化する。
- payload orphan scan は回収候補の列挙・検証までを実装し、vacuum coordinator による durable 回収は Wave 9 に残す。snapshot horizon 上で到達不能な version や stale derived entry を primary corruption と誤判定しない。
- `VectorPayloadStore` は primary property value、HNSW/catalog は derived data である。旧 `PersistentVectorStore` の責務を名前だけ変えて温存しない。
- adjacency V1 と V2 の二重 reader を残さない。新 format は Wave 2 の format family だけで読み書きし、旧 page kind/descriptor を黙って受理しない。
- property/version/payload ref の generation、owner、checksum 検証を省略しない。physical sequence だけを logical identity として返さない。
- §10.4 に Wave 3 専用の独立数値 gate はないが、primary CRUD hot path を変更するため `--basic-perf` の comparable workload を baseline 比 1.20x 以内で確認する。

## 5. 完了確認

| gate | 適用/N/A | コマンドまたは差分根拠 | 合格条件 |
|---|---|---|---|
| 機能 test | 適用 | `dotnet build Quiver.slnx -v minimal`、Stores/Storage/Transactions/Backend/PublicApi/Property tests、solution 全 test project、logical export focused test | 0 errors、0 warnings、全対象 test 成功。index 無しで全 primary CRUD と snapshot/self visibility が動き、primary data だけから logical export できる |
| crash test | N/A | Wave 2 tag との差分で commit/checkpoint/recovery/WAL winner 判定を変更しないことを確認する。本 Wave の reopen は正常 shutdown 後だけ | Wave 5 対象の crash recovery を成功根拠に混ぜず、store page checksum と clean reopen test が成功する |
| baseline gate | 適用 | `plans/single-writer-redesign-baseline.md` と同じ `--basic-perf` の comparable CRUD/property/traversal workload。payload 境界は同一セッションで inline/blob/vector を記録する | comparable workload の各値が baseline 比 1.20x 以内。payload 境界の結果と allocation/write amplification の内訳を記録する |
| as-built 更新 | 適用 | `docs/spec/01_storage_paging.md`、`03_mvcc.md`、`04_records_index.md`、`06_vector.md`、`docs/design/development.md` の実装 map と active docs scan | owner-bound property version、primary payload、entity metadata、adjacency format、clean reopen の実装済み contract と一致する |

追加条件は次のとおりである。

- public `PropertyId` と property ID を露出する public handle/enumerator が PublicApi approval から消えている。
- `PropertyAddress`、`PropertyVersionRef`、ID 非露出 cursor が owner-bound property contract を表す。
- entity metadata は Generation の正本を一箇所だけに持ち、property entity identity を持たない。pstamp/sstamp lane は review C-8 の一時境界として既存 SSN 専用に維持され、新 primary store の identity/visibility 根拠には使わない。
- Vertex、Edge、Nexus の Generation の正本が一箇所で、same-sequence/different-generation の入力と payload ref を stale として拒否する。
- Single cardinality の update と Set cardinality の add/remove が property version の追加/終了として表現される。
- cross-owner chain は silent data leak にならず corruption または not-found として拒否される。
- inline/blob/vector の値境界、blob/payload checksum、vector dimensions、element type、byte length が reopen 後も検証される。
- snapshot horizon 上で到達可能な property version が参照する payload の欠落だけを primary corruption とし、orphan と stale derived ref を区別する。
- member vertex の削除は参加 nexus と incidence を同じ logical delete 境界で不可視にし、dangling incidence を返さない。
- adjacency V1、`InlinePropertyCodec`、旧 fixed-slot store、旧 vector catalog reader、property entity chain と互換 shim が active production path に残らない。
- tenant catalog と全 primary store は `QUIVER-SW` family の新 format で clean reopen し、page checksum 不一致を拒否する。
- primary data だけから logical export を再構成でき、scalar/full-text/vector index の存在を必要としない。
- staged path に対する `scripts/agent-guardrails/check-track-markers.ps1` と `git diff --check` が成功する。
- branch tip は完成または検証済み補修 commit であり、topic branch へ push 済みである。

merge と tag はユーザの明示承認を別々に得る。
