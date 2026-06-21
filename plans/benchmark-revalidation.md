# ベンチマーク再取得プラン

## 背景

ユーザー向けドキュメント (`docs/benchmark-results.md`, `docs/operations/`) で引用するベンチマーク結果のうち、
WAL 増幅計測の絶対スループット値が陳腐化している可能性がある。

| 計測 | 取得日 | 現在の参照先 | 再実行要否 |
|---|---|---|---|
| WAL 増幅 (旧 FT-20) | 2026-05-24 | `docs/benchmark-results.md` §索引付き書き込みの WAL 増幅 | **要** |
| MergeRelationship コスト (旧 QP-3) | 2026-06-18 | `docs/benchmark-results.md` §MergeRelationship の degree 依存コスト | 不要 |
| BasicPerf (README 基本性能) | 2026-06-09 | `README.md` (インライン) | 不要 |

## WAL 増幅計測の再取得が必要な理由

2026-05-24 の取得後に以下の性能改善が入った (2026-06-08〜09):

- GenStamp 高速パス: version sidecar 読み取りの省略
- checksum-at-load: pin ごとの CRC32 再計算を撤去
- deferred WAL encode: ホットページ反復書込の増幅を解消

これにより単一 tx 償却のノード作成が ~17µs → ~3.5µs (~4.8x) に改善。
WAL bytes/entry の**比率は構造的に不変** (PageImage coalesce の仕組み自体は変わっていない) だが、
**bulk パスの絶対スループット** (~100k inserts/sec) と実行時間 (100k で 1,022ms) は改善している可能性が高い。

operations 文書と known_limits で引用しているため、正確な現行値を確定したい。

## 再取得手順

### 1. 実行

```bash
dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --ft20-wal
```

ランナー: `benchmarks/Quiver.Benchmarks/Standalone/FT20WalAmplificationRunner.cs`

出力形式: `scenario, entryCount, walBytes, walBytesPerEntry, wallMs` の CSV

### 2. 結果の反映先

| ファイル | 更新内容 |
|---|---|
| `docs/benchmark-results.md` §索引付き書き込みの WAL 増幅 | 結果テーブルの実行時間列を更新 |
| `docs/operations/03_performance_tuning.md` 鉄則テーブル | wall time、スループット列を更新 |
| `docs/operations/05_known_limits.md` 書き込みスループット | `~100k inserts/sec` の数値を更新 (変動があれば) |

### 3. 判断基準

- WAL bytes/entry が前回比 ±10% 以内: 構造不変を確認。テーブルはそのまま。
- wall time が前回比で有意に改善 (> 20%): 実行時間列とスループット記載を更新。
- WAL bytes/entry が大きく変動: 原因調査が必要 (コードパス変更の影響)。

## 他の計測が不要な理由

- **MergeRelationship (旧 QP-3)**: 2026-06-18 取得。関連コードパスに変更なし。
- **BasicPerf (README)**: 2026-06-09 取得。SIG トラック (6/20) はスコアリング層の追加であり、コア CRUD/traversal 性能に影響しない。
