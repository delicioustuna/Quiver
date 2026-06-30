using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Quiver.Studio.Resources;
using Quiver.Studio.ViewModels;
using InpcFactory = Dock.Model.Inpc.Factory;

namespace Quiver.Studio.Docking;

/// <summary>
/// Dock.Model.Inpc の型と FluentExtensions を使ってドックレイアウトを構築する。
/// <para>
/// Dock.Avalonia v12 と Dock.Model.Inpc を組み合わせる際の制約:
/// <list type="bullet">
///   <item>基底クラスには <c>FactoryBase</c> ではなく <c>Dock.Model.Inpc.Factory</c> を使う。
///         INPC 対応の Create* メソッドが提供され、コンテナプロパティの PropertyChanged 欠落による
///         StackOverflow を避けられる。</item>
///   <item><c>RootDock.ActiveDockable</c> は子ドックではなく、レイアウト全体を表す最上位の
///         <c>ProportionalDock</c> を指す必要がある。
///         <c>RootDockControl</c> は <c>ActiveDockable</c> だけを描画する。</item>
///   <item>ビューの内容は <c>IDockable.Context</c> 経由で渡す。Context に
///         <c>SchemaToolModel</c> などのラッパーモデルを設定し、App.axaml の DataTemplate で
///         <c>Document/Tool → ContentControl Content="{Binding Context}" →
///         モデル別 DataTemplate</c> と接続する。</item>
///   <item><c>DockControl.Layout</c> より先に <c>DockControl.Factory</c> を設定し、
///         <c>InitLayout</c> を明示的に呼び出す。</item>
/// </list>
/// </para>
/// </summary>
public sealed class StudioDockFactory : InpcFactory
{
    private IRootDock? _rootDock;
    private readonly Dictionary<string, (IDockable Dockable, IDock OriginalOwner)> _panelRegistry = new();

    public IReadOnlyDictionary<string, (IDockable Dockable, IDock OriginalOwner)> Panels => _panelRegistry;

    public override IRootDock CreateLayout() => _rootDock ?? CreateRootDock();

