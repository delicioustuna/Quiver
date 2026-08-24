# Yatagarasu

> [英語版README](README.md)を正本とします。

[![CI](https://github.com/delicioustuna/Yatagarasu/actions/workflows/ci.yml/badge.svg)](https://github.com/delicioustuna/Yatagarasu/actions/workflows/ci.yml)
[![AOT publish smoke](https://github.com/delicioustuna/Yatagarasu/actions/workflows/aot.yml/badge.svg)](https://github.com/delicioustuna/Yatagarasu/actions/workflows/aot.yml)
[![OS portability](https://github.com/delicioustuna/Yatagarasu/actions/workflows/portability.yml/badge.svg)](https://github.com/delicioustuna/Yatagarasu/actions/workflows/portability.yml)

Yatagarasuは、グラフデータベース、ベクトル検索、全文検索を統合した.NET向けの組み込みデータベースエンジンです。
プロパティグラフとロール付きn項関係のNexusを単一ファイルへ保存し、Source Generatorによる型安全なマッピング、Fluent APIによるグラフ走査、トランザクション永続化、KNN検索、BM25検索を提供します。

コアエンジンはPure C#で実装され、サードパーティ製パッケージとアンマネージドライブラリに依存せず、NativeAOTに対応します。

## 特徴

- サーバープロセスを必要としないin-process構成
- プロパティグラフの単一ファイル永続化
- `[Vertex]`、`[Edge]`、`[Nexus]`、`[Property]`から生成する型安全なマッピング
- ロール付きn項関係を表すNexusと型付きworkspace API
- Fluentなグラフ走査と宣言的パターンマッチ
- Single Writerと並行Snapshot Readers
- redo-only WALリカバリとdurable commit
- opt-in文字列一意制約を持つB+Treeスカラ索引
- immutable HNSW segmentによるKNNベクトル検索
- immutable index segmentによるBM25全文検索
- ローカルRAG向けのハイブリッド検索

## クイックスタート

型付きグラフモデルを定義します。

```csharp
using Yatagarasu.Api;

[Vertex]
public partial class Person
{
    [Indexed(Unique = true)]
    [Property]
    public string Name { get; set; } = "";

    [Property]
    public int Age { get; set; }
}

[Edge<Person, Person>]
public partial class Knows
{
    [Property]
    public string Since { get; set; } = "";
}
```

型付きworkspaceから書き込みと検索を行います。write callbackが正常終了すると自動でcommitします。

```csharp
using Yatagarasu;

using var graph = GraphWorkspace.Open("people.yata");
graph.Write(write =>
{
    var people = write.Set<Person>();
    var alice = write.Add(people, new Person { Name = "Alice", Age = 30 });
    var bob = write.Add(people, new Person { Name = "Bob", Age = 25 });
    write.Connect(alice, new Knows { Since = "2026" }, bob);
});

IReadOnlyList<Person> known = graph.Read(read =>
    read.Raw.Query.Vertices<Person>()
        .Has(p => p.Name, "Alice")
        .Out<Person>(Knows.GraphType)
        .Where(p => p.Age < 30)
        .ToList());
```

`Yatagarasu`パッケージには、モデル属性とSource Generatorが含まれます。
`ImplicitUsings`が有効なプロジェクトには、`Yatagarasu`と`Yatagarasu.Api`の名前空間が自動的に追加されます。

公開バージョンは現在`0.7.0`であり、1.0未満です。

## 名前の由来

Yatagarasu（八咫烏）は、日本神話で神武天皇を導いた三本足の烏です。三本の足はgraph、vector、full-textの3エンジンに対応し、神武東征を導いた役割は、必要な情報へ利用者を導く検索という製品の役割を表しています。

## ローカルRAG

`Yatagarasu.Rag`は、DocumentとChunkの取込、チャンキング、再取込、メタデータフィルタ、ベクトル検索とBM25の融合、周辺コンテキストと親文書へのgraph expansionを提供します。

埋め込み生成は呼び出し側アプリケーションが担い、`IChunkEmbedder`を通じて注入します。

[RAGサンプル](samples/Yatagarasu.Samples.Rag/)と[ローカルRAGのレシピ](docs/cookbook.md)を参照してください。

## 参考性能

| 操作 | 実測値 |
|---|---:|
| Vertex作成、単一transaction内で償却 | 約3.5～4 µs/op |
| Vertex作成とプロパティ設定 | 約6 µs/op |
| Edge作成 | 約7 µs/op |
| 1操作ごとのdurable commit | 約1.0 ms/commit |
| degree 100の1-hop scan、adjacency segment使用 | 約0.35 µs |
| degree 100の1-hop Fluent query | 約4.2 µs/query |
| 10万EdgeのBulkLoader | 通常のbatch transaction比で約11.8倍 |
| HNSW true recall@10、N=10,000、dim=384 | 0.950 |

AMD Ryzen 7 5700Xと.NET 10を使用し、in-processで計測した参考値です。
異なる環境で同じ値になることを保証するものではありません。
詳細は[ベンチマーク結果](docs/benchmark-results.md)を参照してください。

## 制限事項

| 制限 | 挙動 |
|---|---|
| Single Writer | 同時に実行するwrite transactionは1つです。readerは独立したsnapshotで並行動作します |
| In-processのみ | 1つのプロセスがデータベースファイルを排他的に開きます。ネットワークプロトコルは含みません |
| 物理形式の自動移行なし | 互換性のない形式のデータベースは、ソースデータから再構築します |
| derived vectorの再構築 | immutable HNSWが利用できない場合や再構築中はexact scanを使用します |

契約の詳細は[既知の限界](docs/spec/08_known_limits.md)を参照してください。

## ドキュメント

| 文書 | 内容 |
|---|---|
| [Getting Started](docs/api/getting-started.md) | 導入と最初のデータベース |
| [APIリファレンス](docs/api/) | 承認済みの公開型とメンバー |
| [現行仕様](docs/spec/00_overview.md) | 現在のストレージと実行契約 |
| [0.7.0改名ガイド](docs/operations/06_yatagarasu_rename.md) | パッケージ、名前空間、`.yata`への移行 |

## サンプル

[`samples/`](samples/)には、CRUD、型付きモデル、Nexus、traversal、pattern matching、ベクトル検索、RAG、hosting、observability、migrationのサンプルがあります。

## ライセンス

[MIT License](LICENSE)を適用します。
