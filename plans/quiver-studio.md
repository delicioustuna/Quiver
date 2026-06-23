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
    FullTextSearchViewModel.cs   # [Phase 2] FTS パネル
    QueryHistoryViewModel.cs     # [Phase 2] 履歴パネル
  Views/
    MainWindow.axaml             # DockPanel: 左サイドバー (接続+スキーマ) + 中央 + 下部
    QueryEditor.axaml            # AvaloniaEdit コントロール
    ResultsView.axaml            # DataGrid
    GraphCanvas.axaml            # SkiaSharp キャンバス
    GraphCanvasPanel.cs          # カスタム描画 Control
    PropertyInspector.axaml      # KeyValue リスト
    FullTextSearchPanel.axaml    # [Phase 2] FTS パネル
    QueryHistoryPanel.axaml      # [Phase 2] 履歴パネル
    AddNodeDialog.axaml          # [Phase 2] ノード追加ダイアログ
    AddRelationshipDialog.axaml  # [Phase 2] Rel 追加ダイアログ
  Models/
    VisualNode.cs / VisualEdge.cs / SchemaTreeNode.cs
    QueryResult.cs / ScriptGlobals.cs
    StudioSettings.cs            # [Phase 2] 永続化 POCO
    FtsResultRow.cs              # [Phase 2] FTS 結果行
    RoslynCompletionData.cs      # [Phase 2] ICompletionData 実装
  Services/
    DatabaseService.cs           # GraphDatabase ライフサイクル + ReactiveProperty
    QueryExecutionService.cs     # Roslyn CSharpScript 実行ブリッジ
    GraphLayoutService.cs        # Fruchterman-Reingold 力指向レイアウト
    SettingsService.cs           # [Phase 2] JSON 永続化
    SugiyamaLayoutService.cs     # [Phase 2] 階層レイアウト
    GraphEditingService.cs       # [Phase 2] Write tx ラッパー
    IntellisenseService.cs       # [Phase 2] Roslyn 補完
  Converters/
    FileSizeConverter.cs
  Rendering/
    GraphRenderer.cs / HitTestHelper.cs / CameraTransform.cs
  Export/
    SvgExporter.cs               # [Phase 2] SVG 生成
    PngExporter.cs               # [Phase 2] PNG 生成
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

### Phase 1e: プロパティインスペクタ ✅
- PropertyInspectorViewModel (InspectNode/InspectEdge/Clear) ✅
- PropertyValue ref struct → string 実体化 (全型対応: Bool/Int32/Int64/Double/String/Bytes/FloatArray) ✅
- グラフキャンバス ノード選択 → PropertyInspector 更新 ✅
- DataGrid 行選択 → NodeId 抽出 → PropertyInspector 更新 ✅
- 右サイドバー表示 (選択時のみ表示) ✅
- ノード: EnumerateProperties で全プロパティ列挙 + KeyId→名前逆引き ✅
- リレーションシップ: ListPropertyKeys + GetProperty/GetPropertyValues で全キー走査 ✅

### Phase 1f: ステータスバー + 仕上げ ✅
- ステータスバー: 接続状態・クエリステータス・グラフ統計・ズーム率 ✅
- キーボードショートカット (F5, Ctrl+O, Ctrl+W) ✅
- Dark/Light テーマ切替 (FluentTheme + TextMate + グラフキャンバス連動) ✅
- エッジ (リレーションシップ) クリック選択 + ハイライト + PropertyInspector 連携 ✅
- DB 切断時の全パネルクリア ✅

### Phase 2: 操作・分析ツール化

Studio を「閲覧ツール」から「操作・分析ツール」に引き上げる。

#### 実装順序と依存関係

```
Phase 2a (基盤 — 独立、先行して安定化)
  2a.1 セッション永続化 ← 2b.1 が依存
  2a.2 全文検索パネル   (独立)
  2a.3 Sugiyama レイアウト (独立)

Phase 2b (2a 基盤の上に構築)
  2b.1 クエリ履歴 UI   ← 2a.1 SettingsService に依存
  2b.2 グラフ編集       (最も侵襲的、2a 安定後に着手)
  2b.3 ベクトルスコア可視化 (独立、中程度の複雑さ)

Phase 2c (高複雑度 / 独立)
  2c.1 Roslyn IntelliSense (最高複雑度)
  2c.2 SVG/PNG エクスポート (独立)
```

