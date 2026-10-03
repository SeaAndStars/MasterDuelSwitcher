using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.App.ViewModels;
using MasterDuelSwitcher.App.Views;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Xunit;
using UiMessageBox = Wpf.Ui.Controls.MessageBox;

namespace MasterDuelSwitcher.App.Tests;

/// <summary>在单个真实 STA 应用中验证 WPF 适配器，所有业务服务均与实际 Steam 隔离。</summary>
public sealed class WpfAdapterTests
{
    /// <summary>Windows 标准对话框的命令消息。</summary>
    private const uint CommandMessage = 0x0111;
    /// <summary>标准对话框的确认按钮标识。</summary>
    private const int ConfirmCommand = 1;
    /// <summary>标准对话框的取消按钮标识。</summary>
    private const int CancelCommand = 2;

    /// <summary>一个宿主进程仅创建一次 WPF Application，完整检查真实视图及界面边界。</summary>
    [Fact]
    public async Task RealStaAdaptersRenderDialogsApplyThemesAndProtectBusyWindowClosure()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunApplication(completed)) { IsBackground = true, Name = "MasterDuelSwitcher-WpfAdapterTests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>启动唯一的真实 Dispatcher 循环，捕获断言并在结束后关闭所有测试窗口。</summary>
    private static void RunApplication(TaskCompletionSource<bool> completed)
    {
        try
        {
            var application = new MasterDuelSwitcher.App.App();
            application.InitializeComponent();
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var discovery = new IsolatedDiscovery();
            var store = new IsolatedStore();
            var errors = new IsolatedErrors();
            var services = new ServiceCollection()
                .AddSingleton<ISettingsStore>(_ => store)
                .AddSingleton<ISteamDiscoveryService>(_ => discovery)
                .AddSingleton<ISteamAccountService>(_ => new IsolatedSteam())
                .AddSingleton<IResourceSharingService>(_ => new IsolatedResources())
                .AddSingleton<IUserInteraction>(_ => new IsolatedInteraction())
                .AddSingleton<IThemeService>(_ => new IsolatedTheme())
                .AddSingleton<IApplicationEnvironment>(_ => new IsolatedEnvironment())
                .AddLogging()
                .AddSingleton<MainViewModel>()
                .AddSingleton<MainWindow>()
                .BuildServiceProvider();
            Exception? failure = null;
            var bootstrapper = new IsolatedBootstrapper(services, (window, viewModel) =>
            {
                var started = false;
                window.ContentRendered += async (_, _) =>
                {
                    if (started) return;
                    started = true;
                    try { await VerifyAdaptersAsync(application, window, viewModel, discovery, errors); }
                    catch (Exception exception) { failure = exception; }
                    finally { new WpfApplicationErrorHandler(new WpfUserInteraction()).Shutdown(0); }
                };
            });
            application.Controller = new ApplicationController(bootstrapper, errors);
            Assert.Equal(0, application.Run());
            if (failure is not null) throw failure;
            Assert.Equal(1, bootstrapper.BuildCount);
            Assert.True(store.Disposed);
            completed.TrySetResult(true);
        }
        catch (Exception exception) { completed.TrySetException(exception); }
    }

    /// <summary>验证真实资源、主题、对话框和关闭行为，不执行任何实际账号或目录事务。</summary>
    private static async Task VerifyAdaptersAsync(Application application, MainWindow window, MainViewModel viewModel, IsolatedDiscovery discovery, IsolatedErrors errors)
    {
        window.InitializeComponent();
        for (var attempt = 0; attempt < 100 && (!viewModel.IsReady || !viewModel.StorageReady); attempt++) await Task.Delay(50);
        Assert.True(viewModel.IsReady);
        Assert.True(viewModel.StorageReady);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Same(viewModel, window.DataContext);
        Assert.True(window.IsVisible);
        var rootGrid = Assert.IsType<Grid>(window.Content);
        Assert.NotNull(window.FindName("AccountsPage"));
        Assert.NotNull(application.Resources["CardStyle"]);
        Assert.False(application.Resources.Contains("CanvasBrush"));
        AssertBackgroundMatchesOfficialResource(application, window, rootGrid);

        var theme = new FluentThemeService();
        theme.Apply(true);
        Assert.Equal(ApplicationTheme.Dark, ApplicationThemeManager.GetAppTheme());
        AssertBackgroundMatchesOfficialResource(application, window, rootGrid);
        theme.Apply(false);
        Assert.Equal(ApplicationTheme.Light, ApplicationThemeManager.GetAppTheme());
        AssertBackgroundMatchesOfficialResource(application, window, rootGrid);

        var interaction = new WpfUserInteraction();
        await VerifyNoticeAsync(application, window, interaction.ShowNoticeAsync);
        var directory = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher-folder-dialog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Null(await PickFolderAsync(interaction, directory, false));
            var selected = await PickFolderAsync(interaction, directory, true);
            Assert.Equal(Path.GetFullPath(directory), selected, ignoreCase: true);
        }
        finally { Directory.Delete(directory); }

        _ = application.Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("fixture")));
        await errors.Shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("操作中断", Assert.Single(errors.Notices).Title);
        Assert.DoesNotContain("fixture", errors.Notices[0].Content);
        var errorsAdapter = new WpfApplicationErrorHandler(interaction);
        await VerifyNoticeAsync(application, window, errorsAdapter.ShowErrorAsync);

        discovery.BlockNext = true;
        var refresh = viewModel.RefreshCommand.ExecuteAsync();
        Assert.True(await Task.Run(() => discovery.Entered.Wait(TimeSpan.FromSeconds(5))));
        try
        {
            Assert.True(viewModel.IsBusy);
            window.Close();
            Assert.True(window.IsVisible);
        }
        finally
        {
            discovery.Release.Set();
            await refresh;
        }
        Assert.False(viewModel.IsBusy);
        window.Close();
        Assert.False(window.IsVisible);

        var unboundWindow = new MainWindow(viewModel) { DataContext = null };
        application.MainWindow = unboundWindow;
        unboundWindow.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        unboundWindow.Close();
        Assert.False(unboundWindow.IsVisible);
    }

    /// <summary>比较官方资源、窗口和实际根布局背景颜色，允许主题服务生成等色画刷副本。</summary>
    private static void AssertBackgroundMatchesOfficialResource(Application application, Window window, Grid rootGrid)
    {
        var expected = Assert.IsType<SolidColorBrush>(application.Resources["ApplicationBackgroundBrush"]).Color;
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(window.Background).Color);
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(rootGrid.Background).Color);
    }

    /// <summary>等待实际 Fluent 通知出现，核对所有者和文本，然后通过测试 Dispatcher 正常关闭。</summary>
    private static async Task VerifyNoticeAsync(Application application, Window owner, Func<string, string, Task> showNotice)
    {
        var title = "MasterDuelSwitcher 通知验证 " + Guid.NewGuid().ToString("N");
        const string content = "真实提示框内容，仅验证界面。";
        var observed = false;
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var dialog = application.Windows.OfType<UiMessageBox>().FirstOrDefault(item => item.Title == title);
            if (dialog is null) return;
            timer.Stop();
            try
            {
                Assert.True(dialog.IsVisible);
                Assert.Same(owner, dialog.Owner);
                var text = Assert.IsType<TextBlock>(dialog.Content);
                Assert.Equal(content, text.Text);
                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                observed = true;
            }
            catch (Exception exception) { failure = exception; }
            finally { dialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.MessageBoxButton.Close); }
        };
        timer.Start();
        try { await showNotice(title, content); }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
        Assert.True(observed);
    }

    /// <summary>显示真正的系统目录选择器，仅对同进程且标题精确匹配的测试对话框发送确认或取消。</summary>
    private static async Task<string?> PickFolderAsync(WpfUserInteraction interaction, string directory, bool confirm)
    {
        var title = "MasterDuelSwitcher 目录验证 " + Guid.NewGuid().ToString("N");
        var closeTask = Task.Run(() => CompleteNativeDialog(title, confirm ? ConfirmCommand : CancelCommand));
        var result = interaction.PickFolder(title, directory);
        await closeTask;
        return result;
    }

    /// <summary>在限定时间定位测试宿主自己的标准对话框，未完成时发取消消息保留干净退出。</summary>
    private static void CompleteNativeDialog(string title, int command)
    {
        var watch = Stopwatch.StartNew();
        nint dialog = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            dialog = FindOwnedDialog(title);
            if (dialog != 0) break;
            Thread.Sleep(30);
        }
        if (dialog == 0) throw new TimeoutException("测试系统对话框未出现。");
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (FindOwnedDialog(title) == 0) return;
            PostMessage(dialog, CommandMessage, command, 0);
            Thread.Sleep(100);
        }
        PostMessage(dialog, CommandMessage, CancelCommand, 0);
        throw new TimeoutException("测试系统对话框没有响应确认或取消。");
    }

    /// <summary>依据当前进程、精确标题和标准对话框类名选择窗口，不接触其他应用。</summary>
    private static nint FindOwnedDialog(string title)
    {
        nint found = 0;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var processId);
            if (processId != Environment.ProcessId) return true;
            var className = new StringBuilder(256);
            GetClassName(handle, className, className.Capacity);
            if (className.ToString() != "#32770") return true;
            var text = new StringBuilder(512);
            GetWindowText(handle, text, text.Capacity);
            if (text.ToString() != title) return true;
            found = handle;
            return false;
        }, 0);
        return found;
    }

    /// <summary>枚举顶层窗口的 Win32 回调签名。</summary>
    private delegate bool WindowEnumerationCallback(nint handle, nint state);
    /// <summary>枚举当前桌面的顶层窗口。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowEnumerationCallback callback, nint state);
    /// <summary>读取窗口所属进程，限定测试自身的操作边界。</summary>
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    /// <summary>读取窗口类名以确认标准系统对话框。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint handle, StringBuilder className, int capacity);
    /// <summary>读取窗口标题，精确匹配测试生成的唯一标识。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int capacity);
    /// <summary>向已验证属于测试自身的窗口发送标准按钮命令。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint handle, uint message, nint parameter, nint state);

    /// <summary>提供隔离偏好，存储方法均不访问磁盘。</summary>
    private sealed class IsolatedStore : ISettingsStore, IDisposable
    {
        /// <summary>记录真实桌面会话退出时是否释放所持有的服务容器。</summary>
        public bool Disposed { get; private set; }
        /// <summary>本测试使用的虚构工具状态目录。</summary>
        public string StateDirectory => @"C:\Fixture\WpfAdapterState";
        /// <summary>返回默认的独立偏好对象。</summary>
        public AppSettings Load() => new();
        /// <summary>界面渲染测试不持久保存设置。</summary>
        public void Save(AppSettings settings) { }
        /// <summary>同步边界无需真实数据库。</summary>
        public void SynchronizeAccounts(IReadOnlyList<SteamAccount> accounts) { }
        /// <summary>渲染测试没有虚构账号填充。</summary>
        public IReadOnlyList<SteamAccount> GetAccounts() => [];
        /// <summary>观察应用退出触发的容器释放行为。</summary>
        public void Dispose() => Disposed = true;
    }

    /// <summary>将隔离服务容器交给真实桌面会话，让实际 App 启动和释放会话。</summary>
    private sealed class IsolatedBootstrapper : IApplicationBootstrapper
    {
        /// <summary>由真实桌面会话持有的隔离服务容器。</summary>
        private readonly ServiceProvider _services;
        /// <summary>在窗口显示前注册渲染验证的测试回调。</summary>
        private readonly Action<MainWindow, MainViewModel> _prepare;
        /// <summary>实际 App 启动调用组合入口的次数。</summary>
        public int BuildCount { get; private set; }
        /// <summary>提供已组合的隔离服务和窗口准备回调。</summary>
        public IsolatedBootstrapper(ServiceProvider services, Action<MainWindow, MainViewModel> prepare) { _services = services; _prepare = prepare; }
        /// <summary>创建真实桌面会话，不调用生产 Steam 或安装发现组合。</summary>
        public IApplicationSession Build(string[] args)
        {
            BuildCount++;
            _prepare(_services.GetRequiredService<MainWindow>(), _services.GetRequiredService<MainViewModel>());
            return new DesktopApplicationSession(_services);
        }
    }

    /// <summary>记录真实 App 未处理错误事件，不显示额外业务通知。</summary>
    private sealed class IsolatedErrors : IApplicationErrorHandler
    {
        /// <summary>实际未处理错误事件产生的标题和通用内容。</summary>
        public List<(string Title, string Content)> Notices { get; } = [];
        /// <summary>通知测试真实 Dispatcher 错误事件已经被处理。</summary>
        public TaskCompletionSource<bool> Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>记录最终用户提示并释放测试等待。</summary>
        public Task ShowErrorAsync(string title, string content) { Notices.Add((title, content)); Shown.TrySetResult(true); return Task.CompletedTask; }
        /// <summary>意外启动失败时结束当前测试应用，由宿主退出码断言报告失败。</summary>
        public void Shutdown(int exitCode) => Application.Current.Shutdown(exitCode);
    }

    /// <summary>通过受控暂停验证窗口事务保护，不扫描真实安装。</summary>
    private sealed class IsolatedDiscovery : ISteamDiscoveryService, IDisposable
    {
        /// <summary>下一次发现是否等待测试释放。</summary>
        public bool BlockNext { get; set; }
        /// <summary>通知测试后台扫描已进入暂停点。</summary>
        public ManualResetEventSlim Entered { get; } = new(false);
        /// <summary>测试释放后台扫描的信号。</summary>
        public ManualResetEventSlim Release { get; } = new(false);
        /// <summary>只返回空安装快照，受控暂停不触及外部状态。</summary>
        public DiscoveryResult Discover(string? steamOverride = null, string? gameOverride = null)
        {
            if (BlockNext)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("测试后台扫描未释放。");
            }
            return new DiscoveryResult();
        }
        /// <summary>本测试不读取任何 Steam 账号。</summary>
        public IReadOnlyList<SteamAccount> ReadAccounts(string steamPath) => [];
        /// <summary>释放仅由本测试创建的同步句柄。</summary>
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    /// <summary>拒绝任何意外的 Steam 切号或还原请求。</summary>
    private sealed class IsolatedSteam : ISteamAccountService
    {
        /// <summary>渲染验证不得请求切换真实账号。</summary>
        public Task<string> SwitchAndLaunchAsync(string steamPath, SteamAccount account, CancellationToken cancellationToken = default) => throw new InvalidOperationException("界面适配器测试不得触发账号切换。");
        /// <summary>渲染验证不得请求修改真实登录配置。</summary>
        public Task<string> RestoreLatestAsync(string steamPath, CancellationToken cancellationToken = default) => throw new InvalidOperationException("界面适配器测试不得触发登录还原。");
    }

    /// <summary>提供空资源数据并阻止实际目录事务。</summary>
    private sealed class IsolatedResources : IResourceSharingService
    {
        /// <summary>返回空资源列表。</summary>
        public IReadOnlyList<ResourceProfile> ScanProfiles(string gamePath) => [];
        /// <summary>渲染验证不得创建目录共享。</summary>
        public ShareBackup? EnableSharing(string gamePath, string sourceFolder, IEnumerable<string> targetFolders) => throw new InvalidOperationException("界面适配器测试不得变更资源目录。");
        /// <summary>返回空资源事务列表。</summary>
        public IReadOnlyList<ShareBackup> GetBackups(string gamePath) => [];
        /// <summary>渲染验证不得恢复实际资源目录。</summary>
        public void Restore(string backupId) => throw new InvalidOperationException("界面适配器测试不得还原资源目录。");
    }

    /// <summary>保持视图模型自身交互隔离，真实适配器由测试明确直接调用。</summary>
    private sealed class IsolatedInteraction : IUserInteraction
    {
        /// <summary>视图模型初始化期间的通知保持隔离。</summary>
        public Task ShowNoticeAsync(string title, string content) => Task.CompletedTask;
        /// <summary>视图模型自身不打开目录选择器。</summary>
        public string? PickFolder(string title, string initialDirectory) => null;
    }

    /// <summary>初始化期间不改变全局资源，主题适配器由测试明确调用。</summary>
    private sealed class IsolatedTheme : IThemeService
    {
        /// <summary>忽略视图模型初始化时的主题请求。</summary>
        public void Apply(bool dark) { }
    }

    /// <summary>提供稳定的本机权限与默认目录边界。</summary>
    private sealed class IsolatedEnvironment : IApplicationEnvironment
    {
        /// <summary>测试默认状态目录不关联真实用户数据。</summary>
        public string DefaultStateDirectory => @"C:\Fixture\DefaultWpfState";
        /// <summary>渲染测试无需真实管理员权限。</summary>
        public bool IsElevated => false;
    }
}
