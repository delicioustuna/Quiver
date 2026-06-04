# ARCH-5b 実装プラン — ID 全面 Kind+Gen+Seq 化

> 作成: 2026-06-03 / ブランチ: develop / 前提: ARCH-4 (単一ファイル + 5a テナント化) 完了。
> 着手承認: ユーザ選択 = ① 5b 完了後に 5c 再設計 / ② **Value=packed 物理 ID** / ③ 64bit (Kind4/Gen16/Seq44)。

## 確定した設計

### ID 表現
- `NodeId` / `RelationshipId` / `PropertyId` の `Value` を **packed `Gen16<<44 | Seq44`**（kind ビットは持たない、kind は C# 型で表現）にする。
- 公開アクセサ追加: `Sequence => EntityRef.Sequence(Value)`（下位44bit）/ `Generation => EntityRef.Generation(Value)`（bits44-59）。
- ファクトリ `Create(long sequence, int generation)` 追加。`new XId(seq)` は **gen=0 ⇒ Value==seq** で後方互換（既存構築箇所の大半は無改変）。
- **equality / GetHashCode は Sequence ベース**にオーバライド。adjacency 由来の gen=0 ID と Read 由来の gen≥1 ID が「同一ノード」として一致し、traversal/frontier/dict が壊れない。stale 検出は equality ではなく明示 `TryResolve`(= Read の世代照合) で行う。
- `Invalid = new(-1)` 維持（`Value < 0` は IsValid=false。Sequence/Generation は IsValid 前提）。

### packing 1 本化
- `GenerationalRef`(static, Kind4/Gen16/Seq44) を **`EntityRef`** に改名・吸収。`Pack/Kind/Generation/Sequence/MaxGeneration/SequenceMask` を移植し、kind 消去版ローカル packing `PackLocal(seq,gen)=(gen<<44)|seq` を追加。`Generation/Sequence` は kind ビット有無を問わず下位60bit のみ見るため per-type Value にも cross-kind packed にも使える。
- `EntityId`(内部, FT-11) は `EntityRef` と統合方向（最低限: doc/参照を EntityRef へ寄せる。ToPacked の Kind4/Local60 は EntityRef.Pack と整合）。

### オンディスク
- record の ID フィールドは **Int48(48bit) のまま Sequence を格納**（packed=60bit は入らない）。`WriteInt48(dst, id.Value)` → `id.Sequence`。読み出しは `new XId(ReadInt48(src))`（gen=0）で OK（equality-by-seq）。
- 索引値レーンは従来通り `EntityRef.Pack(Node, seq, currentGen)`（= 旧 GenerationalRef.Pack、バイト同一）。
- sidecar の Generation は ARCH-3(V4) で既存。
- **結論: 5b は on-disk バイト無変更 → FormatVersion bump しない**（§8「format 破壊時のみ進める」。無破壊で bump すると直近の V5 DB を無用に弾く）。これは指示書の「format bump」に対する意図的逸脱としてユーザに報告。

## 実装フェーズ（各フェーズ build + test 緑）

- **Phase A（基盤・挙動不変）**: `EntityRef` 追加（GenerationalRef は一旦併存）。Ids.cs に Sequence/Generation/Create + seq-equality 追加。Allocate/Read は gen=0 のまま ⇒ Value==seq ⇒ 挙動不変。
- **Phase B（slot 演算と Int48 を .Sequence へ）**: NodeStore / PropertyStore / RelationshipStore / EntityVersionStore(localId) / Adjacency(V1/V2) / LabelNodeIndex / GraphSnapshotView / BulkLoader / StreamingBulkLoader / FrontierSet / operators の slot・bitset 演算の `.Value`→`.Sequence`、`WriteInt48(...,.Value)`→`.Sequence`。gen=0 のまま ⇒ 挙動不変。
- **Phase C（generation 注入）**: Allocate/Read/Scan/index resolver が `Create(seq,gen)` を発番。索引 pack を EntityRef へ。Read に世代照合（carriedGen!=0 && != current ⇒ not-found）。gen=1 発番により Phase B の見落としは page 演算爆発で即検出。stale→not-found テスト追加。
- **Phase D（vector + 後片付け）**: vector binding キーに gen（軽量、永続化は ARCH-6）。GenerationalRef 全廃→EntityRef。PublicApi 再承認（Sequence/Generation/Create 追加分）。

## 完了条件（指示書 §3 ARCH-5b）
- `TryResolve(NodeId)` が世代不一致で not-found。外部往復 ID 検証が効く（テスト）。
- ベクトル binding が slot 再利用に安定の素地（ARCH-6 と合流）。
- `GenerationalRef` が物理 ID 統一形 `EntityRef` に集約され重複消滅。
- `dotnet build Quiver.slnx` 0 error、全スイート緑。

## 実装中に確定した設計判断（追補）

1. **`EntityRef` 名前衝突の解消**: 既存の `public record struct EntityRef(EntityKind Kind, long Id)`（VEC-4 ハンドル）に
   packing 静的メソッドを `partial` で相乗り。`Kind` はインスタンスプロパティと衝突するため静的アンパッカーは
   `UnpackKind(long)`。`Sequence`/`Generation` は下位 60bit のみ見るので kind ビット有無を問わず動く。
2. **`Sequence` の sentinel-aware 化**: `Value < 0 ? Value : low44`。Invalid(-1) を Int48 に書くと -1 復元され、
   chain 終端が保たれる。equality/GetHashCode は `Sequence` ベース（reincarnation を等値にしない）。
3. **gen を載せるのは `NodeStore.Allocate` のみ**。Rel/Prop は gen=0 のまま（ARCH-3 の Node 限定スコープ踏襲、
   rel/prop の gen 追跡は将来）。Read が世代照合（carriedGen!=0 && != current ⇒ not-found）。
4. **クエリパイプラインは Sequence 空間 (gen=0) で実行**し hot path コストを回避。利用者に返す境界
   （`QueryRow` マテリアライズ = `Execute` / streaming cursor）で **NodeId 列にだけ現世代を load**
   (`QueryRowMaterializer.StampNodeGenerations`) → `result.Value == CreateNode().Value`（round-trip 一貫）。
   seed の入口（`LiteralProvider.NodeId` / `SingleNodeOperator` / `MultiNodeOperator` / `PairWithConstantOperator`）
   で gen を剥がす。**operator 内部の dedup/距離/frontier マップは一律 `.Sequence` キー**（テストヘルパが packed
   seed を流しても破綻しない防御）。
5. **vector binding キーは slot `Sequence`**（`InMemoryVectorStore` が SetVector/RemoveVector で `EntityRef.Sequence`
   正規化）。利用者は `node.Value`(packed) を渡せるが内部は seq で graph 側 (label index/adjacency/candidate) と整合。
   世代照合による stale binding 検出は **ARCH-6**。
6. **on-disk バイト無変更 ⇒ FormatVersion は bump しない**（V5SingleFile 維持）。record の ID は Int48=Sequence 格納、
   sidecar の Generation は ARCH-3(V4) で既存、索引値レーンの packing もバイト同一。指示書「format bump」に対する
   意図的逸脱（無破壊で bump すると直近 V5 DB を無用に弾くため）。

## 進捗
- Phase A/B/C ✅ 実装完了、`Quiver.Tests` 442/442 緑。stale-resolution テスト追加。
- 既存テスト triage: slot 同一性を `.Value`→`.Sequence` に直す等 ~17 件更新（破壊変更の正当な追従）。
- Phase D: GenerationalRef→EntityRef 統一済 / vector seq 正規化済 / PublicApi 再承認は残（受領後に approved 上書き）。