#### Phase 2a.1: セッション永続化基盤

JSON ファイルで設定・MRU・クエリ履歴を永続化。SQLite 依存なし。

**保存先**: `%LOCALAPPDATA%/QuiverStudio/settings.json`

**データモデル**:
```csharp
// Models/StudioSettings.cs
public sealed class StudioSettings
{
    public int Version { get; set; } = 1;
    public string Theme { get; set; } = "Light";
    public string? LastOpenedPath { get; set; }
    public WindowStateData? WindowState { get; set; }
    public List<RecentFileEntry> RecentFiles { get; set; } = [];   // 最大 20 件 FIFO
    public List<QueryHistoryEntry> QueryHistory { get; set; } = []; // 最大 200 件 FIFO
    public int MaxHistoryEntries { get; set; } = 200;
}

public sealed class RecentFileEntry
{
    public required string Path { get; set; }
    public DateTime LastOpened { get; set; }
}

public sealed class QueryHistoryEntry
{
    public required string Code { get; set; }
    public DateTime Timestamp { get; set; }
    public double DurationMs { get; set; }
    public bool WasError { get; set; }
    public string? DbPath { get; set; }
}

public sealed class WindowStateData
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 800;
    public bool IsMaximized { get; set; }
}
```

**SettingsService 設計**: Singleton。System.Text.Json で読み書き。500ms Timer デバウンス保存。`Window.Closing` で `SaveImmediate()`。

**新規ファイル**: `Models/StudioSettings.cs`, `Services/SettingsService.cs`
**変更ファイル**: `Program.cs` (DI), `MainWindowViewModel.cs` (MRU・テーマ永続化), `QueryEditorViewModel.cs` (履歴記録), `MainWindow.axaml.cs` (ウィンドウ状態保存/復元)

#### Phase 2a.2: 全文検索パネル

FTS インデックスを選択してテキスト検索し、結果をグラフキャンバスに表示。

**配置**: 下部 TabControl 新タブ「Search」(Results/Graph/Output と並列)。

**ViewModel**:
```csharp
// ViewModels/FullTextSearchViewModel.cs
public partial class FullTextSearchViewModel : ObservableObject
{
    [ObservableProperty] IReadOnlyList<FullTextIndexInfo> indexes;
    [ObservableProperty] FullTextIndexInfo? selectedIndex;
    [ObservableProperty] string queryText = "";
    [ObservableProperty] int maxResults = 20;
    [ObservableProperty] IReadOnlyList<FtsResultRow> results;
    [ObservableProperty] bool isSearching;

    [RelayCommand] async Task SearchAsync();
    public event Action<List<NodeId>>? SearchResultReady;
}
```

**API**: `g.Search(indexName, queryText, k).ToList()` → NodeId リスト。スコアは非伝播 (MVP)。
結果行クリック → PropertyInspector 連携 (既存パターン流用)。

**新規**: `ViewModels/FullTextSearchViewModel.cs`, `Views/FullTextSearchPanel.axaml` + `.axaml.cs`, `Models/FtsResultRow.cs`
**変更**: `MainWindowViewModel.cs`, `MainWindow.axaml`, `GraphCanvasViewModel.cs` (`BuildFromNodeIds` 追加)

#### Phase 2a.3: Sugiyama 階層レイアウト

力指向と階層レイアウトのトグル切替。自前実装 (外部ライブラリ依存回避)。

**アルゴリズム** (推定 250 行):
1. サイクル除去 — DFS で back edge 検出・一時反転
2. レイヤー割り当て — Longest-path layering
3. 交差最小化 — Barycenter ヒューリスティック (2–3 パス)
4. 座標割り当て — レイヤー間隔 120px、レイヤー内間隔 80px、中央揃え

```csharp
// Services/SugiyamaLayoutService.cs — Singleton
public sealed class SugiyamaLayoutService
{
    public void Layout(IReadOnlyList<VisualNode> nodes, IReadOnlyList<VisualEdge> edges);
    // FindComponents + 各コンポーネントに 4 step + PackComponents
}
```

