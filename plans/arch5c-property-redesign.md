# ARCH-5c 実装プラン — property 格納再設計（フルリデザイン / §7.1 完全準拠）

> 作成: 2026-06-05 / ブランチ: develop / 前提: ARCH-4 (単一ファイル + 5a テナント化) ✅, ARCH-5b (ID Kind+Gen+Seq) ✅ commit bf7edff。
> 着手承認: ユーザ選択 = **フルリデザイン（§7.1 完全準拠）** — node record inline + 索引対象 列指向 + 可変長 slotted + node record 版チェーン化（MVCC 統一レコードモデル）。format V5→V6。
> 指示書: `plans/arch4-to-arch8-rearchitecture-phases.md` §3 ARCH-5c / 設計根拠: `docs/design/11_rearchitecture_master_plan.md` §6.3 §7.1。

## 0. 現状（再設計の出発点）

- **PropertyStore** (`src/Quiver/Stores/PropertyStore.cs`): 41B 固定 record、entity ごと `FirstPropertyId→NextPropId(off35)` 片方向連結リスト。get/set/has/remove は `GraphTransaction` で **O(#props) チェーン走査**。小値は record に inline (≤24B)、超過は BlobStore spillover。`Create` 毎に `FlushMeta`（書き込み増幅）。
- **NodeStore** (15B) / **RelationshipStore**: 固定サイズ record 配列、`Location(seq)=直接ページ算術`。MVCC は **FT-32 で xmin/xmax を sidecar (`EntityVersionStore`) へ外出し**、record には持たない。論理削除 = xmax スタンプ + slot 非再利用。node record の版チェーンは無く、`FirstPropertyId` は in-place 更新（古い snapshot reader は新 head を invisible で skip して旧版へ）。
- 依存: vacuum (OP-3/5) がチェーン走査、SSN (FT-33) が xmin/xmax sidecar 参照、ARIES (FT-19) は page-WAL（slotted ページ化と相性可）、savepoint/lock。

## 1. 確定設計（着手承認で提示する判断）

### D1. 統一バージョン付き slotted ヒープ（固定 record 配列を置換）
- `node` / `rel` を **可変長 record** として slotted ページに格納する `VersionedRecordHeap`（テナント別）。
- ページレイアウト: `[PageHeader | slot directory(→伸長) | … free … | record bytes(←伸長)]`。slot entry = `(offset:u16, length:u16, flags:u16)`。intra-page compaction 対応。
- record version = `[xmin:8 | xmax:8 | nextVersionPtr:8 (pageId<<16|slot) | kind/label/type:2 | inlinePropCount:1 | inlineProp* | overflowPtr (slotted 可変長ページ参照)]`。
- **xmin/xmax を record へ再内包**（= FT-32 sidecar 外出しの逆方向。統一レコードモデル §6.3）。`EntityVersionStore` は廃止 or vector binding 専用に縮退。

### D2. 論理 ID → 物理位置の indirection（ItemPointerMap）
- node Sequence(44bit) は安定論理 ID のまま。**`ItemPointerMap`**（Sequence → head version の物理 (pageId,slot)）を dense 配列で持つ。
- 更新時: 新 version を別 slot に書き、map を新 head へ repoint、旧 head は xmax スタンプ + `nextVersionPtr` で旧版到達可能（snapshot reader が版チェーン walk）。
- ARCH-5b の Generation は map エントリ or record に併載。adjacency/索引が持つ Sequence キーは **map 経由で解決**（adjacency 側のバイトは不変 = Sequence 格納のまま）。
- `Read(seq)` = `map[seq] → (page,slot) → 版チェーン walk で visible 版` = O(1)+版数（通常 1〜2）。

### D3. inline プロパティ符号化
- record version 内に `[keyId:4 | typeTag:1 | payload]` で**小/固定値を inline**。閾値超過の可変長は slotted overflow ページへ退避し overflow id で参照。
- get/has/set/remove は **inline 走査 = O(inline 件数, 小)**。`FirstPropertyId` 連結リストは inline 化対象では撤去。

### D4. 索引対象の列指向（最高リスク・最終フェーズ・descope 可）
- 索引登録済み property key について、値を **列セグメント（keyId 別 Sequence→value）** に保持し projection/filter スキャンの版チェーン走査を回避。
- ⚠️ **columnar × MVCC は本タスク最大の難所**（列ストアは base+delta が定石）。Phase 5 に隔離し、bench で割に合わなければ後続タスクへ descope する判断点を設ける（既存 SeekIndex/RangeIndex/LabelNodeIndex で値→node は既にカバーされるため、純列指向は projection 高速化が主目的）。

### D5. FlushMeta コミット時バッチ化
- header meta（hwm/freelist）+ ItemPointerMap dirty を **commit 時に一括 flush**。op 毎 flush を廃止。

### D6. FormatVersion V5→V6（develop ゆえマイグレーション不要、旧は `FormatVersionMismatchException`）。

## 2. 段階計画（各 Phase で build + test 緑）

- **Phase 1 — slotted ページ基盤**: `SlottedPage` プリミティブ（insert/update/compact/delete）+ `VersionedRecordHeap` 骨格 + `ItemPointerMap`。未配線。slotted ページ単体テスト。
- **Phase 2 — NodeStore を版チェーン slotted ヒープへ**: xmin/xmax in-record + 版チェーン + ItemPointerMap indirection。Read/Allocate/Free/visibility 改修、NodeStore 呼び出し側追従。**properties は当面チェーン据え置き**（record モデル変更を孤立させる）。WAL/ARIES/lock/SSN を新 record で緑化。
  - **ID モデル決定 (docs/design/11 §3 準拠)**: ノードは **monotonic Sequence (slot 非再利用)**。版チェーン + map-null + MVCC visibility が stale 参照を弾くため free list / 世代不一致機構は不要。ただし Kind+Gen+Seq の ID 契約を保つため **generation を record payload に保持** (monotonic では seq 毎に 1 固定、`NodeId.Create`/`CurrentGeneration`/索引値レーン互換を維持)。
  - **進捗 — Phase 2a ✅ (純加算)**: `VersionedNodeStore` (heap+map 上の `INodeStore` 実装) を追加。payload 19B = flags(1)/firstRel(6)/firstProp(6)/label(2)/generation(4)、先頭 15B は旧 NodeStore と同形で `NodeWriteHandle` を再利用 (in-place ポインタ更新)。inUse は open 時に raw scan で再計算 (Phase 6 で永続化)。VersionedRecordHeap に `TryReadVisible(out xmin/xmax)` / `TryReadHeadRaw` / `GetHead` を追加。単体テスト 10 件緑、Stores 78/78 緑。**既存 NodeStore は温存**。
  - **進捗 — Phase 2b ✅**: VersionedNodeStore を sidecar drop-in 化 (2b-1, f466a7b) → factory 配線 + 7 production ファイル retarget + ItemPointerMap に free list (seq 再利用 + 世代 bump) 実装 (2b-2)。NodeStore クラスは store-unit テスト用に温存 (production 非配線)。tenant id 衝突 (NodeMap=11 ↔ AdjacencyContainer=11) を 14 へ修正。全スイート緑。
  - **Phase 2 既知の制約 (Phase 6 へ後ろ倒し)**: heap の **物理ページ回収**が未実装。vacuum は dead version slot を tombstone + seq を free list へ戻す (論理回収 + seq 再利用は動く) が、tombstone ページのコンテナ free list 返却 / 物理 truncate は行わない (bump allocator が _appendPage しか再利用しないため churn でファイルが緩く成長)。OP-5 物理 truncate は props/rels では従来どおり機能。heap free-page 管理は Phase 6 最適化。
- **Phase 3 — property inline 化**: 小/固定値を node record version へ inline、可変長は slotted overflow。`GraphTransaction` の get/has/set/remove を inline 走査へ。PropertyStore は overflow/可変長専用へ縮退。vacuum を版チェーン walk へ。
- **Phase 4 — RelationshipStore 同様化**: rel property inline。
- **Phase 5 — 列指向セグメント**（D4、最高リスク・descope 判断点）: 索引対象 key の列化。optimizer projection/filter 連携。bench gate。
- **Phase 6 — 仕上げ**: FlushMeta commit バッチ化、format V6 bump、recovery/SSN/vacuum/savepoint テスト sweep、PublicApi 再承認、TS-6 sentinel で bench 比較（20% 内）。

## 3. 影響範囲（要追従）

- `EntityVersionStore`/FT-32 sidecar（廃止 or vector 専用へ）, `MvccContext`/`Visibility`/SSN (`SsnContext`)
- vacuum OP-3/5（チェーン走査 → 版チェーン walk）, B+Tree merge OP-6 は無関係
- ARIES/RecoveryManager/Checkpointer（page-WAL なので slotted ページ image で吸収）, savepoint FT-23, lock FT-24/25
- `GraphTransaction`（property API 全面）, BulkLoader/StreamingBulkLoader, CSR/adjacency（Sequence キーは map 経由解決で不変）
- PublicApi baseline（公開面が変わる場合のみ）

## 4. 完了条件（指示書 §3 ARCH-5c）
- property get/set/has が O(1)〜O(small)。書き込み増幅減。`FlushMeta` バッチ化。bench 改善（TS-6 20% 内）。
- 3 サブステップ（5a/5b/5c）全 build/test 緑。`FormatVersion` V5→V6。
- MVCC snapshot isolation / SSN serializable / crash recovery / vacuum が新 record モデルで緑。

## 5. リスクと留意
- **最大難所は Phase 5 列指向 × MVCC**。descope 判断点を明記済み。
- xmin/xmax 再内包は FT-32 の cache-line 最適化を逆転する（record 可変長化で 64B 制約の前提が変わるため正当）。
- 一括テキスト変換は perl/sed（PowerShell 5.1 Set-Content 禁止 = mojibake、ARCH-1 事故）。
- **TS-7 最終確認テストが裏で実行中のため、build/test/デバッグ実行はユーザの再開合図まで保留**。