    public IRootDock CreateLayout(MainWindowViewModel vm)
    {
        var schemaTool = this.Tool(t =>
        {
            t.Id = "schema";
            t.Title = Strings.Panel_Schema;
            t.Context = new SchemaToolModel(vm.SchemaBrowser);
            t.CanClose = false;
            t.CanFloat = true;
            t.CanPin = true;
        });

        var statsTool = this.Tool(t =>
        {
            t.Id = "stats";
            t.Title = Strings.Panel_Statistics;
            t.Context = new StatsToolModel(vm);
            t.CanClose = false;
            t.CanFloat = true;
            t.CanPin = true;
        });

        var queryDoc = this.Document(d =>
        {
            d.Id = "queryEditor";
            d.Title = Strings.Panel_QueryEditor;
            d.Context = new QueryEditorDocModel(vm.QueryEditor);
            d.CanClose = false;
            d.CanFloat = true;
        });

        var ftsDoc = this.Document(d =>
        {
            d.Id = "fts";
            d.Title = Strings.Panel_FullTextSearch;
            d.Context = new FtsDocModel(vm.FullTextSearch);
            d.CanClose = true;
            d.CanFloat = true;
        });

        var resultsTool = this.Tool(t =>
        {
            t.Id = "results";
            t.Title = Strings.Panel_Results;
            t.Context = new ResultsToolModel(vm.Results);
            t.CanClose = false;
            t.CanFloat = true;
            t.CanPin = true;
        });

        var graphTool = this.Tool(t =>
        {
            t.Id = "graph";
            t.Title = Strings.Panel_Graph;
            t.Context = new GraphToolModel(vm.GraphCanvas);
            t.CanClose = false;
            t.CanFloat = true;
            t.CanPin = true;
        });

        var outputTool = this.Tool(t =>
        {
            t.Id = "output";
            t.Title = Strings.Panel_Output;
            t.Context = new OutputToolModel(vm.QueryEditor);
            t.CanClose = false;
            t.CanFloat = true;
            t.CanPin = true;
        });

        var historyTool = this.Tool(t =>
        {
            t.Id = "history";
            t.Title = Strings.Panel_History;
            t.Context = new HistoryToolModel(vm.QueryHistory);
            t.CanClose = false;
            t.CanFloat = true;
            t.CanPin = true;
        });

        var propertiesTool = this.Tool(t =>
        {
            t.Id = "properties";
            t.Title = Strings.Panel_Properties;
            t.Context = new PropertiesToolModel(vm.PropertyInspector);
            t.CanClose = false;
            t.CanFloat = true;
            t.CanPin = true;
        });

        var leftToolDock = this.ToolDock(Alignment.Left, td =>
        {
            td.Id = "leftTools";
            td.Title = "Left";
            td.Proportion = 0.20;
            td.VisibleDockables = CreateList<IDockable>(schemaTool, statsTool);
        });

        var documentDock = this.DocumentDock(dd =>
        {
            dd.Id = "documents";
            dd.Title = "Documents";
            dd.Proportion = 0.55;
            dd.VisibleDockables = CreateList<IDockable>(queryDoc, ftsDoc);
            dd.CanCreateDocument = false;
        });

        var bottomToolDock = this.ToolDock(Alignment.Bottom, td =>
        {
            td.Id = "bottomTools";
            td.Title = "Bottom";
            td.Proportion = 0.45;
            td.VisibleDockables = CreateList<IDockable>(resultsTool, graphTool, outputTool, historyTool);
        });

        var centerVertical = this.ProportionalDock(Orientation.Vertical, pd =>
        {
            pd.Id = "centerVertical";
            pd.Title = "Center";
            pd.Proportion = double.NaN;
            pd.VisibleDockables = CreateList<IDockable>(
                documentDock,
                this.ProportionalDockSplitter(),
                bottomToolDock);
        });

        var apiDocTool = this.Document(d =>
        {
            d.Id = "apiDoc";
            d.Title = Strings.Panel_ApiDoc;
            d.Context = new ApiDocToolModel(vm.ApiDocumentation);
            d.CanClose = true;
            d.CanFloat = true;
        });

        var settingsDoc = this.Document(d =>
        {
            d.Id = "settings";
            d.Title = Strings.Panel_Settings;
            d.Context = new SettingsDocModel(vm.GraphSettings);
            d.CanClose = true;
            d.CanFloat = true;
        });

        var rightToolDock = this.ToolDock(Alignment.Right, td =>
        {
            td.Id = "rightTools";
            td.Title = "Right";
            td.Proportion = 0.20;
            td.VisibleDockables = CreateList<IDockable>(propertiesTool);
        });

        var mainLayout = this.ProportionalDock(Orientation.Horizontal, pd =>
        {
            pd.Id = "mainLayout";
            pd.Title = "Main";
            pd.VisibleDockables = CreateList<IDockable>(
                leftToolDock,
                this.ProportionalDockSplitter(),
                centerVertical,
                this.ProportionalDockSplitter(),
                rightToolDock);
        });

        // RootDockControl は ActiveDockable だけを描画するため、全パネルを含む
        // ProportionalDock の mainLayout を指定する。子ドックを指定すると残りが非表示になる。
        _rootDock = this.RootDock(r =>
        {
            r.Id = "root";
            r.Title = "Root";
            r.ActiveDockable = mainLayout;
            r.DefaultDockable = mainLayout;
            r.VisibleDockables = CreateList<IDockable>(mainLayout);
        });

        _panelRegistry["schema"] = (schemaTool, leftToolDock);
        _panelRegistry["stats"] = (statsTool, leftToolDock);
        _panelRegistry["queryEditor"] = (queryDoc, documentDock);
        _panelRegistry["fts"] = (ftsDoc, documentDock);
        _panelRegistry["results"] = (resultsTool, bottomToolDock);
        _panelRegistry["graph"] = (graphTool, bottomToolDock);
        _panelRegistry["output"] = (outputTool, bottomToolDock);
        _panelRegistry["history"] = (historyTool, bottomToolDock);
        _panelRegistry["properties"] = (propertiesTool, rightToolDock);
        _panelRegistry["apiDoc"] = (apiDocTool, documentDock);
        _panelRegistry["settings"] = (settingsDoc, documentDock);

        return _rootDock;
    }

    public void TogglePanel(string panelId)
    {
        if (!_panelRegistry.TryGetValue(panelId, out var entry))
            return;

        if (entry.Dockable.Owner is IDock owner
            && owner.VisibleDockables?.Contains(entry.Dockable) == true)
        {
            RemoveDockable(entry.Dockable, collapse: true);
        }
        else
        {
            AddDockable(entry.OriginalOwner, entry.Dockable);
            SetActiveDockable(entry.Dockable);
        }
    }

    public bool IsPanelVisible(string panelId)
    {
        if (!_panelRegistry.TryGetValue(panelId, out var entry))
            return false;

        return entry.Dockable.Owner is IDock owner
            && owner.VisibleDockables?.Contains(entry.Dockable) == true;
    }
}
