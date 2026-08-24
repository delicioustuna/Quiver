# Yatagarasu 0.7.0 改名・移行手順

Yatagarasu 0.7.0 は、旧製品名 Quiver からの改名リリースである。グラフ、ベクトル、全文検索の実装契約は維持し、配布 ID、名前空間、製品名を一貫して変更する。

## 利用者側の変更

NuGet 参照を次のように置き換える。

| 旧 ID | 新 ID |
|---|---|
| `Quiver` | `Yatagarasu` |
| `Quiver.Hosting` | `Yatagarasu.Hosting` |
| `Quiver.OpenTelemetry` | `Yatagarasu.OpenTelemetry` |
| `Quiver.Rag` | `Yatagarasu.Rag` |

`Yatagarasu.SourceGen` は `Yatagarasu` パッケージへ analyzer として同梱し、独立した NuGet パッケージとしては公開しない。

ルート名前空間は `Quiver` / `Quiver.Api` から `Yatagarasu` / `Yatagarasu.Api` へ変更する。製品名を含む公開型も同じ規則で変更する。たとえば `QuiverDatabase` は `YatagarasuDatabase`、`QuiverDatabaseOptions` は `YatagarasuDatabaseOptions` となる。`GraphWorkspace`、`Vertex`、`Edge`、`Nexus`、`Property`、`Indexed` は変更しない。

データベースの標準拡張子は `.quiver` から `.yata` へ変更する。既存データを改名する場合はデータベースをクリーンに閉じ、primary file、`-wal` sidecar、`-ftseg` artifact directoryを同じ基底名へまとめて変更する。クリーン終了後にprimary fileだけが残っている場合は、そのファイルを `.yata` へ変更すればよい。

オンディスクの `QUIVER-SW` family magic は、0.5.x / 0.6.x データとの互換性を維持するため変更しない。これは永続形式の識別子であり、0.7.0以降の製品名や名前空間ではない。

## リリース担当者の NuGet 手作業

旧パッケージのdeprecateはnuget.orgのWeb UIで行い、自動化しない。

1. `v0.7.0` のrelease workflowが完了し、新しい5パッケージがnuget.orgに存在することを確認する。
2. nuget.orgの管理画面で `Quiver`、`Quiver.Hosting`、`Quiver.OpenTelemetry`、`Quiver.Rag` を1件ずつ開く。
3. 各パッケージのdeprecation設定でlegacy扱いを選び、対応する `Yatagarasu` パッケージを代替パッケージとして指定する。
4. 保存後、各旧パッケージの公開ページにdeprecation表示と新IDへのリンクが出ることを未ログイン状態でも確認する。
5. 旧バージョンを参照する既存restoreを壊さないため、旧パッケージを削除またはunlistしない。

この操作は新パッケージの公開成功後にだけ実施する。公開前に旧パッケージをdeprecateすると、移行先が取得できない時間帯が発生する。
