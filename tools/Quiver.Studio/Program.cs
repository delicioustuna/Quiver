using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quiver.Studio.Services;
using Quiver.Studio.ViewModels;

namespace Quiver.Studio;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(ConfigureServices)
            .Build();

        SetupGlobalExceptionHandlers(host.Services);

        BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(args);
    }

    private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<QueryExecutionService>();
        services.AddSingleton<GraphLayoutService>();
        services.AddTransient<MainWindowViewModel>();
    }

    private static AppBuilder BuildAvaloniaApp(IServiceProvider services)
        => AppBuilder.Configure(() => new App(services))
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
