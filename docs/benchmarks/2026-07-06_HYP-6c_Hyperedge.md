# HYP-6c 統合性能ゲート: ハイパーエッジを製品 API 経由で再判定

- 日付: 2026-07-06
- 目的: HYP-1d (走査)、HYP-2c (WAL 増幅)、HYP-3c (RAG 表現力) の三仮説を、ストア直呼びの
  ハーネスではなく **製品 API** (`QuiverDatabase` / `IReadTransaction` / `IWriteTransaction` / fluent DSL / Match) を
  通して再測定する。
- ベンチ:
  - [benchmarks/Quiver.Benchmarks/HyperedgeTraversalBenchmarks.cs](../../benchmarks/Quiver.Benchmarks/HyperedgeTraversalBenchmarks.cs)
  - [benchmarks/Quiver.Benchmarks/HyperedgeWriteBenchmarks.cs](../../benchmarks/Quiver.Benchmarks/HyperedgeWriteBenchmarks.cs)
  - [benchmarks/Quiver.Benchmarks/HyperedgeMatchBenchmarks.cs](../../benchmarks/Quiver.Benchmarks/HyperedgeMatchBenchmarks.cs)
- コマンド:
  - `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --hyperedge-traversal`
  - `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --hyperedge-write`
  - `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --hyperedge-match`

## 環境

```
OS      : Windows 11 (10.0.26200.8655)
CPU     : AMD64 Family 25 Model 33 Stepping 2 (AuthenticAMD), 16 logical cores
Runtime : .NET 10.0.9 / SDK 10.0.301, X64 RyuJIT
GC      : workstation (既定)、server GC 無効
計測方式: Stopwatch ベースの standalone runner (BenchmarkDotNet 非使用)、Release 構成
```

計測は各ベンチの `warmup` 反復で JIT と初回 page allocation を除外した後、`iterations` 本の
サンプルの中央値を p50 とした。WAL バイトは自動 checkpoint を止め (`CheckpointThresholdBytes = 0`)、
明示 checkpoint 直後の WAL file length 差分で測る。

## 1. co-membership 走査 vs binary 1-hop (`--hyperedge-traversal`)

`g.Node(hub).Out("Link")` (binary 1-hop) と
`g.Node(hub).Hyperedges("Fact", "subject").OtherMembers("object")` (役割指定 co-membership) を
次数 10 / 100 / 1,000・アリティ 4 で比較する。co-membership 側は 2 経路を測る:

- **View**: `GraphDatabaseOptions.CoMembershipRolePairs` に `(subject, object)` を登録して
  オープンし、planner が `CoMembershipOperator` へ落として物理ビューを走査する経路。
  ビューが実際に選ばれることは `tx.CoMembershipBlocks.Contains(subjectRole, objectRole)`
  (`CoMembershipOperator.Open` と同一条件) をベンチ内で表明して保証する。
- **Chain**: 同じクエリをビュー未登録でオープンし、incidence リンクチェーンへフォールバックした
  対照値。

warmup=300 / iterations=3000。判定は **View p50 ≤ 3× binary**。

| Degree | Binary p50 (µs) | View p50 (µs) | View/Binary | Chain p50 (µs) | Chain/Binary | Binary alloc (B/op) | View alloc (B/op) | Gate |
|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 10   |  19.100 |  22.000 | 1.15x |  68.600 | 3.59x |   3,128 |   4,960 | PASS |
| 100  |  22.100 |  20.900 | 0.95x |  61.800 | 2.80x |  11,016 |  14,980 | PASS |
| 1000 | 162.500 | 348.000 | 2.14x | 743.200 | 4.57x |  82,872 | 108,464 | PASS |

**判定: 合格。** View 経路は全次数で 3x 以内 (1.15x / 0.95x / 2.14x)。Chain フォールバックは
2.80x〜4.57x で、次数 10 と 1,000 は 3x を超えたまま — HYP-6d のメモリ内ビューがこの残差を
除去していること、そしてベンチが実際にビュー経路を通っていることの裏付けになる。
製品 API 経路のため managed allocation は 0 ではない (DSL のプラン構築費用)。これは
HYP-6d のストアレベル計測 (0 B/op) と対照的で、gate は割り当てではなく p50 比にかかる。

## 2. 書き込み費用 (`--hyperedge-write`)

アリティ 2 / 4 / 8 / 16 の作成・プロパティ書き込み・削除の 1 件あたり遅延と、作成の WAL 増幅を測る。
遅延は 2,000 件/tx をコミットした総時間を件数で割った値。WAL は 1,000 件/tx バッチの差分。
判定は **create WAL が binary リレーションシップ作成比 `(1 + arity/2)` 倍以内**。

binary リレーションシップ作成 WAL: 85,893 bytes (85.89 bytes/item)。

| Arity | create (µs/op) | setProperty (µs/op) | delete (µs/op) | create WAL (bytes) | bytes/item | WAL/binary | limit | Gate |
|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 2  |  47.675 | 13.091 | 5.388 | 107,258 | 107.26 | 1.249x | 2.000x | PASS |
| 4  |  68.212 | 14.092 | 4.781 | 162,334 | 162.33 | 1.890x | 3.000x | PASS |
| 8  | 115.092 | 11.205 | 4.238 | 273,346 | 273.35 | 3.182x | 5.000x | PASS |
| 16 | 220.167 | 10.987 | 4.551 | 493,542 | 493.54 | 5.746x | 9.000x | PASS |

