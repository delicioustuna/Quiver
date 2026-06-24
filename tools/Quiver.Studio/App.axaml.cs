using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Quiver.Studio.Services;
using Quiver.Studio.ViewModels;
using Quiver.Studio.Views;

namespace Quiver.Studio;

public partial class App : Application
{
    private readonly IServiceProvider? _services;

    // プレビューアが起動するときは Main メソッドを通過しないため、ServiceProvider は null になる。
    // そのため、App クラス側で null が渡されても落ちないように（デザインモード用の処理に分岐できるように）しておく必要がある。
    public App(IServiceProvider? services = null)
    {
        _services = services;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // プレビューア（デザインモード）のときの処理
        if (Design.IsDesignMode)
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime designModeDesktop)
            {
                // デザイン用にはDIを使わず、プレビュー専用のViewModelを渡すか、空で生成する
                designModeDesktop.MainWindow = new MainWindow();
            }
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // 通常実行時の処理（null にはならないので _services! で実体を強制）
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow
            {
                DataContext = _services!.GetRequiredService<MainWindowViewModel>(),
            };
            mainWindow.Initialize(_services!.GetRequiredService<SettingsService>());
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
