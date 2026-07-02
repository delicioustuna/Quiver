# ゼロ依存化 (ZD) + 性能フロンティア (VP / CR / DU) — 実装計画書 (rev2)

> 起票日: 2026-07-02 (rev1 はリモートブランチ `claude/library-performance-optimization-tnuoby` 上の同名ファイル)。
> rev2: 2026-07-02。外部レビュー (GPT) の指摘を受けた改訂版。起点 develop = commit `879b8f2` (rev1 と同一)。
> 前提方針 (ユーザ決定): **コア `Quiver` パッケージは NuGet 依存ゼロ (PackageReference ゼロ) とする。**
> サブパッケージ (`Quiver.Hosting` / `Quiver.OpenTelemetry` / `Quiver.Rag` / `Quiver.Embedding`) は従来どおり依存を持ってよい。
>
> [library-refinement-tracks.md](library-refinement-tracks.md) (REF) と
> [perf-improvements-query-read-write.md](perf-improvements-query-read-write.md) を補完する。
> REF の G-1〜G-8 ガードレールを全タスクで継承する (G-2 の扱いは VP-3 参照)。

---

## 0. レビュー回答 — 指摘の検証結果と rev2 での対応

レビューの全指摘をソースで裏取りした。事実関係の指摘は**全点正しい**。各項の判断と rev2 への反映は以下のとおり。