**判定: 合格。** WAL 比は全 arity で線形閾値以内 (1.249x / 1.890x / 3.182x / 5.746x)。
WAL バイトは HYP-2d のストア経由計測 (107,258 / 162,334 / 273,346 / 493,543) をほぼ完全に
再現しており、製品 API 経路でも増幅特性が変わらないことを確認した。作成遅延は arity に
比例して増える (member 検証 + incidence 書込み)。プロパティ書込みと削除は arity 非依存
(inline スロット 1 本 / header 論理削除 1 本)。

### 高次数 DeleteNode カスケード

1 ノードが degree 個のアリティ 4 ハイパーエッジで subject を担う状態で `DeleteNode` を実行する。
`DeleteNode` は所属する全ハイパーエッジを同一トランザクションで削除する契約。数値ゲートは
置かず、tx 時間・WAL・削除後の整合性を記録する (HYP-7 の known limits へ反映)。

| Degree | DeleteNode tx (ms) | WAL (bytes) | deadlock | CheckConsistency |
|---:|---:|---:|---|---|
| 1,000  |  7.84 |  61,164 | なし (完走) | consistent, issues=0 |
| 10,000 | 47.69 | 607,938 | なし (完走) | consistent, issues=0 |

削除は degree にほぼ線形 (10x 次数で tx ~6.1x / WAL ~9.9x)。削除後の `CheckConsistency` は
両次数で 0 件 — 論理削除済み header 配下の incidence は正常な vacuum 待ちとして扱われる。
デッドロック検出器の発火なく完走した。RAG では Chunk や頻出エンティティの node が高次数に
なりうるため、この挙動を known limits の運用注意として記録する。

## 3. 星型 Match vs reified graph pattern (`--hyperedge-match`)

四役割の事実を、第一級ハイパーエッジの星型 `Match`
(`GraphPattern.Hyperedge("f","Fact").Member(...)×4`) と、同じ事実を Fact ノード + 4 本の
ロール名リレーションシップで具象化 (reify) した多段結合 (`.Out(...).Select<NodeId>("f")` の繰返し) で
比較する。どちらも 1 事実あたり subject / object / source / asOf の 4 メンバーを 1 行に束ねる。
数値ゲートは無く p50 を記録する。facts=1,000 / warmup=50 / iterations=300。

| Pattern      | rows  | p50 (ms) |
|---|---:|---:|
| star Match   | 1,000 |   1.960 |
| reified join | 1,000 |   2.768 |

比率 (star / reified) = **0.71x**。第一級ハイパーエッジの星型 Match は、reified な 4-way 結合より
速い。星型 Match は最初のメンバーを anchor に node→hyperedge→残りメンバー展開へ落ちるのに対し、
reified 側は Fact ノードから 4 本のリレーションシップを結合し直すため中間行の再アンカーが増える。

## 4. RAG 4 役割シナリオ (クライアント側 materialize 無し)

固定 RAG シナリオ (`subject` / `object` / `source` / `asOf` の 4 役割 Fact) が、途中に
`ToList()` を挟まず一つの operator tree で取得できることは `HyperedgeRagQueryTests` が検証済み。
HYP-6c ではこれをベンチとして作り直さず、テストの合格を確認して参照する。

- `tests/Quiver.Client.Tests/HyperedgeRagQueryTests.cs` — 5 テスト緑
  (subject 起点の object+source、Chunk 起点の subject+object、property 絞込み後の 4 役割、
  同一 role 複数 member、typed `Select` の型不一致拒否)。

**判定: 合格。** クライアント側 materialize 無しで完走する。

## ゲート集約

| 仮説 (再測定元) | ゲート | 実測 | 判定 |
|---|---|---|---|
| HYP-1d 走査 | co-membership view p50 ≤ 3× binary | 1.15x / 0.95x / 2.14x (degree 10/100/1000) | 合格 |
| HYP-2c WAL | create WAL ≤ (1 + arity/2)× binary | 1.249x / 1.890x / 3.182x / 5.746x (arity 2/4/8/16) | 合格 |
| HYP-3c RAG | 4 役割クエリがクライアント側 materialize 無しで完走 | `HyperedgeRagQueryTests` 緑 | 合格 |
| (参考) 高次数 DeleteNode | 数値ゲート無し | 7.84ms/47.69ms、WAL 61,164/607,938B、consistent 0 issues、no deadlock | 記録 |

三ゲート全て合格。HYP-7 の前提を満たす。

## テスト

- `Quiver.Client.Tests` (Hyperedge / Match フィルタ): 49 件緑 (traversal / match / rag / builder)。
- `Quiver.Tests` (Hyperedge / Vacuum フィルタ): 98 件緑 (診断 / 統計 / vacuum / co-membership block)。
- `Quiver.PublicApi.Tests`: 1 件緑 (公開サーフェス無変更、承認ファイル更新不要)。
- `dotnet build Quiver.slnx`: 0 errors。
