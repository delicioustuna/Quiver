# Quiver Studio — Avalonia UI デスクトップアプリ実装プラン

承認日: 2026-06-22。クロスプラットフォーム GUI ツール (LiteDB Studio ライク + グラフ可視化)。

## Context

Quiver エンジンに対して、GUI クエリ操作 + NodeNetwork 的なインタラクティブグラフ可視化を行えるクロスプラットフォームデスクトップアプリを提供する。

## 技術スタック

- **UI**: Avalonia 12.x + FluentTheme
- **バインディング**: R3 (`ReactiveProperty<T>`) + CommunityToolkit.Mvvm (`[ObservableProperty]` / `[RelayCommand]`) + `Dispatcher.UIThread` (R3 → Subscribe → Dispatcher.Post → generated INPC)
- **コレクション**: ObservableCollections (Phase 1c DataGrid 以降で使用)
- **DI/ホスト**: Microsoft.Extensions.Hosting (Generic Host)
- **エディタ**: Avalonia.AvaloniaEdit 12.x
- **グラフ描画**: SkiaSharp (Avalonia 12 同梱 Skia)
- **クエリ実行**: Microsoft.CodeAnalysis.CSharp.Scripting (Roslyn)
- **プロジェクト参照**: `Quiver.csproj` のみ (Quiver.Hosting は不使用 — 動的接続のため)

## Avalonia 12 開発 注意事項

### デバッグ方法論: 真因の特定と変更の分離

外部コントロール (AvaloniaEdit, DataGrid) の「表示されない」問題で、真因が StyleInclude 未登録の 1 点だったにもかかわらず、特定に至るまでに XAML 名前空間変更・コードビハインド生成・ExpandoObject→string[] 切替・TwoWay→OneWay 等の変更を積み重ねた。結果、真因でない変更が「必要な対処」として誤記録された。

**教訓:**
- **仮説を立て、3 階層程度まで組み合わせて検証する。** 初手の仮説に対して関連する変更を重ねて試すのは正当な手順。ただし 3 層程度試して解決しなければ、その仮説自体を疑い別のアプローチに切り替える。今回は「XAML 名前空間 → コードビハインド生成 → 初期化タイミング」と同一仮説 (レンダリング機序) を掘り続けたが、真因は設定の欠落 (StyleInclude) という別スコープだった。
- **アプローチを切り替える際、先の仮説の変更は機序に応じて復活も検討する。** 全部戻すのでも全部残すのでもなく、新しい仮説と独立に効く可能性がある変更は併用して試す。
- **真因を特定した後、それまでの変更を 1 つずつ外して再検証する。** 真因修正で不要になった変更を残すと、誤った制約としてプランや記憶に定着する。
- **推論で「これが原因だろう」と結論しない。** 公式ドキュメントやコミュニティの情報を先に収集し、既知の問題か確認してから対処する。

### 確認済みの注意点

#### 1. 外部コントロールは App.axaml に StyleInclude が必須
Avalonia 12 の外部コントロール (FluentTheme に同梱されないもの) は、`App.axaml` に StyleInclude を明示登録しないと **テンプレート無しで描画され、表示されない・入力不能になる**。NuGet パッケージを追加しただけでは動かない。症状は多岐にわたる (描画されない、入力不能、データが空) ため、外部コントロールの不具合を疑う前にまず StyleInclude の登録を確認すること。
```xml
<Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml" />
    <StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml" />
</Application.Styles>
```
**新しい外部コントロール NuGet を追加したら、必ず DLL 内のテーマ XAML リソースパスを確認して StyleInclude を追加すること。**

#### 2. TextMate テーマは LightPlus を使う
TextMateSharp の無印 `Light` は文字列・型名・メソッド名のスコープが不足。`LightPlus` (VS Code デフォルト拡張版) で全要素が色分けされる。

