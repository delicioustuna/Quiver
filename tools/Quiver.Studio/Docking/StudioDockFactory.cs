using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Quiver.Studio.Resources;
using Quiver.Studio.ViewModels;
using InpcFactory = Dock.Model.Inpc.Factory;

namespace Quiver.Studio.Docking;

/// <summary>
/// Builds the dock layout using Dock.Model.Inpc types and FluentExtensions.
/// <para>
/// Key design constraints for Dock.Avalonia v12 + Dock.Model.Inpc:
/// <list type="bullet">
///   <item>Base class must be <c>Dock.Model.Inpc.Factory</c> (not <c>FactoryBase</c>)
///         — provides INPC-aware Create* methods and avoids StackOverflow from
///         missing PropertyChanged on container properties.</item>
///   <item><c>RootDock.ActiveDockable</c> must point to the top-level
///         <c>ProportionalDock</c> (the full layout), not a child dock.
///         <c>RootDockControl</c> only renders <c>ActiveDockable</c>.</item>
///   <item>View content flows through <c>IDockable.Context</c> — set a wrapper
///         model (e.g. <c>SchemaToolModel</c>) as Context, then bridge via
///         DataTemplate in App.axaml: <c>Document/Tool → ContentControl
///         Content="{Binding Context}" → per-model DataTemplate</c>.</item>
///   <item><c>DockControl.Factory</c> must be assigned before
///         <c>DockControl.Layout</c>, and <c>InitLayout</c> called explicitly.</item>
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

        // ActiveDockable must be mainLayout (the ProportionalDock containing all panels).
        // RootDockControl renders only ActiveDockable — setting a child dock here
        // would hide the rest of the layout.
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