| 項目 | 指摘の検証 | rev2 での対応 |
|---|---|---|
| ZD-1 CRC | **正** — ページ CRC は単一 CRC ではなく `Crc32(ヘッダ[checksum=0]) XOR Crc32(本体)` ([PageHeader.cs:60,77](../src/Quiver/Storage/PageHeader.cs))。WAL は incremental Append ([WriteAheadLog.cs:380-383](../src/Quiver/Wal/WriteAheadLog.cs))。旧 DB フィクスチャは**存在しない** | 呼び出し構造 (XOR 構成 / incremental) をビット単位で保存する要件を明記。golden フィクスチャは**置換前に現行実装で生成**する手順を追加 |
| ZD-2 Logging | **正** — [QuiverTelemetry.cs](../src/Quiver/Core/Telemetry/QuiverTelemetry.cs) は ActivitySource ×4 + Meter を既に公開しており、rev1 の「Meter + EventSource のみ」は ActivitySource を誤って落としていた。置換前の `QuiverLog` が process-wide static で、最後に Open した DB の factory が全 DB に適用されることも確認 | 計装方針を「**ActivitySource + Meter + EventSource** (すべて in-box)」に訂正。Hosting の ILogger ブリッジを**任意ではなく必須成果物**に昇格。EventSource は process-wide であり DB 別ルーティングは行わないことも明記 |
| ZD-3 ゲート | **正** | csproj 検査に加え、`dotnet pack` の生成 nuspec の dependency group 空検査を追加 |
| VP-1 Scorer | **正** — 既に `Vector<float>` SIMD 済み。追加 1.3× は楽観的、Cosine の 4-way 展開はアキュムレータ 3 系統 × 4 = 12 本でレジスタ圧迫懸念 | 優先度を**最後**に降格。Cosine は 2-way から試す。kill criteria は維持 (未達なら現状維持) |
| VP-2 efSearch | **正** | `VectorSearchOptions` 型として single / batch / filtered の全経路 + 全 backend に一貫して通す設計に改訂 |
| VP-3 HNSW 契約 | **正** — [VectorIndexCatalog.cs:93-115](../src/Quiver/Stores/VectorIndexCatalog.cs) の Load は長さ情報のない packed entry 列を順次読みするため、entry へのフィールド追加は後続 entry の解釈を壊す。rev1 (a) の「後方互換に拡張」は現形式では**不成立** | **全面再設計** (§VP-3)。プロジェクト方針「未リリース = クリーンブレイク可」を根拠に、凍結前の明示的 catalog 再エンコード (a') を推奨案とし、(b) M=16 固定明文化をフォールバックに。efConstruction はレイアウト非決定のため M/Mmax0/MaxLayers と分離 |
| VP-4 payload cache | **正** — [HnswSearchBenchmarks.cs](../benchmarks/Quiver.Benchmarks/HnswSearchBenchmarks.cs) の `FlatScan` は `KnnSearchBatch` 経由で実は **HNSW を呼んでいる** ([PersistentVectorStore.cs:238-247](../src/Quiver/Stores/PersistentVectorStore.cs))。現ベンチは HNSW 対 HNSW。pin 問題自体は実在 ([HnswIndex.cs:453-459](../src/Quiver/Stores/HnswIndex.cs)) | **VP-4.0 (ベンチ修正) を前提タスクとして新設**。キャッシュは単一 `float[]` をやめ、分割 slab + DB 全体メモリ予算に改訂 |
| VP-5 recall gate | **正** — 既存は VectorHnswTests の N=1000 / dim=32 / 閾値 0.85 self-query で、true recall@k ではない | brute-force ground truth に対する true recall@10 (N=10k / dim=384 / ≥0.95) に強化。削除後ケースも ground truth 再計算方式に |
| CR-1 並行ベンチ | **正** — 既存ベンチに 1/2/4/8 thread の KNN / BM25 スケーリングはない | 維持 (最優先の実測タスク) |
| CR-2 PagedFile | **正** — resident hit も unpin も全て `_poolLock` を通る ([PagedFile.cs:392-429](../src/Quiver/Storage/PagedFile.cs), [223-236](../src/Quiver/Storage/PagedFile.cs))。単純 ConcurrentDictionary 化は lookup→pin 間に eviction race がある | 案 1 を「**frame generation + optimistic pin 再検証**」プロトコルに差し替え。ConcurrentDictionary 単独案は棄却 |
| CR-3 VectorStore | **正** — `_gate` は monitor lock で KNN 全体を直列化 ([PersistentVectorStore.cs:169,238](../src/Quiver/Stores/PersistentVectorStore.cs))。batch はロック保持のまま逐次 | global RWLock 案を撤回し、**catalog ロック + index 単位 ReaderWriterLockSlim** の 2 段構成に改訂 |
| DU-1 durability | **正** — additive API なので凍結条件にする根拠が弱い | **REF-7 前提条件から除外**し、形状案を「草案」として記録するのみ。実装は別トラック |

推奨実行順序もレビュー提案どおりに改訂した (§7)。

---

## 1. 現状の依存クロージャ (実測: nuspec 確認済み 2026-07-02)

| パッケージ | 推移的依存 | core での使用箇所 |
|---|---|---|
| `System.IO.Hashing` 8.0.0 | なし (net8.0+ はリーフ) | ページチェックサム ([PageHeader.cs:60,77](../src/Quiver/Storage/PageHeader.cs))、WAL レコード CRC ([WriteAheadLog.cs:380](../src/Quiver/Wal/WriteAheadLog.cs) / [WalReader.cs:76](../src/Quiver/Wal/WalReader.cs))。**いずれも on-disk フォーマットの一部** |
| `Microsoft.Extensions.Logging.Abstractions` 10.0.7 | **`Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.7 を連れてくる** | `QuiverLog` ファサード + 呼び出し 5 ファイル。**`GraphDatabaseOptions.LoggerFactory` として公開 API に露出** |

ゼロ依存には ZD-1 / ZD-2 の両方が必要。

## 2. Track ZD — コアのゼロ依存化

### ZD-1: System.IO.Hashing の除去 (CRC-32 自前実装)

- **目的**: core から `System.IO.Hashing` を外す。チェックサムは on-disk フォーマットの一部なので、**ビット同一の再実装**のみが許される。
- **[rev2] チェックサムの実構造 (置換で保存すべき契約)**:
  - ページ: `storedCrc = Crc32.HashToUInt32(header32B[checksum=0]) XOR Crc32.HashToUInt32(body)` — **2 つの独立 one-shot CRC の XOR** であり、ページ全体を 1 本の CRC (連結 or incremental) で計算した値とは一致しない。呼び出し構造をそのまま保存する。
  - WAL レコード: `new Crc32(); Append(hdr[..21]); Append(payload); GetCurrentHashAsUInt32()` — **incremental 構成**。こちらは連結 1 本の CRC と数学的に等価だが、API 形状として incremental を提供する。
  - 自前 `Crc32` は one-shot 静的 (`HashToUInt32`) と incremental (`Append`/`GetCurrentHashAsUInt32`) の両形を持ち、置換は**両呼び出し箇所で機械的な型差し替えのみ** (計算構造の変更禁止)。
- **実装手順**:
  1. CRC-32 (IEEE 802.3、反転多項式 `0xEDB88320`) を slicing-by-16 テーブル方式で internal 実装 (`src/Quiver/Core/Crc32.cs`)。
  2. **[rev2] golden フィクスチャの事前生成**: 旧 DB フィクスチャは現存しないため、**置換前に**現行実装 (System.IO.Hashing) で (i) 小規模 DB ファイル + WAL のバイナリフィクスチャ、(ii) 代表ページ/WAL レコードの生バイトと期待 CRC 値の組を生成し、テストリソースとしてコミットする。置換後の実装がこのフィクスチャを開けて checksum 検証をパスすることがビット同一性の実地証明になる。
  3. オラクルテスト: `System.IO.Hashing` は**テストプロジェクトにのみ**残し、FsCheck ランダムバッファで `自前 == 参照実装` を one-shot / incremental の両経路で固定。既知ベクトル ("123456789" → `0xCBF43926` 等) の golden テストも置く。
  4. ARM64 は `System.Runtime.Intrinsics.Arm.Crc32` (in-box、IEEE 互換) の加速パスを任意で追加。**x86 SSE4.2 `crc32` 命令は CRC-32C (Castagnoli) でありビット同一にならないため使用禁止。**
  5. [WriteAheadLog.cs:24](../src/Quiver/Wal/WriteAheadLog.cs) のコメント「Crc32C(4)」は実装 (IEEE) と不一致の誤記なので「Crc32(4)」へ修正。
  6. `--basic-perf` の write / recovery 経路 before/after (checksum 検証は load 時のみなので読取ホットパス影響なしの見込み、実測で確認)。
- **判断ポイント**: ビット同一が絶対条件。フィクスチャが開けなければ中断・報告。「新フォーマットで checksum 変更」へ逃げない。
- **完了条件**: core の PackageReference から `System.IO.Hashing` が消え、golden フィクスチャ/オラクルテスト緑、perf 退行なし (±2%)。
- **工数**: 小 (0.5〜1 日。フィクスチャ生成 +0.5 日)。

### ZD-2: Microsoft.Extensions.Logging.Abstractions の除去

- **目的**: core から M.E.L.Abstractions (+ 推移的 DI.Abstractions) を外す。`GraphDatabaseOptions.LoggerFactory` が公開契約に漏れており、除去は破壊的変更 = 0.x の今しか無料でできない (**REF-7 前提条件**)。
- **[rev2] 計装方針の訂正**: core の計装は「**ActivitySource + Meter + EventSource** (すべて in-box、PackageReference 不要)」とする。rev1 の「Meter + EventSource のみ」は誤り — `QuiverTelemetry` は既に ActivitySource ×4 (Transaction/Query/Checkpoint/WalFlush) + Meter を公開しており、これらは `Quiver.OpenTelemetry` の接続点として維持する。ZD-2 で除去するのは **ILogger 系統のみ**。REF-14 の改訂文言もこれに合わせる。
- **[rev2] 副次的な設計欠陥の解消**: 置換前の `QuiverLog` は process-wide static で、マルチ DB 構成では最後に Open した DB の factory が全 DB のログに適用される。ILogger 除去はこの歪みも同時に解消する (EventSource/Meter/ActivitySource はもともとプロセス集約が標準セマンティクス)。
- **実装手順**:
  1. `QuiverLog` の LoggerMessage イベント 6 種 (TxCommitted / TxAborted / TxCommitFailed / CheckpointCompleted / WalFlushed / QueryExecuted) と tx/query scope を、既存 `QuiverEventSource` のイベントへ移設。イベント ID・レベル・構造化キー (`quiver.tx.id` 等) を文書に固定。
  2. `GraphDatabaseOptions.LoggerFactory` を公開面から除去 (approved.txt 差分レビュー、G-3)。
  3. **[rev2] ILogger ブリッジは必須成果物**: `Quiver.Hosting` (もともと M.E.* 依存の世界) に EventSource → ILogger の EventListener ベースブリッジを実装する。Hosting 経由の利用者は従来どおり `ILoggerFactory` でログを受け取れる (互換機能であり任意ではない)。従来の「最後に Open した factory が勝つ」状態は解消するが、EventSource 自体は process-wide なので、同一プロセスに複数ホスト listener があれば各 listener が全 Quiver イベントを受け取る。DB 別ルーティングは契約にしない。加えて docs/operations に「dotnet-counters / dotnet-trace / OTel での観測手順」を整備。
  4. REF-14 の計装方針を「ActivitySource + Meter + EventSource (すべて in-box)」へ改訂。REF-3 インベントリで `LoggerFactory` は DEMOTE 扱い。
  5. 全テスト緑 + `--basic-perf` で計装経路の退行なし (NullLogger 経路 → EventSource disabled 経路の等価性 = 分岐 1 回)。
- **判断ポイント**: core に独自ロギング抽象 (IQuiverLogger 等) を新設しない。Hosting 未使用ユーザーが直接 `ILoggerFactory` を挿せなくなるトレードオフは README / docs/operations に明記 (EventSource/EventCounters が組み込み DB の標準観測面)。
- **完了条件**: core の PackageReference から M.E.L.Abstractions が消え、approved.txt から `LoggerFactory` が消え、**Hosting ブリッジが動作するテスト付きで存在し**、観測手順が文書化され、全テスト緑。
- **依存関係**: **REF-7 より先に必須**。
- **工数**: 中 (2〜3 日。ブリッジ必須化で rev1 比 +1 日を固定計上)。

### ZD-3: ゼロ依存の宣言と回帰ゲート

- **実装手順**:
  1. CI ゲート (2 段): (i) core csproj に `PackageReference` が存在しないことの検査、(ii) **[rev2]** `dotnet pack` が生成する nuspec の dependency group が**全 TFM で空**であることの検査。csproj 検査だけでは SDK / props 経由で注入される依存や ProjectReference 由来の依存を見逃すため、最終成果物 (nuspec) を正とする。
  2. `Quiver.csproj` の `Description` と README 特徴欄を「依存パッケージゼロ・純 C#・アンマネージド依存なし・NativeAOT 対応」へ更新。
- **完了条件**: 両ゲート緑。README/nuspec 記述更新。
- **依存関係**: ZD-1, ZD-2。工数: 小 (0.5 日)。

---

## 3. Track VP — ベクトル検索経路

### VP-4.0: HNSW ベンチマーク baseline の修正 (新設、VP 系実測の前提)

- **[rev2 新設]** 現行 [HnswSearchBenchmarks.cs](../benchmarks/Quiver.Benchmarks/HnswSearchBenchmarks.cs) の `FlatScan` は「full-corpus scan」と説明しているが、`KnnSearchBatch` は [PersistentVectorStore.cs:242](../src/Quiver/Stores/PersistentVectorStore.cs) で `h.Hnsw!.Search(...)` を呼ぶため、**実測は HNSW 対 HNSW** になっている。このままでは VP-2/VP-4 の before/after も recall 比較も分母が壊れる。
- **実装手順**:
  1. 真の flat scan baseline を追加する。`VectorPayloadStore` の全件走査 + `VectorKnnHeap` を使う内部ベンチ用ヘルパを設け、「HNSW を経由しない exact top-k」であることをコードで保証する。`KnnSearchFiltered` に全 seq を渡す方法は候補数が閾値を超えると HNSW 分岐へ戻るため使用しない。
  2. `KnnSearchBatch` を騙って flat と呼んでいる XML doc / ベンチの説明文を修正する。
  3. dim を 128 固定から {384, 768} パラメタ化し、VP-4a spike と VP-5 recall gate が同じコーパス定義を共有できるようにする。
- **依存関係**: なし。**VP-2 / VP-4 / VP-5 より先に実施**。工数: 小 (0.5 日)。

### VP-1: VectorScorer のマルチアキュムレータ展開 — 優先度: 最後

- **[rev2] 降格の理由**: 現行 [VectorScorer.cs](../src/Quiver/Core/VectorScorer.cs) は既に `Vector<float>` SIMD でスカラ比 3.5〜10× を達成済み。追加 1.3× は楽観的であり、特に Cosine は dot / normA / normB の 3 アキュムレータ系統を持つため 4-way 展開で 12 本のベクトルレジスタを要し、AVX2 (16 本) ではレジスタ圧迫・スピルの懸念がある。
- **実装手順**: Dot/Euclidean は 4-way、**Cosine は 2-way から**試す。端数は既存スカラループ。`ScalarVectorScorer` パリティテスト (加算順序が変わるため ULP/相対誤差で固定) と次元 {64, 384, 768, 1536} の micro-bench。
- **Kill criteria**: 次元 384+ の Dot/Cosine で ≥1.3×。未達なら展開数を振って再測、それでも未達なら**現状維持で報告** (このタスクは「実験」であり成果を前提にしない)。
- **判断ポイント**: HNSW/flat の順位が既知コーパスで不変であること (タイブレークは seq 順で吸収)。
- **依存関係**: なし。工数: 小 (0.5 日)。

### VP-2: VectorSearchOptions の導入 (efSearch の公開)

- **目的**: [HnswIndex.cs:271](../src/Quiver/Stores/HnswIndex.cs) 相当の検索ビーム幅が `ef = Max(EfConstruction=200, k)` 固定で、k=10 の典型 RAG クエリでも ef=200 を払っている。efSearch はディスクレイアウトと無関係な純実行時パラメタ。
- **[rev2] API 形状**: 個別 overload の追加ではなく **`VectorSearchOptions` 型を新設**し、`KnnSearch` / `KnnSearchFiltered` / `KnnSearchBatch` の**全経路**と全 backend (persistent / in-memory reference) に一貫して通す。
  - `EfSearch` (既定 = 現行互換 200)、`FilteredOversampleFactor` (現行 k*8 の係数) を収容。
  - 将来の VP-4 (キャッシュ有無) や DU 系オプションはここに足さない — 検索セマンティクスに影響するノブのみ。
- **実装手順**: オプション型 + 全経路の配線 + recall/latency トレードオフ表 (ef ∈ {32, 64, 100, 200}) を実測して docs/operations のチューニングガイドへ。
- **判断ポイント**: **既定値は変えない** (現行 200 — 既存ユーザの recall を黙って下げない)。public API 追加のため approved.txt レビュー (G-3)。
- **完了条件**: 全経路でオプションが効くテスト + 実測表。全テスト緑。
- **依存関係**: VP-5 が先にあると安全。VP-4.0 のベンチ修正が先。工数: 小〜中 (1〜1.5 日)。

### VP-3: HNSW 構築パラメタの契約決定 (凍結前必須) — 全面再設計

- **事実関係 (rev2 で確定)**:
  - `HnswIndex` の `M=16` / `Mmax0=32` / `MaxLayers=8` は **RecordSize (1164B) を決定し on-disk レイアウトに焼き込まれている** ([HnswIndex.cs:26-36](../src/Quiver/Stores/HnswIndex.cs))。
  - **efConstruction (=200) はレコードサイズを決めない** — 構築品質のみに効く純ビルド時パラメタであり、M/Mmax0/MaxLayers とは契約強度が異なる。rev1 はこれを混同していた。
  - [VectorIndexCatalog.cs](../src/Quiver/Stores/VectorIndexCatalog.cs) の entry は**長さ情報のない packed 可変長列**で、Load は先頭から順次デコードする。entry へのフィールド追加は後続 entry の解釈をずらすため、**rev1 (a) の「catalog エントリの追加フィールドで後方互換に拡張」は現形式では成立しない**。
- **選択肢 (rev2 改訂)**:
  - **(a') 凍結前クリーンブレイクで自己記述化 (推奨)**: catalog の entry エンコードを「per-entry 長さプレフィクス付き + M / Mmax0 / MaxLayers を明示フィールドとして永続化」へ**明示的に再エンコード**する。プロジェクト方針として Quiver は未リリースであり、フォーマット変更はマイグレーション・旧互換なしのクリーンブレイクで行える (docs/spec/08_known_limits.md #no-migration の凍結方針が効くのは 1.0 **以後**)。長さプレフィクスにより 1.x 内の将来フィールド追加が真に後方互換になる (旧リーダは未知末尾を読み飛ばせる) ので、このタイミングの 1 回だけフォーマット変更予算を払う価値がある。HnswIndex はレイアウト定数を catalog 由来の per-index 値に置き換え、RecordSize を導出する。efConstruction は永続必須ではなく `VectorIndexSpec` のビルド時オプション (情報として catalog に書いてもよいがレイアウト契約ではない) に分離する。
  - **(b) v1 は M=16 固定の明文化 (フォールバック)**: 「v1 では M/Mmax0/MaxLayers 固定。変更は 2.0 のフォーマット改訂で行う」を docs/spec/08_known_limits.md に判断として記録する。efSearch (VP-2) が実行時に調整可能なので、recall ノブが全て失われるわけではない。
- **G-2 との整理**: G-2 (FormatVersion bump 禁止) の趣旨は「リファクタタスクに紛れた暗黙のフォーマット変更の禁止」であり、(a') は**独立した明示的フォーマット変更タスク**として切り出す。凍結前 (0.x) のクリーンブレイクはプロジェクト既定方針と整合する。エンコードを変更する以上、format byte の据え置きは禁止し、`FormatVersion` を明示的に V2 へ bump して旧 V1 DB を明確な `FormatVersionMismatchException` で拒否する。
- **判断ポイント**: (a') か (b) かは**ユーザ判断事項** (フォーマット変更予算 2 日を今払うか、M 固定を 2.0 まで受け入れるか)。推奨は (a') — 凍結後は選択肢が消えるため。どちらに転んでも「**決定が記録されている**」ことが完了条件。
- **完了条件**: (a') 実装 + round-trip テスト + 旧エンコード DB が明確なエラーで拒否されること、または (b) spec への判断記録。REF-7 チェックリストに反映。
- **依存関係**: **REF-7 より先に必須**。工数: (b) 0.5 日 / (a') 2 日。

### VP-4: HNSW 検索経路の pin 除去 (payload の in-memory キャッシュ) — spike 先行

- **目的**: HNSW 検索は距離計算 1 回ごとに `_payload.TryGet(seq, dest)` = ページ pin + ベクトル全体コピーを行う ([HnswIndex.cs:453-459](../src/Quiver/Stores/HnswIndex.cs))。ef=200 のビーム探索では 1 クエリ数百〜数千 pin となり、その全てが PagedFile のグローバルロック (CR-2) を通る。Task A/B で「per-row / per-edge の pin が真因」だったパターンの再現が濃厚。
- **Spike VP-4a (配分特定)**: **VP-4.0 で修正した真の baseline の上で**、HNSW 検索 micro-bench (dim 384/768、N 10k/100k、k 10) の内訳 (payload TryGet / PriorityQueue / visited set) をプロファイル確認する。rev1 の「Task B 級 10× の ROI」はベンチ修正前の数字を含むため、spike で言い直す。
- **改善案 (spike 結果で採否)**:
  1. **[rev2] 分割 slab キャッシュ + DB 全体メモリ予算**: 単一の巨大 `float[]` は採らない — dim 768 × 数百万件で 2GB/配列上限と LOH 常駐に当たり、seq が疎な場合 (削除 / gen 混在) に無駄が大きい。設計は:
     - **slab 分割**: seq レンジごとの固定サイズ slab (例: slab あたり 64Ki ベクトル相当) を遅延確保する。疎レンジは slab 未確保のまま。
     - **予算は DB 全体**: `GraphDatabaseOptions.VectorCacheBudgetBytes` (仮) を**全 index 合計**で管理し、index 単位に按分または LRU で予算超過 slab を落とす。超過時は現行 pin 経路へフォールバック (機能は不変、速度のみ劣化)。
     - 書き込み (Set/Remove) は write-through。abort/reload (`ReloadAll` / `ReloadFromPages`) でキャッシュを無効化→再構築する経路を必ずテスト (undo 後の整合)。
  2. per-search 確保の除去: `SearchLayer` ごとの `HashSet<long>` visited + `PriorityQueue` ×2、`Search` ごとの `(seq, gen) => IsLive(...)` クロージャ ([PersistentVectorStore.cs:170](../src/Quiver/Stores/PersistentVectorStore.cs)) を再利用可能な search context に集約。
- **Kill criteria**: dim 768 / N 100k / k 10 の KNN レイテンシで**真の flat baseline 比・HNSW 現行比の両方を報告**し、HNSW 現行比 ≥3× 改善。recall 不変 (VP-5 ゲート)。reload 整合テスト緑。
- **判断ポイント**: キャッシュは読み取り経路の写しであり正本はページのまま (WAL/ARIES 契約に触らない、G-1)。予算なし無条件キャッシュにしない。
- **依存関係**: VP-4.0、VP-5 が先。CR-2 と独立だが効果は相補的。工数: 中 (spike 0.5 日 + 本実装 2.5 日)。

### VP-5: recall@k 回帰ゲート

- **[rev2] 既存テストの限界**: VectorHnswTests に recall 検証は存在するが N=1000 / dim=32 / 閾値 0.85 であり、RAG 想定 (dim 384+) の安全網としては弱い。また削除後テストは self-query (自分自身が返るか) であり true recall@k ではない。
- **実装手順**: BDN JSON の性能比較だけを担う既存 `Quiver.Benchmarks.RegressionCheck` へ混在させず、`Quiver.Tests` の決定的な品質回帰テスト (実行時間が過大なら専用 `Quiver.Benchmarks.RecallCheck` executable) として以下を追加:
  1. 決定的コーパス (固定 seed、dim 384、N 10k) で **brute-force ground truth (exact top-10) を毎回計算**し、HNSW top-10 との true recall@10 ≥ 0.95 をゲートにする。
  2. 削除 30% 後 (HealNeighborhood 経由): **削除後の生存集合で ground truth を再計算**し、同じ true recall@10 で固定する (self-query 方式は廃止)。
  3. README のベンチ表に recall 実測を 1 行追加。
- **依存関係**: VP-4.0 (コーパス定義共有)。**VP-2 / VP-4 より先に実施** (安全網)。工数: 小 (0.5〜1 日)。

---

## 4. Track CR — 並行読み取りスケーラビリティ

### CR-1: マルチスレッド読み取りベンチの追加 (実測先行、最初に実施)

- **目的**: [PagedFile.cs](../src/Quiver/Storage/PagedFile.cs) の単一 `_poolLock` がバッファプール操作を保護し、**すべての `PinForRead` / `Unpin` / `UnpinDirty` がプロセス全体で 1 つの mutex を通る** (GetOrLoadFrame:392 は resident hit でも lock、Unpin:223-236 も lock)。単一ファイル化で全テナントがこの 1 つの PagedFile を共有するため、「リーダは並行」契約に対し実態はページアクセス粒度で全リーダが直列化する。既存ベンチは全て単一スレッドでこの乖離が見えていない。
- **実装手順**: `--basic-perf` に {1, 2, 4, 8} スレッドの (a) 1-hop スキャン (read-only tx)、(b) KNN 検索、(c) BM25 検索のスケーリングカーブを追加。理想値 (線形) との比を記録。
- **完了条件**: スケーリング実測が docs/benchmarks に記録され、CR-2/CR-3 の kill criteria の分母になる。
- **依存関係**: なし。**CR-2 / CR-3 より先に必須**。工数: 小 (1 日)。

### CR-2: PagedFile ホットパスのロック競合除去 — spike 先行

- **目的**: resident hit の pin/unpin をグローバルロック外へ出す。
- **[rev2] アプローチの差し替え**: rev1 案 1 (「ConcurrentDictionary 化 + Interlocked pin count」) は **lookup と pin の間に eviction が割り込む race** を解決できないため棄却する。dict から frame index を得た直後に eviction がそのフレームを別ページへ再割当てし得るので、pin count を増やしても「増やした対象がまだ目的のページである」保証がない。
- **改訂アプローチ (spike で検証)**:
  1. **frame generation + optimistic pin (第一候補)**:
     - 各フレームに `Generation` (世代カウンタ) を持たせ、eviction (フレーム再割当て) 時に `_poolLock` 下でインクリメントする。
     - **hit 経路 (lock-free)**: 共有 directory (ConcurrentDictionary or read-mostly 配列) から frame index を読む → `Interlocked.Increment(PinCount)` → **再検証**: フレームの `(PageId, Generation)` が期待と一致するか確認。不一致 (pin の間に evict された) なら decrement して slow path (下記) へ。
     - **miss / eviction 経路 (排他)**: 従来どおり `_poolLock` 下。victim 選定は `PinCount == 0` のフレームのみ (Interlocked increment が先行するため、pin 途中のフレームを evict しても再検証で弾かれ、整合は世代で守られる)。
     - unpin は `Interlocked.Decrement` のみ (lock 不要)。
  2. **ロックストライピング (縮退案)**: PageId ハッシュで N 分割。案 1 の複雑度が spike で管理不能 (フレーム状態機械にデータレースの疑い) と判明したらこちらへ縮退、それも不可なら中断・報告。
- **書き込み側は現行構造を維持**: `PinForWrite` の before-image 捕捉 (`WalPageContext.CaptureBeforeImage`) の原子性、`UnpinDirty` の LSN/checksum 更新→PageImage ログ順序は WAL 耐久性の根幹なので触らない。最初のスコープは**読み取り hit 経路のみ**。
- **Kill criteria**: CR-1 の 4 スレッド 1-hop スループットが 1 スレッド比 ≥3× (スケーリング比で判定)。単一スレッド退行 ±2% 以内。全テスト緑 (crash contract / chaos / stress 含む)。
- **判断ポイント**: 「動くが証明できない」lock-free を採用しない。pin プロトコルの正当性 (increment→再検証→decrement の各順序) はコメントで不変条件として明文化し、stress テストを追加する。
- **依存関係**: CR-1。工数: 中 (spike 1 日 + 本実装 2〜3 日)。

### CR-3: PersistentVectorStore の読み取り並行化 — index 単位ロックへ改訂

- **目的**: [PersistentVectorStore.cs:169](../src/Quiver/Stores/PersistentVectorStore.cs) の `_gate` (monitor) が KNN 検索全体を直列化し、`KnnSearchBatch` (238-247) はロック保持のまま全クエリを逐次処理する。
- **[rev2] ロック構成の差し替え**: rev1 の「`_gate` を global ReaderWriterLockSlim へ置換」では、(i) 別 index への書き込みで全 index の KNN が停止する、(ii) batch がロック保持のまま逐次である点が残る。改訂:
  1. **catalog ロック** (小さな排他): `_indexes` dictionary と `_catalog` の構造変更 (Create/Drop/ReloadAll) のみを守る。lookup は read-mostly (snapshot 参照 or 短い lock)。
  2. **index 単位の `ReaderWriterLockSlim`** (IndexHandle に持たせる): Search/TryGet = read lock、Set/Remove/(per-index) reload = write lock。別 index の書き込みは当該 index の KNN に影響しない。
  3. `KnnSearchBatch` は per-query に read lock を取得 (または read lock 保持で全クエリ — reader 同士は並行なのでどちらでも他 reader を妨げない)。クエリ間の `Parallel.For` 化は read lock 化が入った後の追加候補として記録のみ (このタスクの必須ではない)。
- **判断ポイント**: HNSW Insert/Delete 中のグラフは中間状態 (双方向リンク片側のみ等) を持つ。read lock 化で見える中間状態が検索の正確性 (欠落・無限ループ) を壊さないことを精査し、保証できなければ「書き込み中は排他・読み取り同士のみ並行」の保守的構成で確定する (読み取り主体の RAG ワークロードには十分効く)。writer は single-writer 契約なので write lock の競合は catalog 側に限られる。
- **Kill criteria**: 4 スレッド並行 KNN スループット ≥3× (CR-1 計測比)。単一スレッド退行 ±2% 以内。
- **依存関係**: CR-1。VP-4 と同一セッション可。工数: 中 (1.5〜2.5 日)。

---

## 5. Track DU — durability オプション (REF-7 前提条件から除外)

### DU-1: opt-in 緩和 durability (`synchronous=NORMAL` 相当) — 別トラック化

- **[rev2] 凍結条件からの除外**: `DurabilityMode` オプションの追加は **additive な公開 API 変更**であり、1.x のいつでも無料で追加できる。よって REF-7 (1.0 凍結) の前提条件には**しない**。rev1 が凍結前必須とした根拠 (契約の前倒し確定) は「急がなくても失うものがない」ため成立しない。
- **rev2 での扱い**: 本書には**形状草案のみ**を記録し (下記)、実装・API 確定とも凍結後の別トラック (REF-10 writer queue の後が自然) とする。approved.txt にも今は載せない。
  - 草案: `DurabilityMode Durability { get; set; } = DurabilityMode.Full;` (`Full` / `Relaxed`)、`TimeSpan RelaxedFlushInterval` (既定 100ms)。
  - 契約: `Relaxed` でも WAL 書き込み自体は commit 経路で必ず行う。失うのは直近数十 ms の耐久性のみで、原子性・一貫性・リカバリ正確性は不変。既定は Full (安全側)。
  - 実装時: WAL flush worker (既存 `Channel<FlushRequest>` / flush loop) に周期 flush モードを追加。crash contract テストに「Relaxed で直近 commit が消えても DB は open 可能で一貫」を追加。
- **完了条件 (本トラック内)**: 本節の草案記録をもって完了。REF-7 チェックリストには**載せない**。

---

## 6. スコープ外 (判断記録)

| 項目 | 判断 |
|---|---|
| `System.Numerics.Tensors` (TensorPrimitives) | **不採用。** in-box ではなく PackageReference が必要な OOB パッケージで、ゼロ依存方針と矛盾。VP-1 (手書き) で代替。 |
| HNSW `Delete` の全ノード O(N) back-ref 走査 | 08_known_limits.md へのコスト特性記載のみ。実需が出たら逆参照索引を別タスク。 |
| BM25 非 WAND 経路のアロケーション削減 | FTS が実測でホットになってから。REF-4 と重なるため先行は手戻り。 |
| ネイティブ並行 writer | 将来課題 (REF 記載)。CR トラックは読み取り並行のみ。 |
| DU-1 実装 | **[rev2 移動]** 別トラック化 (§5)。 |

## 7. 推奨実行順序 (rev2、レビュー提案を採用)

1. **本書 (rev2) による計画矛盾の修正** — 完了をもって着手可能。
2. **ZD-2 / VP-3** — 凍結前判断 2 点 (REF-7 前提条件は ZD-2 / VP-3 の 2 点に確定。DU-1 は除外)。VP-3 の (a')/(b) はユーザ判断。
3. **VP-4.0 / VP-5 / CR-1** — 安全網と実測分母の整備 (ベンチ baseline 修正 → recall gate → 並行スケーリング実測)。
4. **ZD-1 / ZD-3** — golden フィクスチャ生成 → CRC 置換 → ゼロ依存ゲート。
5. **VP-2 / CR-3** — VectorSearchOptions と index 単位ロック (同一ファイル周辺、同一セッション可)。
6. **VP-4 spike → 本実装** — slab キャッシュ。
7. **VP-1 / CR-2** — 実験価値はあるが優先度最下位 (VP-1) と、正当性検証が重い最適化 (CR-2)。
8. **DU-1** — 別トラックとして凍結後に実装。

## 8. 完了の定義 (トラック全体)

- core `Quiver` の PackageReference が **0 件**になり、csproj + nuspec の 2 段 CI ゲートで維持され、README/nuspec が「依存パッケージゼロ」を掲げている。
- 凍結前契約 2 点 (ZD-2 / VP-3) が REF-7 チェックリストに反映され、決定が記録されている (DU-1 は別トラック化の判断が記録されている)。
- HNSW ベンチが真の flat baseline を持ち、true recall@10 ゲートが CI で緑。
- 並行読み取りのスケーリングカーブが計測・文書化され、`_poolLock` / `_gate` の直列化が spike の kill criteria を満たす形で解消 (または数値根拠付きで DEFER) されている。
- HNSW 検索が VectorSearchOptions (efSearch) + payload slab キャッシュで改善されている。
