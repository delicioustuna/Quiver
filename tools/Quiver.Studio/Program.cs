using System.Globalization;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quiver.Studio.Resources;
using Quiver.Studio.Services;
using Quiver.Studio.ViewModels;

namespace Quiver.Studio;

public static class Program
{
    public static IServiceProvider? ServiceProvider { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--diag-layout")
        {
            var dbPath = args.Length > 1 ? args[1]
                : Path.Combine(AppContext.BaseDirectory, "sample.quiver");
            DiagLayout.Run(dbPath);
            return;
        }

        using var mutex = new Mutex(true, "Quiver.Studio.SingleInstance", out var createdNew);
        if (!createdNew)
            return;

        var logPath = Path.Combine(AppContext.BaseDirectory, "studio.log");

        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(new FileLoggerProvider(logPath));
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        services.AddSingleton<SettingsService>();
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<QueryExecutionService>();
        services.AddSingleton<GraphLayoutService>();
        services.AddSingleton<SugiyamaLayoutService>();
        services.AddSingleton<GraphEditingService>();
        services.AddSingleton<IntellisenseService>();
        services.AddTransient<MainWindowViewModel>();

        using var sp = services.BuildServiceProvider();

        SetupGlobalExceptionHandlers(sp);

        var settingsService = sp.GetRequiredService<SettingsService>();
        var lang = settingsService.Settings.Language;
        if (lang is not "auto")
        {
            var culture = new CultureInfo(lang);
            Strings.Culture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }

        ServiceProvider = sp;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure(() => new App(ServiceProvider))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void SetupGlobalExceptionHandlers(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalExceptionHandler");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            if (e.IsTerminating)
                logger.LogCritical(ex, "致命的な未処理例外が発生しました。アプリケーションを終了します。");
            else
                logger.LogError(ex, "未処理例外が発生しました。");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "未観測の Task 例外が発生しました。");
            e.SetObserved();
        };

        R3.ObservableSystem.RegisterUnhandledExceptionHandler(ex =>
        {
            logger.LogError(ex, "R3 サブスクリプション内で未処理例外が発生しました。");
        });
    }
}
