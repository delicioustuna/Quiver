# 組み込みRAG比較ベンチマーク計画

起票日: 2026-07-02

状態: 設計案

優先度: P1

## 背景

QuiverのREADMEには単体のマイクロベンチマーク結果があるが、比較軸としているSQLite、LiteDB、KuzuDB系との同一ワークロード比較はない。

内部演算子の速度だけでは、ローカルRAGバックエンドとしての取込時間、検索レイテンシ、メモリ、ファイルサイズを判断できない。

各製品の機能集合は一致しないため、すべての処理を一つの総合点へ畳み込むと比較を歪める。

## 目的

同一データ、同一クエリ、同一マシンで、組み込みDBとして利用した場合の実効性能を測定する。

比較可能な機能だけを直接比較し、製品固有の機能は別表で示す。

## 非目標

- 単一の総合順位を作らない。
- サーバ型DBや分散DBを比較対象に含めない。
- 各製品に存在しない機能を外部サービスで補って同等機能と見なさない。
- Quiverだけに有利なデータモデルへ固定しない。

## 比較対象

最低限、次のadapterを用意する。

- Quiver binary backend
- Quiver in-memory backend
- SQLite
- LiteDB
- KuzuDB系の利用可能な.NET組み込み構成

実装着手時に、各製品のライセンス、.NET対応、Native依存、vector検索、全文検索の提供状況を確認する。

存在しない機能は `N/A` とし、代替実装を製品本体の性能として扱わない。

## ベンチマーク構造

新規solution projectを `benchmarks/Quiver.Benchmarks.Comparative/` に置く。

製品ごとの差分は次のinterfaceへ閉じ込める。

```csharp
internal interface IEmbeddedRagBackend : IAsyncDisposable
{
    ValueTask InitializeAsync(BenchmarkSchema schema, CancellationToken cancellationToken);
    ValueTask IngestAsync(IReadOnlyList<BenchmarkDocument> documents, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<SearchHit>> SearchTextAsync(TextQuery query, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<SearchHit>> SearchVectorAsync(VectorQuery query, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<SearchHit>> SearchHybridAsync(HybridQuery query, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<long>> ExpandGraphAsync(GraphQuery query, CancellationToken cancellationToken);
}
```

共通harnessはデータ生成、正解集合、warmup、測定、結果出力だけを担当する。

adapter内部のスキーマや索引は、各製品の推奨構成を使う。

## データセット

### 決定的な合成データ

CIと回帰確認にはseed固定の合成コーパスを使う。

規模は次の3段階とする。

| 規模 | 文書 | chunk | relationship |
|---|---:|---:|---:|
| Small | 1,000 | 10,000 | 20,000 |
| Medium | 10,000 | 100,000 | 200,000 |
| Large | 100,000 | 1,000,000 | 2,000,000 |

各chunkは固定次元の埋め込み、全文、metadata、親文書と前後chunkへのedgeを持つ。

### 公開コーパス

利用者向けレポートには、再配布可能な公開コーパスを一つ追加する。

ライセンスと取得手順を記録し、raw dataをリポジトリへ直接commitしない。

## ワークロード

| ID | ワークロード | 主な測定値 |
|---|---|---|
| C1 | DB作成とschema初期化 | wall time、allocation |
| C2 | bulk ingest | docs/sec、chunks/sec、peak RSS、file size |
| C3 | 再オープン | open time、recovery time |
| C4 | metadata完全一致と範囲検索 | p50、p95、p99、throughput |
| C5 | 1-hopと2-hop graph traversal | p50、p95、visited edges/sec |
| C6 | BM25全文検索 | p50、p95、Recall@k |
| C7 | vector KNN | p50、p95、Recall@k |
| C8 | metadata付きvector検索 | p50、p95、Recall@k |
| C9 | hybrid検索 | p50、p95、nDCG@k |
| C10 | 検索後のgraph expansion | end-to-end p50、p95 |
| C11 | 1件commitとbatch commit | commits/sec、fsync影響 |
| C12 | 異常終了後の再オープン | recovery time、整合性 |

製品がサポートしないワークロードは測定対象外とし、比較表で理由を示す。

## 測定規約

- Release buildを使う。
- CPU、RAM、OS、ストレージ、SDK、製品versionを記録する。
- cold runとwarm runを分ける。
- 各scenarioは独立した新規DBで開始する。
- warmup後に最低30sampleを取得する。
- 平均だけでなくp50、p95、p99と分散を保存する。
- timeout、失敗、OOMを結果から除外せず記録する。
- GC mode、thread数、batch size、vector次元を固定する。
- 正解集合を共有し、速度とRecallを同時に評価する。

## 結果形式

機械可読結果を `artifacts/comparative/*.json` に出力する。

利用者向けの確定結果だけを `docs/benchmark-results.md` に反映する。

各表には次を併記する。

- 比較可能な機能範囲
- adapterと製品version
- 測定commit
- 測定環境
- tuning parameter
- `N/A` の理由

## 検証

- 全adapterが共通の正解集合に対して結果を検証する。
- 取込後の文書数、chunk数、edge数が一致する。
- exact queryの結果集合が一致する。
- approximate vector検索はRecall@kを併記する。
- harness単体テストでsample集計とpercentile計算を検証する。
- `dotnet build Quiver.slnx` を実行する。

## 完了条件

- SmallとMediumを全対応adapterで再現可能に実行できる。
- Quiver、SQLite、LiteDB、KuzuDB系の比較可能範囲が明示される。
- end-to-end RAG検索のレイテンシ、メモリ、ファイルサイズが測定される。
- 結果JSONから文書表を再生成できる。
- Quiverに不利な結果も除外せず公開される。

## 依存関係

`plans/benchmark-regression-baseline.md` の回帰ゲートとは別系統とする。

比較ベンチマークは環境依存性が高いため、PR必須checkにはしない。

確定値の取得は `plans/build-reproducibility-and-coverage.md` のSDKと依存version固定後に行う。
