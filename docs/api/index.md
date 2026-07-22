# Quiver Documentation

Pure C# で実装するグラフデータベースエンジン Quiver のドキュメントへようこそ。

## このサイトの構成

- **[Getting Started](getting-started.md)** — インストールから最初のクエリまでの最短経路
- **[Concepts](concepts/index.md)** — Vertex / Edge モデル、Transaction、Traversal、MERGE、KNN、Backends
- **[Tutorials](tutorials/index.md)** — 短いコード例で手を動かしながら覚える
- **[API surface snapshot](../../tests/Quiver.PublicApi.Tests/PublicApi/Quiver.approved.txt)** — 承認済みの公開 API 一覧

## サンプルコード

リポジトリの [`samples/`](https://github.com/delicioustuna/Quiver/tree/main/samples) に CRUD、Traversal、Match、SourceGen、Vector の各シナリオを示すサンプルプロジェクトがあります。

```bash
dotnet run --project samples/Quiver.Samples.Crud
dotnet run --project samples/Quiver.Samples.Traversal
dotnet run --project samples/Quiver.Samples.Match
dotnet run --project samples/Quiver.Samples.SourceGen
dotnet run --project samples/Quiver.Samples.Vector
```

## ローカルでビルドする

```bash
dotnet tool install -g docfx
docfx build docfx.json
docfx serve docs/api/_site
```

## ライセンス

[MIT License](../../LICENSE)