#### 3. DataGrid で ExpandoObject は使えない
Avalonia DataGrid は ExpandoObject のプロパティバインディングを解決できない — 列ヘッダーと行枠は表示されるがセル値が空になる ([#18209](https://github.com/AvaloniaUI/Avalonia/discussions/18209))。動的行は `string[]` + ordinal インデクサ `[0]`, `[1]` で代替する。この問題は StyleInclude 登録後に再検証して確定したもの。

#### 4. R3Extensions.Avalonia は Avalonia 12 非対応
`ReactiveProperty<T>` → `Subscribe` + `Dispatcher.UIThread.Post()` → CommunityToolkit.Mvvm `[ObservableProperty]` への転写で代替。

## プロジェクト構造

`tools/Quiver.Studio/` (WinExe, net10.0, IsPackable=false)。`Quiver.slnx` の `/tools/` フォルダに所属。

```
tools/Quiver.Studio/
  Program.cs                     # Generic Host + Avalonia + 3 層例外ハンドリング
  App.axaml / App.axaml.cs       # FluentTheme, DI ServiceProvider
  ViewModels/
    MainWindowViewModel.cs       # シェル (INPC + R3 Subscribe)
    SchemaBrowserViewModel.cs    # ラベル/RelType/PropKey/Index/FTS/Vector ツリー
    QueryEditorViewModel.cs      # AvaloniaEdit + Roslyn 実行
    ResultsViewModel.cs          # DataGrid 動的列表示
    GraphCanvasViewModel.cs      # VisualNode/Edge + カメラ + 選択
    PropertyInspectorViewModel.cs
  Views/
    MainWindow.axaml             # DockPanel: 左サイドバー (接続+スキーマ) + 中央 + 下部
    QueryEditor.axaml            # AvaloniaEdit コントロール
    ResultsView.axaml            # DataGrid
    GraphCanvas.axaml            # SkiaSharp キャンバス
    PropertyInspector.axaml      # KeyValue リスト
  Models/
    VisualNode.cs / VisualEdge.cs / SchemaTreeNode.cs
  Services/
    DatabaseService.cs           # GraphDatabase ライフサイクル + ReactiveProperty
    QueryExecutionService.cs     # Roslyn CSharpScript 実行ブリッジ
    GraphLayoutService.cs        # Fruchterman-Reingold 力指向レイアウト
  Converters/
    FileSizeConverter.cs
  Rendering/
    GraphRenderer.cs / HitTestHelper.cs / CameraTransform.cs
```

## 設計判断

### クエリ実行: Roslyn C# Scripting
ScriptGlobals (`db`, `tx`, `g`, `schema`) を事前バインドし、ユーザは Quiver API をそのまま C# で書く。既定は読み取り専用 tx。

### グラフ可視化: SkiaSharp + Fruchterman-Reingold
力指向レイアウト (300 iterations, >500 ノードで Barnes-Hut)。パン/ズーム/ノードドラッグ。

### VM パターン: R3 ReactiveProperty → CommunityToolkit.Mvvm
DatabaseService が `ReactiveProperty<T>` を公開。VM は `Subscribe` + `Dispatcher.UIThread.Post()` で CommunityToolkit.Mvvm の `[ObservableProperty]` フィールドへ転写。XAML は標準 `{Binding}` で動作。R3Extensions.Avalonia は Avalonia 12 非対応のため不使用。

### 例外ハンドリング (3 層)
1. `AppDomain.CurrentDomain.UnhandledException` — 致命的例外
2. `TaskScheduler.UnobservedTaskException` — 未観測 Task
3. `R3.ObservableSystem.RegisterUnhandledExceptionHandler` — R3 サブスクリプション

## 実装フェーズ + 進捗

### Phase 0a: ISchemaApi 列挙 API 追加 ✅
`ListLabels()` / `ListRelationshipTypes()` / `ListPropertyKeys()` を ISchemaApi + SchemaApi に追加。commit c7a945c。

### Phase 0b: スケルトン ✅
プロジェクト作成、Generic Host + Avalonia 12 統合、3 層例外ハンドリング。commit c7a945c。

### Phase 1a: DB 接続パネル + スキーマブラウザ ✅
- DatabaseService (Open/Close/IsOpen/FilePath/Statistics の ReactiveProperty 公開) ✅
- MainWindow 左サイドバー (接続パネル + 統計 + スキーマ TreeView) ✅
- SchemaBrowserViewModel + SchemaTreeNode ✅
- ファイルダイアログ (StorageProvider) + エラーダイアログ ✅
- FileSizeConverter ✅
- R3Extensions.Avalonia 除去 + CommunityToolkit.Mvvm 導入 + Dispatcher UIスレッド安全化 ✅
- **残**: MRU (最近使ったファイル) リスト永続化 → Phase 1f へ繰り延べ

### Phase 1b: クエリエディタ + Roslyn 実行 ✅
- AvaloniaEdit 統合 (TextMate C# シンタックスハイライト) ✅
  - App.axaml に StyleInclude 必須 (`avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml`)
  - TextEditor はコードビハインド生成方式 (XAML 名前空間経由だと描画不能)
  - テーマ: LightPlus (Light は型名・文字列・メソッドのスコープ不足)
- QueryExecutionService (Roslyn CSharpScript + ScriptGlobals: db/tx/g/schema) ✅
- 結果の実体化 (scalar/tabular/enumerable→QueryResult) ✅
- エラー表示 (コンパイルエラー/実行時例外→Output パネル) ✅
- F5 ショートカット + Execute ボタン ✅
- 出力は Phase 1c の DataGrid 導入まで text table 形式

### Phase 1c: 結果ビュー ✅
- DataGrid 動的列生成 (string[] 行 + ordinal インデクサ) ✅
  - ExpandoObject は Avalonia DataGrid 非対応 (注意事項 §3)
  - DataGrid StyleInclude 登録必須 (注意事項 §1)
- Results/Output タブ切替 ✅
- CSV/JSON コピー ✅
- 行選択→PropertyInspector 連携 → Phase 1e へ繰り延べ

### Phase 1d: グラフキャンバス
- VisualNode / VisualEdge モデル
- GraphLayoutService (Fruchterman-Reingold)
- GraphRenderer (SkiaSharp: ノード=色付き円+ラベル、エッジ=矢印+型名)
- CameraTransform (パン/ズーム) + HitTestHelper
- ノードドラッグ
- クエリ結果からの NodeId/RelationshipId 抽出→グラフ構築

### Phase 1e: プロパティインスペクタ
- 選択エンティティの全プロパティ表示
- PropertyValue ref struct → Dictionary 実体化

### Phase 1f: ステータスバー + 仕上げ
- 接続状態、実行時間、ノード/エッジ数、ズーム率
- キーボードショートカット (F5, Ctrl+O, Ctrl+N)
- Dark/Light テーマ切替

### Phase 2 (将来)
- インタラクティブグラフ編集 (ノード/Rel の GUI 追加/削除)
- ベクトル検索結果の距離スコア可視化
- 全文検索パネル
- クエリ履歴の永続化
- Roslyn IntelliSense (コード補完)
- 階層レイアウト (Sugiyama) トグル
- グラフの SVG/PNG エクスポート

## 検証方法

1. `dotnet build Quiver.slnx` で全プロジェクトビルド成功
2. `dotnet run --project tools/Quiver.Studio` でウィンドウ起動
3. サンプル DB を開いてスキーマツリー表示確認
4. クエリ実行→結果表示→グラフ可視化の E2E 動作
5. ノードクリック→プロパティインスペクタ表示
6. パン/ズーム/ノードドラッグの操作性
