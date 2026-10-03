using System.IO;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.App.ViewModels;
using MasterDuelSwitcher.App.Views;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MasterDuelSwitcher.App;

/// <summary>应用组合入口，以 .NET 依赖注入连接可替换服务、视图模型与窗口。</summary>
public static class Bootstrapper
{
    /// <summary>构建真实应用服务；状态目录默认位于系统 LocalApplicationData。</summary>
    public static ServiceProvider Build(string[] args)
    {
        var environment = new WindowsApplicationEnvironment();
        var stateDirectory = ResolveStateDirectory(args, environment);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_ => ApplicationLogging.CreateFactory(stateDirectory));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IApplicationEnvironment>(environment);
        services.AddSingleton<ISettingsStore>(provider => new SettingsStore(stateDirectory, provider.GetRequiredService<ILogger<SettingsStore>>()));
        services.AddSingleton<ISteamDiscoveryService, SteamDiscoveryService>();
        services.AddSingleton<ISteamAccountService>(provider => new SteamAccountService(stateDirectory, logger: provider.GetRequiredService<ILogger<SteamAccountService>>()));
        services.AddSingleton<IResourceSharingService>(provider => new ResourceSharingService(stateDirectory, logger: provider.GetRequiredService<ILogger<ResourceSharingService>>()));
        services.AddSingleton<IUserInteraction, WpfUserInteraction>();
        services.AddSingleton<IThemeService, FluentThemeService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }

    /// <summary>读取可选 --state-dir 参数；用于便携部署和隔离验证，原始默认目录保持固定。</summary>
    public static string ResolveStateDirectory(string[] args, IApplicationEnvironment environment)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], "--state-dir", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(args[index + 1]);
        return environment.DefaultStateDirectory;
    }
}
