# Yatagarasu Documentation

Pure C# で実装するグラフデータベースエンジン Yatagarasu のドキュメントへようこそ。

## このサイトの構成

- **[Getting Started](getting-started.md)** — インストールから最初のクエリまでの最短経路
- **API Reference** — `GraphStore`、`GraphWorkspace`、query、Match、RAGの公開型とメンバー
- **[API surface snapshot](https://github.com/delicioustuna/Yatagarasu/blob/main/tests/Yatagarasu.PublicApi.Tests/PublicApi/Yatagarasu.approved.txt)** — 承認済みの公開 API 一覧

## サンプルコード

リポジトリの [`samples/`](https://github.com/delicioustuna/Yatagarasu/tree/main/samples) に各シナリオを示す回帰・統合サンプルがあります。公開パッケージだけを使う最小例はGetting Startedを正本とします。

```bash
dotnet run --project samples/Yatagarasu.Samples.Hosting
dotnet run --project samples/Yatagarasu.Samples.Rag
```

## ローカルでビルドする

```bash
dotnet tool restore
dotnet docfx docfx.json
dotnet docfx serve docs/api/_site
```

## ライセンス

[MIT License](https://github.com/delicioustuna/Yatagarasu/blob/main/LICENSE)
