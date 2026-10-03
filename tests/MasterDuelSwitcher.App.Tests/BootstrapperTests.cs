using System.IO;
using System.Security.Principal;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.App.ViewModels;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MasterDuelSwitcher.App.Tests;

/// <summary>验证组合入口、系统数据目录及真实权限读取，不启动或修改 Steam。</summary>
public sealed class BootstrapperTests
{
    /// <summary>默认目录与不完整参数保持一致，完整覆盖参数按 Windows 完整路径解析。</summary>
    [Fact]
    public void StateDirectoryResolverHandlesDefaultIgnoredIncompleteAndOverrideArguments()
    {
        var environment = new TestEnvironment();
        Assert.Equal(environment.DefaultStateDirectory, Bootstrapper.ResolveStateDirectory([], environment));
        Assert.Equal(environment.DefaultStateDirectory, Bootstrapper.ResolveStateDirectory(["ignored", "value"], environment));
        Assert.Equal(environment.DefaultStateDirectory, Bootstrapper.ResolveStateDirectory(["--state-dir"], environment));
        Assert.Equal(environment.DefaultStateDirectory, Bootstrapper.ResolveStateDirectory(["ignored", "--state-dir"], environment));
        var expected = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher-path-check");
        Assert.Equal(expected, Bootstrapper.ResolveStateDirectory(["ignored", "value", "--STATE-DIR", expected], environment));
        Assert.Equal(Path.GetFullPath("relative-state"), Bootstrapper.ResolveStateDirectory(["--state-dir", "relative-state"], environment));
    }

    /// <summary>真实运行环境使用系统 LocalApplicationData，权限结果与当前 Windows 令牌一致。</summary>
    [Fact]
    public void WindowsEnvironmentReportsSystemDataDirectoryAndActualProcessToken()
    {
        var environment = new WindowsApplicationEnvironment();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MasterDuelSwitcher"), environment.DefaultStateDirectory);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), environment.IsElevated);
    }

    /// <summary>所有生产接口通过容器解析为预期实现，构造视图模型不读取登录数据。</summary>
    [Fact]
    public void ContainerResolvesAllProductionServicesIntoAnIsolatedViewModel()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher-DI-" + Guid.NewGuid().ToString("N"));
        using var services = Bootstrapper.Build(["--state-dir", stateDirectory]);
        Assert.IsType<WindowsApplicationEnvironment>(services.GetRequiredService<IApplicationEnvironment>());
        Assert.IsType<SettingsStore>(services.GetRequiredService<ISettingsStore>());
        Assert.IsType<SteamDiscoveryService>(services.GetRequiredService<ISteamDiscoveryService>());
        Assert.IsType<SteamAccountService>(services.GetRequiredService<ISteamAccountService>());
        Assert.IsType<ResourceSharingService>(services.GetRequiredService<IResourceSharingService>());
        Assert.IsType<WpfUserInteraction>(services.GetRequiredService<IUserInteraction>());
        Assert.IsType<FluentThemeService>(services.GetRequiredService<IThemeService>());
        var viewModel = services.GetRequiredService<MainViewModel>();
        Assert.Equal(stateDirectory, viewModel.StateDirectory);
        Assert.False(viewModel.StorageReady);
        Assert.Same(viewModel, services.GetRequiredService<MainViewModel>());
        Assert.False(File.Exists(Path.Combine(stateDirectory, "accounts.db")));
    }

    /// <summary>无参数容器的状态默认目录仍使用系统配置目录，构造不访问数据库。</summary>
    [Fact]
    public void ContainerUsesDefaultSystemDataDirectoryWhenNoOverrideWasSpecified()
    {
        using var services = Bootstrapper.Build([]);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MasterDuelSwitcher"), services.GetRequiredService<IApplicationEnvironment>().DefaultStateDirectory);
    }

    /// <summary>为路径解析提供固定目录，无需访问任何真实用户数据。</summary>
    private sealed class TestEnvironment : IApplicationEnvironment
    {
        /// <summary>路径解析测试的固定系统数据目录。</summary>
        public string DefaultStateDirectory => @"C:\Fixture\State";
        /// <summary>本测试环境使用普通权限。</summary>
        public bool IsElevated => false;
    }
}