**UI**: グラフキャンバス上部トグルボタン「Force」/「Hierarchy」。
ピン制約は Sugiyama では無視 (階層配置は全位置を制御)。切替時は即時再レイアウト。

**新規**: `Services/SugiyamaLayoutService.cs`
**変更**: `Program.cs` (DI), `GraphCanvasViewModel.cs` (`IsHierarchicalLayout` トグル), `Views/GraphCanvas.axaml` (トグルボタン)

#### Phase 2b.1: クエリ履歴 UI

過去のクエリを一覧表示、ダブルクリックでエディタに再ロード。2a.1 `SettingsService` に依存。

**配置**: 下部 TabControl 新タブ「History」。

```csharp
// ViewModels/QueryHistoryViewModel.cs
public partial class QueryHistoryViewModel : ObservableObject
{
    [ObservableProperty] ObservableCollection<QueryHistoryEntry> entries;
    [ObservableProperty] string filterText = "";

    [RelayCommand] void LoadEntry(QueryHistoryEntry entry);
    [RelayCommand] void ClearHistory();
    [RelayCommand] void DeleteEntry(QueryHistoryEntry entry);

    public event Action<string>? LoadRequested;
}
```

**UI**: ListBox — タイムスタンプ、コード先頭 100 文字、実行時間、エラーフラグ。最新が上。テキストフィルタ。

**新規**: `ViewModels/QueryHistoryViewModel.cs`, `Views/QueryHistoryPanel.axaml` + `.axaml.cs`
**変更**: `MainWindowViewModel.cs`, `MainWindow.axaml`, `MainWindow.axaml.cs` (LoadRequested → TextEditor)

#### Phase 2b.2: インタラクティブグラフ編集

キャンバス上でノード/リレーションシップの GUI 追加・削除。

**トランザクションモデル**: 既存 `QueryExecutionService` (読み取り専用 tx) とは**別経路**の `GraphEditingService` を新設。操作ごとに write tx → 即 commit (auto-commit per operation)。

```csharp
// Services/GraphEditingService.cs — Singleton
public sealed class GraphEditingService
{
    // 各メソッド: BeginTransaction → 操作 → Commit (失敗時 Rollback)
    // 成功後 DatabaseService.RefreshStatistics()
    public NodeId CreateNode(string label, IReadOnlyList<(string key, string value)>? properties = null);
    public void DeleteNode(NodeId id);
    public RelationshipId CreateRelationship(NodeId source, NodeId target, string type);
    public void DeleteRelationship(RelationshipId id);
    public void SetProperty(NodeId id, string key, string value);
    public void RemoveProperty(NodeId id, string key);
}
```

**操作フロー**:
- 右クリック コンテキストメニュー:
  - キャンバス空白 → 「Add Node...」
  - ノード上 → 「Delete Node」「Add Relationship from here...」「Edit Properties...」
  - エッジ上 → 「Delete Relationship」
- リレーション作成: ソース選択 → `IsLinkMode = true` → ソースからカーソルへ破線描画 → ターゲットクリック → RelType ダイアログ → 作成。ESC キャンセル。
- 編集後: Nodes/Edges リストに直接追加 → レイアウト再実行 → GraphChanged

**HitTestHelper 拡張**: エッジ hit test 追加 (点-線分距離、tolerance 6px)。

**新規**: `Services/GraphEditingService.cs`, `Views/AddNodeDialog.axaml` + `.axaml.cs`, `Views/AddRelationshipDialog.axaml` + `.axaml.cs`
**変更**: `Program.cs`, `GraphCanvasViewModel.cs` (編集コマンド・LinkMode), `GraphCanvasPanel.cs` (右クリック・コンテキストメニュー), `GraphRenderer.cs` (LinkMode 破線), `HitTestHelper.cs` (エッジ hit test), `VisualEdge.cs` (`IsSelected`), `MainWindowViewModel.cs`

#### Phase 2b.3: ベクトル検索スコア可視化

ベクトル検索結果にスコアを付けてグラフ上で視覚表現。

**API 制約**:
- `g.Knn()` → `GraphTraversal<NodeId>` (スコア非伝播)。スコア取得は `db.Vectors.KnnSearch()` 直接呼び出しのみ。
- `VectorSearchResult` = `readonly record struct(EntityKind, long EntityId, float Score)`

