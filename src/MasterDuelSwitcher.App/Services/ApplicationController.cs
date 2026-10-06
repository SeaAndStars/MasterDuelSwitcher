using System.Windows;
using MasterDuelSwitcher.App.ViewModels;
using MasterDuelSwitcher.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MasterDuelSwitcher.App.Services;

/// <summary>定义应用组合会话，允许不同桌面宿主注入自己的生命周期。</summary>
public interface IApplicationSession : IDisposable
{
    /// <summary>当前会话数据目录下持久化的应用生命周期日志。</summary>
    ILogger<ApplicationController> Logger { get; }
    /// <summary>显示主视图并初始化它的视图模型。</summary>
    Task StartAsync();
}

/// <summary>按启动参数构建应用会话的接口。</summary>
public interface IApplicationBootstrapper
{
    /// <summary>创建当前运行所需的完整依赖注入会话。</summary>
    IApplicationSession Build(string[] args);
}

/// <summary>应用启动及未处理错误的末级通知接口。</summary>
public interface IApplicationErrorHandler
{
    /// <summary>显示无需业务服务的末级错误通知。</summary>
    Task ShowErrorAsync(string title, string content);
    /// <summary>使用指定退出码结束桌面应用。</summary>
    void Shutdown(int exitCode);
}

/// <summary>将应用生命周期与 WPF 事件隔离为可注入、可等待的操作。</summary>
public sealed class ApplicationController : IDisposable
{
    /// <summary>应用会话组合服务。</summary>
    private readonly IApplicationBootstrapper _bootstrapper;
    /// <summary>末级错误通知与退出服务。</summary>
    private readonly IApplicationErrorHandler _errors;
    /// <summary>本次成功创建并由应用持有的会话。</summary>
    private IApplicationSession? _session;
    /// <summary>当前会话的生命周期日志，组合失败时使用注入的末级日志。</summary>
    private ILogger<ApplicationController> _logger;

    /// <summary>使用宿主提供的组合入口与错误处理初始化控制器。</summary>
    public ApplicationController(IApplicationBootstrapper bootstrapper, IApplicationErrorHandler errors, ILogger<ApplicationController>? logger = null)
    {
        _bootstrapper = bootstrapper;
        _errors = errors;
        _logger = logger ?? NullLogger<ApplicationController>.Instance;
    }

    /// <summary>构建和启动应用，失败时显示错误并使用失败退出码结束。</summary>
    public async Task StartAsync(string[] args)
    {
        try
        {
            _session = _bootstrapper.Build(args);
            _logger = _session.Logger;
            _logger.LogInformation("应用开始启动");
            await _session.StartAsync();
            _logger.LogInformation("应用启动完成");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "应用启动失败");
            await _errors.ShowErrorAsync("Master Duel Switcher", "启动失败。请检查工具数据目录的访问权限，然后重新打开程序。");
            _errors.Shutdown(1);
        }
    }

    /// <summary>提示界面未处理错误，不输出异常栈或登录数据。</summary>
    public Task HandleUnhandledErrorAsync(Exception? exception = null)
    {
        _logger.LogError(exception, "界面出现未处理异常");
        return _errors.ShowErrorAsync("操作中断", "当前操作遇到错误。请刷新列表后重试；资源变更可在“备份还原”中检查记录。");
    }

    /// <summary>释放已创建的应用会话，未启动时保持无操作。</summary>
    public void Dispose()
    {
        _logger.LogInformation("应用退出");
        _session?.Dispose();
    }
}

/// <summary>使用生产依赖注入入口建立 WPF 桌面会话。</summary>
public sealed class DesktopApplicationBootstrapper : IApplicationBootstrapper
{
    /// <summary>创建使用系统数据目录或明确覆盖目录的桌面会话。</summary>
    public IApplicationSession Build(string[] args) => new DesktopApplicationSession(Bootstrapper.Build(args));
}

/// <summary>持有一个依赖注入容器的真实 WPF 桌面会话。</summary>
public sealed class DesktopApplicationSession : IApplicationSession
{
    /// <summary>应用拥有并在退出时释放的服务容器。</summary>
    private readonly ServiceProvider _services;
    /// <summary>以完整服务容器创建桌面会话。</summary>
    public DesktopApplicationSession(ServiceProvider services) => _services = services;
    /// <summary>读取此会话容器持有的生命周期日志。</summary>
    public ILogger<ApplicationController> Logger => _services.GetRequiredService<ILogger<ApplicationController>>();
    /// <summary>显示真实 Fluent 窗口并初始化真实注入的视图模型。</summary>
    public async Task StartAsync()
    {
        var window = _services.GetRequiredService<MainWindow>();
        Application.Current.MainWindow = window;
        window.Show();
        await _services.GetRequiredService<MainViewModel>().InitializeAsync();
    }
    /// <summary>在退出时释放容器持有的服务。</summary>
    public void Dispose() => _services.Dispose();
}

/// <summary>以官方 Fluent 通知实现应用启动和未处理错误提示。</summary>
public sealed class WpfApplicationErrorHandler : IApplicationErrorHandler
{
    /// <summary>错误提示使用的可注入界面服务。</summary>
    private readonly IUserInteraction _interaction;
    /// <summary>注入与正常操作一致的 Fluent 界面服务。</summary>
    public WpfApplicationErrorHandler(IUserInteraction interaction) => _interaction = interaction;
    /// <summary>显示与普通操作一致的官方 Fluent 提示。</summary>
    public Task ShowErrorAsync(string title, string content) => _interaction.ShowNoticeAsync(title, content);
    /// <summary>使用真实 WPF 应用生命周期退出。</summary>
    public void Shutdown(int exitCode) => Application.Current.Shutdown(exitCode);
}
