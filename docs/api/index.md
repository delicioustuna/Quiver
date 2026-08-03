# Quiver Documentation

Pure C# で実装するグラフデータベースエンジン Quiver のドキュメントへようこそ。

## このサイトの構成

- **[Getting Started](getting-started.md)** — インストールから最初のクエリまでの最短経路
- **API Reference** — `GraphStore`、`GraphWorkspace`、query、Match、RAGの公開型とメンバー
- **[API surface snapshot](https://github.com/delicioustuna/Quiver/blob/main/tests/Quiver.PublicApi.Tests/PublicApi/Quiver.approved.txt)** — 承認済みの公開 API 一覧

## サンプルコード

リポジトリの [`samples/`](https://github.com/delicioustuna/Quiver/tree/main/samples) に各シナリオを示す回帰・統合サンプルがあります。公開パッケージだけを使う最小例はGetting Startedを正本とします。

```bash
dotnet run --project samples/Quiver.Samples.Hosting
dotnet run --project samples/Quiver.Samples.Rag
```

## ローカルでビルドする

```bash
dotnet tool restore
dotnet docfx docfx.json
dotnet docfx serve docs/api/_site
```

## ライセンス

[MIT License](https://github.com/delicioustuna/Quiver/blob/main/LICENSE)
