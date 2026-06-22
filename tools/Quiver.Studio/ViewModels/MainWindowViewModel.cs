using Quiver.Studio.Services;

namespace Quiver.Studio.ViewModels;

public sealed class MainWindowViewModel
{
    private readonly DatabaseService _databaseService;

    public MainWindowViewModel(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public string Title => "Quiver Studio";
}
