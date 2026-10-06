using System.Windows;
using System.Windows.Threading;
using MasterDuelSwitcher.App.Services;

namespace MasterDuelSwitcher.App;

/// <summary>WPF 应用生命周期入口，组合服务并处理界面未捕获错误。</summary>
public partial class App : Application
{
    /// <summary>宿主可注入的应用生命周期控制器，默认使用生产桌面组合入口。</summary>
    public ApplicationController Controller { get; set; } = new(new DesktopApplicationBootstrapper(), new WpfApplicationErrorHandler(new WpfUserInteraction()));

    /// <summary>构建真实服务、打开 Fluent 窗口并启动视图模型的首次发现。</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        await Controller.StartAsync(e.Args);
    }

    /// <summary>退出时释放应用服务容器。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Controller.Dispose();
        base.OnExit(e);
    }

    /// <summary>显示可恢复的界面错误，不输出异常对象或登录数据。</summary>
    private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        await Controller.HandleUnhandledErrorAsync(e.Exception);
    }
}
