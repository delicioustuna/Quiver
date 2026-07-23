# Quiver 開発者向けドキュメント

本書は、リポジトリで開発を行うための構成、検証手順、運用規則を記す。

- 現行仕様：[docs/spec/](../spec/)
- 現行計画：[plans/README.md](../../plans/README.md)
- API安定性：[docs/api-stability.md](../api-stability.md)
- 運用ガイド：[docs/operations/README.md](../operations/README.md)

## 正本

実装済みの契約は `docs/spec/` を正本とする。

計画は未実装の目標を含むため、現行仕様として扱わない。
計画とコードが食い違う場合は、互換用の旧APIを追加せず、計画を修正してから実装する。

完了した計画、過去の比較案、個別の実測記録は通常の作業対象から外す。
必要な履歴はGitから復元する。

## プロジェクト構成

| プロジェクト | 役割 | 配布 |
|---|---|---|
| `Quiver` | ストレージ、WAL、MVCC、クエリ、全文検索、ベクトル検索を含むエンジン中核 | NuGet |
| `Quiver.SourceGen` | 型付きグラフモデル用のRoslyn Source Generator | `Quiver`へ同梱 |
| `Quiver.Rag` | DocumentとChunkの取込、ハイブリッド検索、graph expansion | NuGet |
| `Quiver.Hosting` | `Microsoft.Extensions.Hosting`とDIの統合 | NuGet |
| `Quiver.OpenTelemetry` | OpenTelemetryの計装登録 | NuGet |

依存方向はアドオンからエンジン中核への一方向とする。
エンジン中核はアドオンへ依存しない。

```text
Quiver.SourceGen  -- analyzer --> Quiver
Quiver.Rag        --------------> Quiver
Quiver.Hosting    --------------> Quiver
Quiver.OpenTelemetry -----------> Quiver
```

## エンジン構成

中核エンジンは単一アセンブリ `Quiver` に集約し、名前空間と内部型でレイヤを分ける。

```text
Quiver.Api                  public facade and typed DSL
Quiver.Query                logical IR, optimizer, physical operators
Quiver.Transactions         writer lease, snapshots, recovery, checkpoint
Quiver.Index                scalar and full-text indexes
Quiver.Storage.Records      versioned entities, properties, payloads
Quiver.Storage.Wal          redo-only write-ahead log
Quiver.Storage              pages, buffer pool, single-file container
Quiver.Codec                binary encoding primitives
Quiver.Core                 shared identities and contracts
```

ストレージ、WAL、MVCC、索引、クエリの契約は、対応する[仕様書](../spec/)を参照する。

## ビルドとテスト

必要なSDKは.NET 10以降である。

```powershell
dotnet build Quiver.slnx
dotnet test Quiver.slnx
```

各変更では、最低限次の検証を行う。

1. 変更箇所のfocused testを実行する。
2. `dotnet build Quiver.slnx`を実行する。
3. 公開APIを変更した場合はPublic API approval baselineを更新する。
4. 永続形式またはトランザクション契約を変更した場合は、対応するcrash testを実行する。
5. 性能へ影響する場合は、該当ベンチマークと回帰判定を実行する。
6. `scripts/agent-guardrails/check-track-markers.ps1 -DiffAgainst HEAD`を実行する。
7. `git diff --check`を実行する。

Public APIは`tests/Quiver.PublicApi.Tests/`のapproval testで固定する。
性能の公開値は[ベンチマーク結果](../benchmark-results.md)へ集約し、回帰判定の基準値は`benchmarks/baselines/main.json`で管理する。

## APIドキュメント

公開APIリファレンスはXML documentationとDocFXから生成する。

```powershell
docfx metadata docfx.json
docfx build docfx.json
```

生成物は`docs/api/_apispec/`と`docs/api/_site/`へ出力し、Gitでは管理しない。
手書きの利用ガイドは`docs/api/`、運用手順は`docs/operations/`に置く。

## 開発計画

`plans/`には、未完了または検討中の計画だけを置く。

計画に記載されていることは、着手承認または採用決定を意味しない。
計画を実装するときは、対象の契約、完了条件、decision logを確認する。

## コメントと内部管理表記

コードでHow、テスト名とassertでWhat、コミット本文でWhyを表す。
ソースコメントには、局所コードから復元できない不変条件または代替を採用しなかった理由を書く。

内部タスク番号、Wave番号、比較案の記号は、`plans/`と`docs/design/`以外のソース、テスト、ベンチマーク、公開文書へ残さない。
検出ロジックの正本は`scripts/agent-guardrails/check-track-markers.ps1`とする。

検査対象は変更差分へ絞る。
正規表現の一致は候補シグナルとして扱い、意味上の違反かどうかを差分で確認する。

## ローカル設定

`.agents/`、`.claude/`、`AGENTS.md`、`CLAUDE.md`はGitで管理しない。

`quiver-implement`のミラーはbyte-for-byteで一致させる。
可変の進捗、判断、検証結果をスキルへ記録せず、追跡中の計画またはコミットへ記録する。

## Versioning

公開バージョンは[Semantic Versioning](https://semver.org/)に従う。
バージョンの正本は[Directory.Build.props](../../Directory.Build.props)の`VersionPrefix`である。

オンディスクのformat family versionは公開SemVerとは独立して管理する。
互換性の契約は[API安定性ポリシー](../api-stability.md)と[既知の限界](../spec/08_known_limits.md)を参照する。

## NuGetパッケージ

`dotnet pack Quiver.slnx`は、packableなプロジェクトだけをパッケージ化する。

```powershell
dotnet pack Quiver.slnx --configuration Release --output artifacts/nupkg
```

| パッケージ | 主な依存 |
|---|---|
| `Quiver` | コアの外部パッケージ依存なし |
| `Quiver.Hosting` | `Quiver`、Microsoft.Extensions |
| `Quiver.OpenTelemetry` | `Quiver`、OpenTelemetry |
| `Quiver.Rag` | `Quiver` |

Source Generator、README、アイコン、symbol packageの同梱規則は`Directory.Build.targets`と各`.csproj`で管理する。
