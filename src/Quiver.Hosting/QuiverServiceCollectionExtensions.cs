using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Quiver.Hosting;

/// <summary>
/// <see cref="IServiceCollection"/> に Quiver の <see cref="GraphStore"/> シングルトンを
/// 登録するヘルパ。<c>Microsoft.Extensions.Configuration</c> 経由で appsettings.json / 環境変数から
/// 設定を読み取り、ASP.NET Core / .NET Generic Host の DI コンテナと統合する。
/// </summary>
/// <remarks>
/// 想定セクション名は <c>"Quiver"</c>。<see cref="QuiverConfigurationOptions"/> の各プロパティが
/// そのままキーになる。コンテナ破棄時に <see cref="GraphStore.Dispose"/> が呼ばれる。
/// </remarks>
public static class QuiverServiceCollectionExtensions
{
    /// <summary>
    /// <paramref name="configuration"/> セクション (例: <c>config.GetSection("Quiver")</c>) を
    /// <see cref="QuiverConfigurationOptions"/> に bind し、<see cref="GraphStore"/> を
    /// シングルトンとして登録する。
    /// </summary>
    /// <param name="services">対象の DI コンテナ。</param>
    /// <param name="configuration">Quiver セクションを表す <see cref="IConfiguration"/>。</param>
    /// <param name="postConfigure">
    /// bind 後の <see cref="GraphStoreOptions"/> をさらに編集するための任意フック。
    /// </param>
    public static IServiceCollection AddQuiver(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<GraphStoreOptions>? postConfigure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<QuiverConfigurationOptions>().Bind(configuration);
        RegisterGraphStore(services, postConfigure);
        return services;
    }

    /// <summary>
    /// プログラム的に <see cref="QuiverConfigurationOptions"/> を構成する版。テストや、
    /// 設定ソースを持たない短命プロセスから利用する。
    /// </summary>
    public static IServiceCollection AddQuiver(
        this IServiceCollection services,
        Action<QuiverConfigurationOptions> configure,
        Action<GraphStoreOptions>? postConfigure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<QuiverConfigurationOptions>().Configure(configure);
        RegisterGraphStore(services, postConfigure);
        return services;
    }

    private static void RegisterGraphStore(
        IServiceCollection services,
        Action<GraphStoreOptions>? postConfigure)
    {
        services.TryAddSingleton(
            sp => new QuiverEventLoggerBridge(sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddSingleton<GraphStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<QuiverConfigurationOptions>>().Value;
            if (string.IsNullOrWhiteSpace(opts.DataDirectory))
            {
                throw new InvalidOperationException(
                    "Quiver:DataDirectory が空です。appsettings.json または環境変数で "
                    + "データベースディレクトリの絶対パスを指定してください。");
            }

            // DB を開く前に listener を有効化し、起動中の EventSource イベントも ILogger へ流す。
            _ = sp.GetRequiredService<QuiverEventLoggerBridge>();
            var storeOptions = opts.ToGraphStoreOptions();
            postConfigure?.Invoke(storeOptions);
            // Quiver は単一ファイル (*.quiver) のため、DataDirectory 配下の graph.quiver を開く。
            return GraphStore.Open(
                System.IO.Path.Combine(opts.DataDirectory, "graph.quiver"), storeOptions);
        });
    }
}
