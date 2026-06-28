using Quiver.Studio.ViewModels;

namespace Quiver.Studio.Docking;

public sealed class SchemaToolModel(SchemaBrowserViewModel viewModel)
{
    public SchemaBrowserViewModel ViewModel { get; } = viewModel;
}

public sealed class StatsToolModel(MainWindowViewModel viewModel)
{
    public MainWindowViewModel ViewModel { get; } = viewModel;
}

public sealed class QueryEditorDocModel(QueryEditorViewModel viewModel)
{
    public QueryEditorViewModel ViewModel { get; } = viewModel;
}

public sealed class FtsDocModel(FullTextSearchViewModel viewModel)
{
    public FullTextSearchViewModel ViewModel { get; } = viewModel;
}

public sealed class SettingsDocModel(GraphSettingsViewModel viewModel)
{
    public GraphSettingsViewModel ViewModel { get; } = viewModel;
}

public sealed class ResultsToolModel(ResultsViewModel viewModel)
{
    public ResultsViewModel ViewModel { get; } = viewModel;
}

public sealed class GraphToolModel(GraphCanvasViewModel viewModel)
{
    public GraphCanvasViewModel ViewModel { get; } = viewModel;
}

public sealed class OutputToolModel(QueryEditorViewModel viewModel)
{
    public QueryEditorViewModel ViewModel { get; } = viewModel;
}

public sealed class HistoryToolModel(QueryHistoryViewModel viewModel)
{
    public QueryHistoryViewModel ViewModel { get; } = viewModel;
}

public sealed class PropertiesToolModel(PropertyInspectorViewModel viewModel)
{
    public PropertyInspectorViewModel ViewModel { get; } = viewModel;
}

public sealed class ApiDocToolModel(ApiDocumentationViewModel viewModel)
{
    public ApiDocumentationViewModel ViewModel { get; } = viewModel;
}