**スコア捕捉**: `MaterializeEnumerable` で `VectorSearchResult` 型を検出し、NodeId + Score を抽出。

```csharp
// QueryExecutionService.cs に分岐追加
if (firstNonNull is VectorSearchResult)
{
    // nodeIds + scores 抽出 → QueryResult に VectorScores を付与
}
```

**QueryResult 拡張**: `IReadOnlyDictionary<long, float>? VectorScores`
**VisualNode 拡張**: `float? VectorScore`, `float? NormalizedScore` (0.0–1.0 min-max)

**描画**: スコアがある場合:
1. 不透明度: `0.3 + 0.7 × normalizedScore`
2. 半径: `baseRadius × (0.7 + 0.6 × normalizedScore)`
3. スコアバッジ: ノード下部に 9px で表示 (例: "0.92")

**変更**: `QueryExecutionService.cs`, `QueryResult.cs`, `VisualNode.cs`, `GraphCanvasViewModel.cs`, `GraphRenderer.cs`

#### Phase 2c.1: Roslyn IntelliSense

クエリエディタでドット補完と Ctrl+Space 補完。

**NuGet 追加**: `Microsoft.CodeAnalysis.CSharp.Features 4.*`

**アーキテクチャ**:
```csharp
// Services/IntellisenseService.cs — Singleton
public sealed class IntellisenseService : IDisposable
{
    private AdhocWorkspace _workspace;
    private ProjectId _projectId;

    public async Task<IReadOnlyList<CompletionEntry>> GetCompletionsAsync(
        string code, int caretPosition, CancellationToken ct);
}
```

**コードラッピング**: ユーザコードを ScriptGlobals メンバーがアクセス可能な形にラップ:
```csharp
using System; using System.Linq; using System.Collections.Generic;
using Quiver; using Quiver.Core; using Quiver.Api;
using Quiver.Transactions; using Quiver.Storage.Records;

public static GraphDatabase db => default!;
public static IGraphTransaction tx => default!;
public static GraphTraversalSource g => default!;
public static ISchemaApi schema => default!;

{userCode}
```

preamble 長をオフセットとして保持し、Roslyn の補完位置を調整。

**AvaloniaEdit 統合**: `TextArea.TextEntered` ('.' で補完トリガー) + `TextArea.KeyDown` (Ctrl+Space) → `CompletionWindow` に `ICompletionData` 実装を渡す。

**パフォーマンス**: Workspace は起動時に 1 回構築、DB 変更時に再構築。150ms デバウンス ('.' は即時)。バックグラウンドスレッド + CancellationToken。

**新規**: `Services/IntellisenseService.cs`, `Models/RoslynCompletionData.cs`
**変更**: `Quiver.Studio.csproj`, `Program.cs`, `MainWindow.axaml.cs` (CompletionWindow)

#### Phase 2c.2: SVG/PNG エクスポート

グラフキャンバスの内容をファイルに書き出す。

**SVG**: 自前 XML 生成。ワールド座標で出力 (カメラ変換なし)。viewBox を bounding box + 40px margin で設定。`<defs>` に arrowhead marker、`<line>` + `<circle>` + `<text>` で描画。

```csharp
// Export/SvgExporter.cs
public static class SvgExporter
{
    public static string Export(
        IReadOnlyList<VisualNode> nodes, IReadOnlyList<VisualEdge> edges, bool isDarkTheme);
}
```

**PNG**: Avalonia `RenderTargetBitmap` + `GraphRenderer.Render()` でオフスクリーン描画。2x スケール。

```csharp
// Export/PngExporter.cs
public static class PngExporter
{
    public static async Task ExportAsync(
        IReadOnlyList<VisualNode> nodes, IReadOnlyList<VisualEdge> edges,
        GraphRenderer renderer, string outputPath, bool isDarkTheme, double scale = 2.0);
}
```

**UI**: グラフタブ上部ツールバーに「Export SVG」「Export PNG」。SaveFileDialog。

**新規**: `Export/SvgExporter.cs`, `Export/PngExporter.cs`
**変更**: `GraphCanvasViewModel.cs` (エクスポートコマンド), `Views/GraphCanvas.axaml` (ボタン)

### Phase 2 設計判断サマリ

| 判断 | 選択 | 理由 |
|------|------|------|
| 永続化形式 | JSON (System.Text.Json) | データ量小、SQLite 依存不要、デバッグ容易 |
| Sugiyama | 自前実装 | 外部ライブラリ依存回避 (Quiver.csproj のみ制約)、250 行程度 |
| グラフ編集 tx | 操作ごと auto-commit | 長時間 write tx によるエンジンブロック回避 |
| 編集 UI | 右クリック コンテキストメニュー | フローティングツールバーより侵襲性低い |
| ベクトルスコア取得 | MaterializeEnumerable で VectorSearchResult 型検出 | g.Knn() はスコア非伝播 |
| IntelliSense | AdhocWorkspace + CompletionService | 最もロバストな Roslyn 補完パス |
| SVG 生成 | 自前 XML | 外部ライブラリ不要、DOM は単純 |
| PNG 生成 | RenderTargetBitmap | Avalonia 標準 API |

### Phase 2 新規ファイル一覧

| # | パス | 役割 |
|---|------|------|
| 1 | `Models/StudioSettings.cs` | 永続化 POCO |
| 2 | `Services/SettingsService.cs` | JSON 読み書き + デバウンス保存 |
| 3 | `ViewModels/FullTextSearchViewModel.cs` | FTS パネル VM |
| 4 | `Models/FtsResultRow.cs` | FTS 結果行 |
| 5 | `Views/FullTextSearchPanel.axaml` + `.axaml.cs` | FTS パネル UI |
| 6 | `Services/SugiyamaLayoutService.cs` | 階層レイアウトアルゴリズム |
| 7 | `ViewModels/QueryHistoryViewModel.cs` | 履歴パネル VM |
| 8 | `Views/QueryHistoryPanel.axaml` + `.axaml.cs` | 履歴パネル UI |
| 9 | `Services/GraphEditingService.cs` | Write tx ラッパー |
| 10 | `Views/AddNodeDialog.axaml` + `.axaml.cs` | ノード追加ダイアログ |
| 11 | `Views/AddRelationshipDialog.axaml` + `.axaml.cs` | Rel 追加ダイアログ |
| 12 | `Services/IntellisenseService.cs` | Roslyn 補完エンジン |
| 13 | `Models/RoslynCompletionData.cs` | ICompletionData 実装 |
| 14 | `Export/SvgExporter.cs` | SVG 生成 |
| 15 | `Export/PngExporter.cs` | PNG 生成 |

## 検証方法

### Phase 1 検証
1. `dotnet build Quiver.slnx` で全プロジェクトビルド成功
2. `dotnet run --project tools/Quiver.Studio` でウィンドウ起動
3. サンプル DB を開いてスキーマツリー表示確認
4. クエリ実行→結果表示→グラフ可視化の E2E 動作
5. ノード/エッジクリック→プロパティインスペクタ表示
6. パン/ズーム/ノードドラッグの操作性

### Phase 2 検証
- **2a.1 永続化**: DB 開閉 → 再起動 → MRU 表示。テーマ/ウィンドウ状態の復元。
- **2a.2 全文検索**: FTS DB → Search タブ → インデックス選択 → テキスト検索 → グラフ表示。結果行 → PropertyInspector。
- **2a.3 Sugiyama**: グラフ表示 → Hierarchy トグル → レイヤー状配置 → Force に戻る。
- **2b.1 履歴**: クエリ実行 → History タブ → エントリ確認 → ダブルクリックでエディタ反映 → 再起動後も残存。
- **2b.2 編集**: 空白右クリック → Add Node → 出現。ノード右クリック → Delete/Add Rel。エッジ右クリック → Delete。PropertyInspector 確認。
- **2b.3 スコア**: `db.Vectors.KnnSearch(...)` 実行 → グラフでサイズ/不透明度変化 → スコアバッジ表示。
- **2c.1 IntelliSense**: `g.` → 補完ウィンドウ。`tx.` → メソッド一覧。Ctrl+Space。
- **2c.2 エクスポート**: Export SVG → ブラウザ確認。Export PNG → 画像ビューア確認。
