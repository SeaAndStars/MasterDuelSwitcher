using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.App.ViewModels;
using MasterDuelSwitcher.App.Views;
using MasterDuelSwitcher.App.Views.Pages;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Extensions;
using Xunit;
using IThemeService = MasterDuelSwitcher.App.Services.IThemeService;
using UiContentDialog = Wpf.Ui.Controls.ContentDialog;
using UiContentDialogHost = Wpf.Ui.Controls.ContentDialogHost;

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

    /// <summary>输出测试阶段与线程标识，用于定位真实 STA 调度或同步布局停顿。</summary>
    private static void ReportStage(string stage) =>
        Console.WriteLine($"WPF-STA {DateTime.UtcNow:O} thread={Environment.CurrentManagedThreadId} {stage}");

    /// <summary>在真实同步布局调用前后输出阶段标识，保留原有布局与断言行为。</summary>
    private static void UpdateLayoutWithDiagnostics(FrameworkElement element, string stage)
    {
        ReportStage(stage + "-before-layout");
        element.UpdateLayout();
        ReportStage(stage + "-after-layout");
    }

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
        AvatarFixture? avatarFixture = null;
        Exception? runFailure = null;
        try
        {
            ReportStage("application-start");
            var application = new MasterDuelSwitcher.App.App();
            application.InitializeComponent();
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            avatarFixture = new AvatarFixture();
            var discovery = new IsolatedDiscovery();
            var store = new IsolatedStore();
            var errors = new IsolatedErrors();
            var services = new ServiceCollection()
                .AddSingleton<ISettingsStore>(_ => store)
                .AddSingleton<ISteamDiscoveryService>(_ => discovery)
                .AddSingleton<ISteamAccountService>(_ => new IsolatedSteam())
                .AddSingleton<IAccountAvatarService>(_ => new IsolatedAvatar())
                .AddSingleton<IResourceSharingService>(_ => new IsolatedResources())
                .AddSingleton<IUserInteraction>(_ => new IsolatedInteraction())
                .AddSingleton<IThemeService>(_ => new IsolatedTheme())
                .AddSingleton<IApplicationEnvironment>(_ => new IsolatedEnvironment())
                .AddLogging()
                .AddSingleton<WorkspaceService>()
                .AddSingleton<MainViewModel>()
                .AddSingleton<AccountsPageViewModel>()
                .AddSingleton<ResourcesPageViewModel>()
                .AddSingleton<BackupsPageViewModel>()
                .AddSingleton<SettingsPageViewModel>()
                .AddSingleton<AccountsPage>()
                .AddSingleton<ResourcesPage>()
                .AddSingleton<BackupsPage>()
                .AddSingleton<SettingsPage>()
                .AddSingleton<INavigationViewPageProvider, PageService>()
                .AddSingleton<INavigationService, NavigationService>()
                .AddSingleton<MainWindow>()
                .BuildServiceProvider();
            Exception? failure = null;
            var bootstrapper = new IsolatedBootstrapper(services, (window, viewModel) =>
            {
                VerifyEmptyNavigationSelection(window, viewModel);
                var started = false;
                window.ContentRendered += async (_, _) =>
                {
                    if (started) return;
                    started = true;
                    ReportStage("content-rendered");
                    try { await VerifyAdaptersAsync(application, window, viewModel, discovery, errors, services, avatarFixture); }
                    catch (Exception exception) { failure = exception; ReportStage("assertion-failure " + exception); }
                    finally
                    {
                        ReportStage("application-shutdown-before");
                        new WpfApplicationErrorHandler(new WpfUserInteraction()).Shutdown(0);
                        ReportStage("application-shutdown-after");
                    }
                };
            });
            application.Controller = new ApplicationController(bootstrapper, errors);
            ReportStage("application-run-before");
            Assert.Equal(0, application.Run());
            ReportStage("application-run-after");
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.Equal(1, bootstrapper.BuildCount);
            Assert.True(store.Disposed);
        }
        catch (Exception exception) { runFailure = exception; }
        finally
        {
            try
            {
                ReportStage("avatar-fixture-cleanup-before");
                avatarFixture?.Dispose();
                ReportStage("avatar-fixture-cleanup-after");
            }
            catch (Exception exception) { runFailure ??= exception; }
        }
        if (runFailure is null) completed.TrySetResult(true);
        else completed.TrySetException(runFailure);
    }

    /// <summary>验证真实资源、主题、对话框和关闭行为，不执行任何实际账号或目录事务。</summary>
    private static async Task VerifyAdaptersAsync(Application application, MainWindow window, MainViewModel viewModel, IsolatedDiscovery discovery, IsolatedErrors errors, ServiceProvider services, AvatarFixture avatarFixture)
    {
        ReportStage("initialize-start");
        window.InitializeComponent();
        var workspace = viewModel.Workspace;
        for (var attempt = 0; attempt < 100 && (workspace.IsBusy || !workspace.StorageReady); attempt++) await Task.Delay(50);
        Assert.False(workspace.IsBusy);
        Assert.True(workspace.StorageReady);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Same(viewModel, window.DataContext);
        Assert.True(window.IsVisible);
        var rootGrid = Assert.IsType<Grid>(window.Content);
        Assert.NotNull(application.Resources["CardStyle"]);
        Assert.False(application.Resources.Contains("CanvasBrush"));
        AssertBackgroundMatchesOfficialResource(application, window, rootGrid);
        ReportStage("nav-start");
        await VerifyOfficialNavigationAsync(window, viewModel, services);
        ReportStage("nav-end resources-binding-start");
        await ResourcesPageViewModelTests.VerifyRealPageBindingsAsync();
        ReportStage("resources-binding-end backups-binding-start");
        await BackupsPageViewModelTests.VerifyRealPageBindingsAsync();
        ReportStage("nav-end scroll-start");
        await VerifyAccountsListScrollingAsync(window, viewModel, discovery, services);
        ReportStage("scroll-end avatars-start");
        await VerifyAccountAvatarsAsync(window, viewModel, discovery, services, avatarFixture);
        ReportStage("avatars-end fonts-start");
        await VerifyEmbeddedSymbolFontsAsync(window, rootGrid);

        ReportStage("fonts-end theme-start");
        var theme = new FluentThemeService();
        theme.Apply(true);
        Assert.Equal(ApplicationTheme.Dark, ApplicationThemeManager.GetAppTheme());
        AssertBackgroundMatchesOfficialResource(application, window, rootGrid);
        theme.Apply(false);
        Assert.Equal(ApplicationTheme.Light, ApplicationThemeManager.GetAppTheme());
        AssertBackgroundMatchesOfficialResource(application, window, rootGrid);

        var interaction = new WpfUserInteraction();
        ReportStage("theme-end notices-start");
        await VerifyNoticeAsync(application, window, interaction.ShowNoticeAsync);
        ReportStage("notices-main-end notices-cold-start");
        await VerifyColdNoticeAsync(application, window, interaction.ShowNoticeAsync);
        ReportStage("notices-cold-end confirm-primary-start");
        await VerifyConfirmationAsync(window, interaction.ConfirmAsync, true);
        ReportStage("confirm-primary-end confirm-close-start");
        await VerifyConfirmationAsync(window, interaction.ConfirmAsync, false);
        ReportStage("confirm-close-end picker-start");
        var directory = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher-folder-dialog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Null(await PickFolderAsync(interaction, directory, false));
            var selected = await PickFolderAsync(interaction, directory, true);
            Assert.Equal(Path.GetFullPath(directory), selected, ignoreCase: true);
        }
        finally { Directory.Delete(directory); }

        ReportStage("picker-end unhandled-start");
        _ = application.Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("fixture")));
        await errors.Shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("操作中断", Assert.Single(errors.Notices).Title);
        Assert.DoesNotContain("fixture", errors.Notices[0].Content);
        var errorsAdapter = new WpfApplicationErrorHandler(interaction);
        await VerifyNoticeAsync(application, window, errorsAdapter.ShowErrorAsync);

        ReportStage("unhandled-end busy-close-start");
        discovery.BlockNext = true;
        var refresh = viewModel.RefreshCommand.ExecuteAsync();
        Assert.True(await Task.Run(() => discovery.Entered.Wait(TimeSpan.FromSeconds(5))));
        try
        {
            Assert.True(workspace.IsBusy);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(Assert.IsType<Wpf.Ui.Controls.NavigationView>(window.FindName("MainNavigation")).IsEnabled);
            foreach (var name in new[] { "AccountsNav", "ResourcesNav", "BackupsNav", "SettingsNav" })
                Assert.False(Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(window.FindName(name)).IsEnabled);
            Assert.Equal("Accounts", viewModel.CurrentPage);
            window.Close();
            Assert.True(window.IsVisible);
        }
        finally
        {
            discovery.Release.Set();
            await refresh;
        }
        Assert.False(workspace.IsBusy);
        ReportStage("busy-close-end close-start");
        window.Close();
        Assert.False(window.IsVisible);

        var unboundWindow = new MainWindow(viewModel, services.GetRequiredService<INavigationService>()) { DataContext = null };
        application.MainWindow = unboundWindow;
        var closingWasRaised = false;
        unboundWindow.Closing += (_, _) => closingWasRaised = true;
        unboundWindow.Close();
        Assert.True(closingWasRaised);
        Assert.False(unboundWindow.IsVisible);
        ReportStage("close-end");
    }

    /// <summary>通过官方导航项自动化选择路径验证真实 Page、独立视图模型和导航区底部信息。</summary>
    private static async Task VerifyOfficialNavigationAsync(MainWindow window, MainViewModel viewModel, ServiceProvider services)
    {
        var navigation = Assert.IsType<Wpf.Ui.Controls.NavigationView>(window.FindName("MainNavigation"));
        var account = Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(window.FindName("AccountsNav"));
        var resources = Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(window.FindName("ResourcesNav"));
        var backups = Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(window.FindName("BackupsNav"));
        var settings = Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(window.FindName("SettingsNav"));
        Assert.Equal(3, navigation.MenuItems.Count);
        Assert.Same(account, navigation.MenuItems[0]);
        Assert.Same(resources, navigation.MenuItems[1]);
        Assert.Same(backups, navigation.MenuItems[2]);
        Assert.Same(settings, Assert.Single(navigation.FooterMenuItems));
        Assert.IsAssignableFrom<FrameworkElement>(navigation.PaneHeader);
        var footer = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("NavigationStatusFooter"));
        Assert.Same(footer, navigation.PaneFooter);
        Assert.True(footer.IsVisible);
        Assert.Equal(viewModel.Workspace.DiscoveryStatus, Assert.IsType<TextBlock>(window.FindName("DiscoveryStatus")).Text);
        Assert.Equal(viewModel.AdminStatus, Assert.IsType<TextBlock>(window.FindName("AdminStatus")).Text);

        var accountsPage = services.GetRequiredService<AccountsPage>();
        var resourcesPage = services.GetRequiredService<ResourcesPage>();
        var backupsPage = services.GetRequiredService<BackupsPage>();
        var settingsPage = services.GetRequiredService<SettingsPage>();
        foreach (var page in new Page[] { accountsPage, resourcesPage, backupsPage, settingsPage })
        {
            Assert.Equal(14, page.FontSize);
            Assert.Equal(new[] { "Segoe UI Variable", "Microsoft YaHei UI", "Segoe UI" }, page.FontFamily.Source.Split(',').Select(name => name.Trim()));
        }
        Assert.Same(services.GetRequiredService<AccountsPageViewModel>(), Assert.IsAssignableFrom<INavigableView<AccountsPageViewModel>>(accountsPage).ViewModel);
        Assert.Same(services.GetRequiredService<ResourcesPageViewModel>(), Assert.IsAssignableFrom<INavigableView<ResourcesPageViewModel>>(resourcesPage).ViewModel);
        Assert.Same(services.GetRequiredService<BackupsPageViewModel>(), Assert.IsAssignableFrom<INavigableView<BackupsPageViewModel>>(backupsPage).ViewModel);
        Assert.Same(services.GetRequiredService<SettingsPageViewModel>(), Assert.IsAssignableFrom<INavigableView<SettingsPageViewModel>>(settingsPage).ViewModel);
        Assert.Same(accountsPage.ViewModel, accountsPage.DataContext);
        Assert.Same(resourcesPage.ViewModel, resourcesPage.DataContext);
        Assert.Same(backupsPage.ViewModel, backupsPage.DataContext);
        Assert.Same(settingsPage.ViewModel, settingsPage.DataContext);
        Assert.Same(viewModel.Workspace, accountsPage.ViewModel.Workspace);
        Assert.Same(viewModel.Workspace, resourcesPage.ViewModel.Workspace);
        Assert.Same(viewModel.Workspace, backupsPage.ViewModel.Workspace);
        Assert.Same(viewModel.Workspace, settingsPage.ViewModel.Workspace);
        accountsPage.InitializeComponent();
        ((System.Windows.Markup.IStyleConnector)accountsPage).Connect(-1, new object());
        resourcesPage.InitializeComponent();
        backupsPage.InitializeComponent();
        settingsPage.InitializeComponent();
        Assert.NotNull(accountsPage.FindName("AccountList"));
        Assert.NotNull(resourcesPage.FindName("ProfileList"));
        Assert.NotNull(backupsPage.FindName("BackupList"));
        Assert.NotNull(settingsPage.FindName("SteamPathBox"));

        var pages = new[]
        {
            (Item: resources, Name: "Resources", View: (Page)resourcesPage),
            (Item: backups, Name: "Backups", View: (Page)backupsPage),
            (Item: settings, Name: "Settings", View: (Page)settingsPage),
            (Item: account, Name: "Accounts", View: (Page)accountsPage)
        };
        var provider = services.GetRequiredService<INavigationViewPageProvider>();
        var navigationService = services.GetRequiredService<INavigationService>();
        Assert.Same(navigation, navigationService.GetNavigationControl());
        foreach (var page in pages)
        {
            Assert.True(page.Item.IsEnabled);
            Assert.Equal(page.View.GetType(), page.Item.TargetPageType);
            Assert.Same(page.View, provider.GetPage(page.View.GetType()));
            await SelectNavigationItemAsync(page.Item);
            Assert.Equal(page.Name, viewModel.CurrentPage);
            Assert.True(page.Item.IsActive);
            Assert.True(page.View.IsVisible);
            if (page.View is SettingsPage) await VerifyStatusLayoutAsync(window, page.View, navigation);
            foreach (var other in pages)
            {
                Assert.Equal(other.Name == page.Name, other.Item.IsActive);
                Assert.Equal(other.Name == page.Name, other.View.IsVisible);
            }
        }
        VerifyMissingNavigationTag(navigation, account, viewModel);
    }

    /// <summary>真实窗口尚未 Loaded 时导航没有选中项，官方路由选择事件恢复默认账号标题。</summary>
    private static void VerifyEmptyNavigationSelection(MainWindow window, MainViewModel viewModel)
    {
        var navigation = Assert.IsType<Wpf.Ui.Controls.NavigationView>(window.FindName("MainNavigation"));
        Assert.Null(navigation.SelectedItem);
        viewModel.CurrentPage = "Settings";
        navigation.RaiseEvent(new RoutedEventArgs(Wpf.Ui.Controls.NavigationView.SelectionChangedEvent, navigation));
        Assert.Equal("Accounts", viewModel.CurrentPage);
        Assert.Null(navigation.SelectedItem);
    }

    /// <summary>实际选中项缺少可选页面标签时，官方选择事件采用默认标题并保留当前页面。</summary>
    private static void VerifyMissingNavigationTag(Wpf.Ui.Controls.NavigationView navigation, Wpf.Ui.Controls.NavigationViewItem item, MainViewModel viewModel)
    {
        Assert.Same(item, navigation.SelectedItem);
        var previousTag = item.TargetPageTag;
        try
        {
            item.SetCurrentValue(Wpf.Ui.Controls.NavigationViewItem.TargetPageTagProperty, null);
            Assert.Null(item.TargetPageTag);
            viewModel.CurrentPage = "Settings";
            navigation.RaiseEvent(new RoutedEventArgs(Wpf.Ui.Controls.NavigationView.SelectionChangedEvent, navigation));
            Assert.Equal("Accounts", viewModel.CurrentPage);
            Assert.Same(item, navigation.SelectedItem);
            Assert.True(item.IsActive);
        }
        finally { item.SetCurrentValue(Wpf.Ui.Controls.NavigationViewItem.TargetPageTagProperty, previousTag); }
    }

    /// <summary>实际设置页和滚动视口始终位于独立状态行上方，展开日志后仍保持边界。</summary>
    private static async Task VerifyStatusLayoutAsync(MainWindow window, Page page, Wpf.Ui.Controls.NavigationView navigation)
    {
        await Task.Delay(navigation.TransitionDuration);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        UpdateLayoutWithDiagnostics(window, "status-initial");
        var workspaceGrid = Assert.IsType<Grid>(window.FindName("Workspace"));
        Assert.Equal(2, workspaceGrid.RowDefinitions.Count);
        Assert.True(workspaceGrid.RowDefinitions[0].Height.IsStar);
        Assert.True(workspaceGrid.RowDefinitions[1].Height.IsAuto);
        Assert.Equal(0, Grid.GetRow(navigation));
        var statusBorder = Assert.Single(workspaceGrid.Children.OfType<Border>(), element => Grid.GetRow(element) == 1);
        var status = Assert.IsType<TextBlock>(window.FindName("StatusText"));
        var logs = Assert.IsType<ListBox>(window.FindName("StatusLog"));
        var expander = Assert.Single(VisualDescendants(statusBorder).OfType<Wpf.Ui.Controls.CardExpander>());
        var scrollViewer = Assert.IsType<ScrollViewer>(page.Content);
        var previousWidth = window.Width;
        var previousHeight = window.Height;
        try
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            foreach (var expanded in new[] { false, true })
            {
                expander.IsExpanded = expanded;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                UpdateLayoutWithDiagnostics(window, "status-expanded-" + expanded);
                scrollViewer.ScrollToEnd();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                UpdateLayoutWithDiagnostics(window, "status-scrolled-" + expanded);
                Assert.True(status.IsVisible);
                Assert.True(statusBorder.ActualHeight > 0);
                Assert.Equal(expanded, logs.IsVisible);
                var statusTop = statusBorder.TransformToAncestor(window).Transform(new Point()).Y;
                var navigationBottom = navigation.TransformToAncestor(window).Transform(new Point(0, navigation.ActualHeight)).Y;
                var pageBottom = page.TransformToAncestor(window).Transform(new Point(0, page.ActualHeight)).Y;
                var viewportBottom = scrollViewer.TransformToAncestor(window).Transform(new Point(0, scrollViewer.ActualHeight)).Y;
                Assert.True(navigationBottom <= statusTop + 1, $"导航下沿 {navigationBottom} 超过状态区上沿 {statusTop}。");
                Assert.True(pageBottom <= statusTop + 1, $"页面下沿 {pageBottom} 超过状态区上沿 {statusTop}。");
                Assert.True(viewportBottom <= statusTop + 1, $"滚动视口下沿 {viewportBottom} 超过状态区上沿 {statusTop}。");
            }
        }
        finally
        {
            expander.IsExpanded = false;
            window.Width = previousWidth;
            window.Height = previousHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            UpdateLayoutWithDiagnostics(window, "status-cleanup");
        }
    }

    /// <summary>最小窗口中账号仅在列表内部滚动，搜索和右侧编辑器固定且不侵入状态区。</summary>
    private static async Task VerifyAccountsListScrollingAsync(MainWindow window, MainViewModel viewModel, IsolatedDiscovery discovery, ServiceProvider services)
    {
        var page = services.GetRequiredService<AccountsPage>();
        var accountsViewModel = services.GetRequiredService<AccountsPageViewModel>();
        var list = Assert.IsType<ListBox>(page.FindName("AccountList"));
        var search = Assert.IsType<Wpf.Ui.Controls.TextBox>(page.FindName("AccountSearch"));
        var editor = Assert.IsType<StackPanel>(page.FindName("SelectedAccountPanel"));
        var details = Assert.IsType<ScrollViewer>(page.FindName("AccountDetailsScroll"));
        var navigation = Assert.IsType<Wpf.Ui.Controls.NavigationView>(window.FindName("MainNavigation"));
        var workspaceGrid = Assert.IsType<Grid>(window.FindName("Workspace"));
        var statusBorder = Assert.Single(workspaceGrid.Children.OfType<Border>(), element => Grid.GetRow(element) == 1);
        var originalAccounts = discovery.Accounts;
        var originalWidth = window.Width;
        var originalHeight = window.Height;
        list.SelectedItem = Assert.Single(accountsViewModel.Accounts);
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.NotNull(accountsViewModel.SelectedAccount);
        var selectedId = accountsViewModel.SelectedAccount.Account.SteamId;
        discovery.Accounts = originalAccounts.Concat(Enumerable.Range(1, 40).Select(index => new SteamAccount
        {
            SteamId = (76561198000000000L + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
            AccountName = "fixture-account-" + index,
            PersonaName = "滚动测试账号 " + index,
            RememberPassword = true,
            AllowAutoLogin = true,
            LastLoginTimestamp = 1700000000L + index
        })).ToArray();
        try
        {
            await viewModel.RefreshCommand.ExecuteAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(41, list.Items.Count);
            Assert.NotNull(accountsViewModel.SelectedAccount);
            Assert.Equal(selectedId, accountsViewModel.SelectedAccount.Account.SteamId);
            Assert.Same(accountsViewModel.SelectedAccount, list.SelectedItem);
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await Task.Delay(navigation.TransitionDuration);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            UpdateLayoutWithDiagnostics(window, "scroll-initial");
            var listScroll = Assert.Single(VisualDescendants(list).OfType<ScrollViewer>());
            var listViewport = Assert.Single(VisualDescendants(listScroll).OfType<ScrollContentPresenter>());
            Assert.True(listScroll.ScrollableHeight > 0);
            listScroll.ScrollToTop();
            details.ScrollToTop();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            UpdateLayoutWithDiagnostics(window, "scroll-top");
            var searchBefore = BoundsInWindow(search, window);
            var editorBefore = BoundsInWindow(editor, window);
            var detailsBefore = BoundsInWindow(details, window);
            var detailsOffset = details.VerticalOffset;
            listScroll.ScrollToEnd();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            UpdateLayoutWithDiagnostics(window, "scroll-end");
            Assert.True(listScroll.VerticalOffset > 0);
            Assert.Equal(listScroll.ScrollableHeight, listScroll.VerticalOffset, precision: 3);
            Assert.Equal(searchBefore, BoundsInWindow(search, window));
            Assert.Equal(editorBefore, BoundsInWindow(editor, window));
            Assert.Equal(detailsBefore, BoundsInWindow(details, window));
            Assert.Equal(detailsOffset, details.VerticalOffset);
            Assert.Equal(selectedId, accountsViewModel.SelectedAccount.Account.SteamId);
            Assert.Same(accountsViewModel.SelectedAccount, list.SelectedItem);
            Assert.True(search.IsVisible);
            Assert.True(editor.IsVisible);
            var statusTop = BoundsInWindow(statusBorder, window).Top;
            Assert.True(BoundsInWindow(listViewport, window).Bottom <= statusTop + 1, "账号列表视口超过独立状态区上沿。");
            Assert.True(BoundsInWindow(details, window).Bottom <= statusTop + 1, "账号编辑视口超过独立状态区上沿。");
        }
        finally
        {
            discovery.Accounts = originalAccounts;
            await viewModel.RefreshCommand.ExecuteAsync();
            window.Width = originalWidth;
            window.Height = originalHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            UpdateLayoutWithDiagnostics(window, "scroll-cleanup");
        }
    }

    /// <summary>取得控件在实际窗口中的绘制布局边界，用于比较独立滚动区域。</summary>
    private static Rect BoundsInWindow(FrameworkElement element, Window window) =>
        element.TransformToAncestor(window).TransformBounds(new Rect(new Point(), element.RenderSize));

    /// <summary>真实 PNG 绑定显示圆角头像，损坏 PNG 触发图像失败并恢复首字，异步通知保持 UI 线程。</summary>
    private static async Task VerifyAccountAvatarsAsync(MainWindow window, MainViewModel viewModel, IsolatedDiscovery discovery, ServiceProvider services, AvatarFixture fixture)
    {
        const string validId = "76561198000000101";
        const string brokenId = "76561198000000102";
        var page = services.GetRequiredService<AccountsPage>();
        var accountsViewModel = services.GetRequiredService<AccountsPageViewModel>();
        var list = Assert.IsType<ListBox>(page.FindName("AccountList"));
        var avatar = Assert.IsType<IsolatedAvatar>(services.GetRequiredService<IAccountAvatarService>());
        var originalAccounts = discovery.Accounts;
        var originalPaths = avatar.Paths;
        var originalCompletion = avatar.Completion;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThreadId = Environment.CurrentManagedThreadId;
        var notifications = new List<(string Id, int ThreadId, SynchronizationContext? Context)>();
        var failedIds = new List<string>();
        EventHandler<ExceptionRoutedEventArgs> failed = (_, args) =>
        {
            if (args.OriginalSource is Image { DataContext: MasterDuelSwitcher.App.AccountItem account })
            {
                failedIds.Add(account.Account.SteamId);
                ReportStage("avatars-image-failed " + account.Account.SteamId);
            }
        };
        page.AddHandler(Image.ImageFailedEvent, failed, true);
        avatar.Paths = new Dictionary<string, string> { [validId] = fixture.ValidPath, [brokenId] = fixture.BrokenPath };
        avatar.Completion = release.Task;
        discovery.Accounts =
        [
            new SteamAccount { SteamId = validId, AccountName = "avatar-valid", PersonaName = "正常头像账号", RememberPassword = true, MostRecent = true },
            new SteamAccount { SteamId = brokenId, AccountName = "avatar-broken", PersonaName = "损坏头像账号", RememberPassword = true }
        ];
        try
        {
            ReportStage("avatars-refresh-before");
            await viewModel.RefreshCommand.ExecuteAsync();
            ReportStage("avatars-refresh-after");
            Assert.Equal(2, accountsViewModel.Accounts.Count);
            foreach (var account in accountsViewModel.Accounts)
            {
                account.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(MasterDuelSwitcher.App.AccountItem.AvatarPath))
                    {
                        notifications.Add((account.Account.SteamId, Environment.CurrentManagedThreadId, SynchronizationContext.Current));
                        ReportStage("avatars-path-changed " + account.Account.SteamId);
                    }
                };
            }
            ReportStage("avatars-initial-idle-before");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            ReportStage("avatars-initial-idle-after");
            UpdateLayoutWithDiagnostics(window, "avatars-initial");
            var valid = Assert.Single(accountsViewModel.Accounts, account => account.Account.SteamId == validId);
            var broken = Assert.Single(accountsViewModel.Accounts, account => account.Account.SteamId == brokenId);
            ReportStage("avatars-details-before");
            await VerifySelectedAccountDetailsAsync(page, list, accountsViewModel);
            ReportStage("avatars-details-after avatars-gate-before");
            list.SelectedItem = valid;
            await Task.Run(() => release.TrySetResult(true));
            ReportStage("avatars-gate-released loading-before");
            await accountsViewModel.AvatarLoadingTask.WaitAsync(TimeSpan.FromSeconds(5));
            ReportStage("avatars-loading-after idle-before");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            ReportStage("avatars-idle-after");
            UpdateLayoutWithDiagnostics(window, "avatars-layout");
            var validRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(valid));
            var validImage = Assert.Single(VisualDescendants(validRow).OfType<Image>(), image => image.Name == "AccountAvatarImage");
            var validInitial = Assert.Single(VisualDescendants(validRow).OfType<TextBlock>(), text => text.Name == "AvatarInitial");
            Assert.Equal(fixture.ValidPath, valid.AvatarPath);
            Assert.True(valid.HasAvatar);
            Assert.True(validImage.IsVisible);
            Assert.False(validInitial.IsVisible);
            var source = Assert.IsAssignableFrom<BitmapSource>(validImage.Source);
            Assert.Equal(8, source.PixelWidth);
            Assert.Equal(8, source.PixelHeight);
            Assert.Equal(Stretch.UniformToFill, validImage.Stretch);
            var avatarGrid = Assert.IsType<Grid>(VisualTreeHelper.GetParent(validImage));
            var clip = Assert.IsType<RectangleGeometry>(avatarGrid.Clip);
            Assert.Equal(new Rect(0, 0, 36, 36), clip.Rect);
            Assert.Equal(10, clip.RadiusX);
            Assert.Equal(10, clip.RadiusY);
            Assert.Equal(36, validImage.ActualWidth);
            Assert.Equal(36, validImage.ActualHeight);
            ReportStage("avatars-valid-end avatars-fallback-before");
            for (var attempt = 0; attempt < 50 && broken.AvatarPath is not null; attempt++)
            {
                await Task.Delay(20);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            UpdateLayoutWithDiagnostics(window, "avatars-fallback");
            Assert.Contains(brokenId, failedIds);
            Assert.Null(broken.AvatarPath);
            Assert.False(broken.HasAvatar);
            var brokenRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(broken));
            var brokenImage = Assert.Single(VisualDescendants(brokenRow).OfType<Image>(), image => image.Name == "AccountAvatarImage");
            var brokenInitial = Assert.Single(VisualDescendants(brokenRow).OfType<TextBlock>(), text => text.Name == "AvatarInitial");
            Assert.False(brokenImage.IsVisible);
            Assert.True(brokenInitial.IsVisible);
            Assert.Equal(broken.Initial, brokenInitial.Text);
            Assert.Contains(notifications, notification => notification.Id == validId);
            Assert.Contains(notifications, notification => notification.Id == brokenId);
            Assert.All(notifications, notification =>
            {
                Assert.Equal(uiThreadId, notification.ThreadId);
                Assert.IsType<DispatcherSynchronizationContext>(notification.Context);
            });
            Assert.Same(valid, accountsViewModel.SelectedAccount);
            ReportStage("avatars-fallback-after cache-refresh-before");
            await viewModel.RefreshCommand.ExecuteAsync();
            await accountsViewModel.AvatarLoadingTask.WaitAsync(TimeSpan.FromSeconds(5));
            ReportStage("avatars-cache-refresh-after");
            Assert.Null(Assert.Single(accountsViewModel.Accounts, account => account.Account.SteamId == brokenId).AvatarPath);
        }
        finally
        {
            ReportStage("avatars-cleanup-before");
            release.TrySetResult(true);
            page.RemoveHandler(Image.ImageFailedEvent, failed);
            avatar.Paths = originalPaths;
            avatar.Completion = originalCompletion;
            discovery.Accounts = originalAccounts;
            await viewModel.RefreshCommand.ExecuteAsync();
            await accountsViewModel.AvatarLoadingTask.WaitAsync(TimeSpan.FromSeconds(5));
            ReportStage("avatars-cleanup-refresh-after idle-before");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            ReportStage("avatars-cleanup-idle-after");
            UpdateLayoutWithDiagnostics(window, "avatars-cleanup");
            ReportStage("avatars-cleanup-after");
        }
    }

    /// <summary>通过真实列表先后选择两个账号，确认右侧标题、标识、状态和按钮标签随当前模型更新。</summary>
    private static async Task VerifySelectedAccountDetailsAsync(AccountsPage page, ListBox list, AccountsPageViewModel viewModel)
    {
        var title = Assert.IsType<TextBlock>(page.FindName("SelectedAccountTitle"));
        var id = Assert.IsType<TextBlock>(page.FindName("SelectedAccountId"));
        var state = Assert.IsType<TextBlock>(page.FindName("SelectedAccountState"));
        var star = Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("SelectedStarButton"));
        var hide = Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("HideAccountButton"));
        Assert.Equal(2, viewModel.Accounts.Count);
        foreach (var account in viewModel.Accounts)
        {
            list.SelectedItem = account;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            UpdateLayoutWithDiagnostics(page, "avatars-details " + account.Account.SteamId);
            Assert.Same(account, viewModel.SelectedAccount);
            Assert.True(viewModel.HasSelectedAccount);
            Assert.Equal(account.Account.DisplayName, title.Text);
            Assert.Equal(account.SteamIdLabel, id.Text);
            Assert.Equal(account.LoginState, state.Text);
            Assert.Equal(account.StarLabel, star.Content);
            Assert.Equal(account.HideLabel, hide.Content);
        }
    }

    /// <summary>调用官方导航项 SelectionItem 自动化路径，使用实际页面类型及页面提供器。</summary>
    private static async Task SelectNavigationItemAsync(Wpf.Ui.Controls.NavigationViewItem item)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(item);
        Assert.NotNull(peer);
        var selection = Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem));
        selection.Select();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>检查 Win11 正文字体顺序和真正渲染的官方 Regular、Filled 图标字形。</summary>
    private static async Task VerifyEmbeddedSymbolFontsAsync(MainWindow window, Grid rootGrid)
    {
        Assert.Equal(new[] { "Segoe UI Variable", "Microsoft YaHei UI", "Segoe UI" }, window.FontFamily.Source.Split(',').Select(name => name.Trim()));
        var regular = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Person24 };
        var filled = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Person24, Filled = true };
        var holder = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        holder.Children.Add(regular);
        holder.Children.Add(filled);
        Grid.SetRow(holder, 1);
        rootGrid.Children.Add(holder);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        UpdateLayoutWithDiagnostics(window, "fonts");
        try
        {
            AssertSymbolGlyph(regular, Wpf.Ui.Controls.SymbolRegular.Person24.GetString(), "Regular");
            AssertSymbolGlyph(filled, Wpf.Ui.Controls.SymbolFilled.Person24.GetString(), "Filled");
        }
        finally { rootGrid.Children.Remove(holder); }
    }

    /// <summary>从实际图标模板读取字体，验证包内字体 URI、资源流及非零字形，避免字体回退方块。</summary>
    private static void AssertSymbolGlyph(Wpf.Ui.Controls.SymbolIcon icon, string expectedText, string variant)
    {
        Assert.True(icon.IsVisible);
        var glyphText = Assert.Single(VisualDescendants(icon).OfType<TextBlock>());
        Assert.Equal(expectedText, glyphText.Text);
        var typeface = new Typeface(glyphText.FontFamily, glyphText.FontStyle, glyphText.FontWeight, glyphText.FontStretch);
        Assert.True(typeface.TryGetGlyphTypeface(out var glyphTypeface));
        Assert.NotNull(glyphTypeface);
        Assert.Equal("pack", glyphTypeface.FontUri.Scheme);
        Assert.Contains("Wpf.Ui", glyphTypeface.FontUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(variant, glyphTypeface.FontUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        var resource = Application.GetResourceStream(glyphTypeface.FontUri);
        Assert.NotNull(resource);
        using var stream = resource.Stream;
        Assert.True(stream.Length > 0);
        foreach (var rune in expectedText.EnumerateRunes())
        {
            Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetValue(rune.Value, out var glyph));
            Assert.NotEqual((ushort)0, glyph);
        }
    }

    /// <summary>遍历实际绘制的控件树，以自动化标识定位主按钮的实际背景。</summary>
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    /// <summary>比较官方资源、窗口和实际根布局背景颜色，允许主题服务生成等色画刷副本。</summary>
    private static void AssertBackgroundMatchesOfficialResource(Application application, Window window, Grid rootGrid)
    {
        var expected = Assert.IsType<SolidColorBrush>(application.Resources["ApplicationBackgroundBrush"]).Color;
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(window.Background).Color);
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(rootGrid.Background).Color);
    }

    /// <summary>验证主窗口官方宿主中的真实模态通知，并通过官方关闭按钮命令正常结束。</summary>
    private static async Task VerifyNoticeAsync(Application application, Window owner, Func<string, string, Task> showNotice)
    {
        var host = Assert.IsType<UiContentDialogHost>(owner.FindName("DialogHost"));
        Assert.Same(host, UiContentDialogHost.GetForWindow(owner));
        Assert.Null(host.Content);
        var title = "MasterDuelSwitcher 通知验证 " + Guid.NewGuid().ToString("N");
        const string content = "真实提示框内容，仅验证界面。";
        var observed = false;
        var closed = false;
        Task? noticeTask = null;
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (host.Content is not UiContentDialog dialog || !Equals(dialog.Title, title)) return;
            timer.Stop();
            try
            {
                Assert.NotNull(noticeTask);
                Assert.False(noticeTask.IsCompleted);
                AssertNoticeContent(owner, host, dialog, title, content);
                Assert.Empty(application.Windows.OfType<NoticeDialogWindow>());
                dialog.Closed += (_, _) => closed = true;
                observed = true;
            }
            catch (Exception exception) { failure = exception; }
            finally { dialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Close); }
        };
        timer.Start();
        try { noticeTask = showNotice(title, content); await noticeTask; }
        finally { timer.Stop(); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(observed);
        Assert.True(closed);
        Assert.Null(host.Content);
        Assert.True(owner.IsVisible);
    }

    /// <summary>主窗口尚未创建时使用真实临时 Fluent 宿主，通知关闭后恢复主窗口并释放临时窗口。</summary>
    private static async Task VerifyColdNoticeAsync(Application application, Window owner, Func<string, string, Task> showNotice)
    {
        var title = "MasterDuelSwitcher 冷启动通知验证 " + Guid.NewGuid().ToString("N");
        const string content = "启动阶段仍显示官方内容对话框。";
        NoticeDialogWindow? temporaryWindow = null;
        UiContentDialogHost? temporaryHost = null;
        Task? noticeTask = null;
        var closed = false;
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var candidate = application.Windows.OfType<NoticeDialogWindow>().FirstOrDefault();
            if (candidate?.FindName("DialogHost") is not UiContentDialogHost host ||
                host.Content is not UiContentDialog dialog || !Equals(dialog.Title, title)) return;
            timer.Stop();
            temporaryWindow = candidate;
            temporaryHost = host;
            candidate.Closed += (_, _) => closed = true;
            try
            {
                candidate.InitializeComponent();
                Assert.NotNull(noticeTask);
                Assert.False(noticeTask.IsCompleted);
                Assert.Single(application.Windows.OfType<NoticeDialogWindow>());
                AssertNoticeContent(candidate, host, dialog, title, content);
                Assert.Contains(VisualDescendants(candidate), element => element is Wpf.Ui.Controls.TitleBar);
            }
            catch (Exception exception) { failure = exception; }
            finally { dialog.Hide(Wpf.Ui.Controls.ContentDialogResult.None); }
        };
        application.MainWindow = null;
        timer.Start();
        try
        {
            noticeTask = showNotice(title, content);
            await noticeTask;
            Assert.Null(application.MainWindow);
        }
        finally
        {
            timer.Stop();
            application.MainWindow = owner;
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.NotNull(temporaryWindow);
        Assert.NotNull(temporaryHost);
        Assert.True(closed);
        Assert.False(temporaryWindow.IsVisible);
        Assert.Null(temporaryHost.Content);
        Assert.Empty(application.Windows.OfType<NoticeDialogWindow>());
        Assert.Same(owner, application.MainWindow);
        Assert.True(owner.IsVisible);
    }

    /// <summary>真实确认对话框仅在官方主按钮点击后返回同意，关闭按钮明确返回取消。</summary>
    private static async Task VerifyConfirmationAsync(Window owner, Func<string, string, Task<bool>> confirm, bool accept)
    {
        var host = Assert.IsType<UiContentDialogHost>(owner.FindName("DialogHost"));
        Assert.Null(host.Content);
        var title = "MasterDuelSwitcher 确认验证 " + Guid.NewGuid().ToString("N");
        const string content = "检查目标资源记录后继续，取消时保留现有状态。";
        var observed = false;
        var closed = false;
        Task<bool>? confirmationTask = null;
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (host.Content is not UiContentDialog dialog || !Equals(dialog.Title, title)) return;
            timer.Stop();
            try
            {
                Assert.NotNull(confirmationTask);
                Assert.False(confirmationTask.IsCompleted);
                AssertNoticeContent(owner, host, dialog, title, content, true);
                dialog.Closed += (_, _) => closed = true;
                observed = true;
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                dialog.TemplateButtonCommand.Execute(accept ? Wpf.Ui.Controls.ContentDialogButton.Primary : Wpf.Ui.Controls.ContentDialogButton.Close);
            }
        };
        timer.Start();
        bool result;
        try { confirmationTask = confirm(title, content); result = await confirmationTask; }
        finally { timer.Stop(); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(observed);
        Assert.True(closed);
        Assert.Equal(accept, result);
        Assert.Null(host.Content);
        Assert.True(owner.IsVisible);
    }

    /// <summary>检查官方新宿主、实际可见模态内容、原始标题正文以及通知和确认按钮文字。</summary>
    private static void AssertNoticeContent(Window owner, UiContentDialogHost host, UiContentDialog dialog, string title, string content, bool confirmation = false)
    {
        Assert.True(owner.IsVisible);
        Assert.True(host.IsEnabled);
        Assert.True(dialog.IsVisible);
        Assert.Same(host, dialog.DialogHostEx);
        Assert.Same(host, UiContentDialogHost.GetForWindow(owner));
        Assert.Same(dialog, host.Content);
        Assert.Same(owner, Window.GetWindow(dialog));
        Assert.Equal(title, dialog.Title);
        Assert.Equal(confirmation ? "取消" : "知道了", dialog.CloseButtonText);
        Assert.Equal(confirmation ? "继续" : "", dialog.PrimaryButtonText);
        Assert.Equal(confirmation ? Wpf.Ui.Controls.ContentDialogButton.Primary : Wpf.Ui.Controls.ContentDialogButton.Close, dialog.DefaultButton);
        var text = Assert.IsType<TextBlock>(dialog.Content);
        Assert.Equal(content, text.Text);
        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
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
        /// <summary>供界面刷新使用的可控账号快照，全部为隔离测试数据。</summary>
        public IReadOnlyList<SteamAccount> Accounts { get; set; } = [new SteamAccount { SteamId = "ui-fixture", AccountName = "ui-fixture", PersonaName = "界面测试账号", RememberPassword = true, MostRecent = true }];
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
            return new DiscoveryResult { Accounts = Accounts };
        }
        /// <summary>本测试不读取任何真实 Steam 账号。</summary>
        public IReadOnlyList<SteamAccount> ReadAccounts(string steamPath) => [];
        /// <summary>释放仅由本测试创建的同步句柄。</summary>
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    /// <summary>隔离头像获取边界，禁止网络访问并保持默认首字头像。</summary>
    private sealed class IsolatedAvatar : IAccountAvatarService
    {
        /// <summary>由测试提供的本地临时图片映射，空映射保持首字头像。</summary>
        public IReadOnlyDictionary<string, string> Paths { get; set; } = new Dictionary<string, string>();
        /// <summary>由测试释放的完成信号，确保订阅属性通知后才返回头像。</summary>
        public Task Completion { get; set; } = Task.CompletedTask;
        /// <summary>在线程池返回隔离图片路径，不读取 Steam 缓存或访问网络。</summary>
        public Task<string?> GetAvatarPathAsync(string steamPath, string steamId, CancellationToken cancellationToken = default)
        {
            var path = Paths.GetValueOrDefault(steamId);
            var completion = Completion;
            return Task.Run(async () => { await completion.WaitAsync(cancellationToken).ConfigureAwait(false); return path; }, cancellationToken);
        }
    }

    /// <summary>拥有完整像素 PNG 和签名、尾块完整但图像宽度无效的 PNG，应用退出后删除临时文件。</summary>
    private sealed class AvatarFixture : IDisposable
    {
        /// <summary>仅本次测试持有的头像临时目录。</summary>
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher-avatar-ui-" + Guid.NewGuid().ToString("N"));
        /// <summary>可由 WPF 完整解码的八乘八像素头像。</summary>
        public string ValidPath => Path.Combine(_directory, "valid.png");
        /// <summary>PNG 签名和 IEND 尾块完整，但 IHDR 宽度为零且真实解码失败的头像。</summary>
        public string BrokenPath => Path.Combine(_directory, "broken.png");
        /// <summary>生成两份本测试所有的实际图片文件，不复用真实账号头像。</summary>
        public AvatarFixture()
        {
            Directory.CreateDirectory(_directory);
            var pixels = Enumerable.Range(0, 8 * 8).SelectMany(_ => new byte[] { 30, 120, 210, 255 }).ToArray();
            var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(ValidPath)) encoder.Save(stream);
            var broken = File.ReadAllBytes(ValidPath);
            Array.Clear(broken, 16, 4);
            File.WriteAllBytes(BrokenPath, broken);
            Assert.Equal((8, 8), DecodePixels(ValidPath));
            var exception = Record.Exception(() => DecodePixels(BrokenPath));
            Assert.NotNull(exception);
            Assert.True(exception is COMException or FileFormatException, exception.ToString());
        }
        /// <summary>通过真实 WPF 解码器读取尺寸并复制全部像素，验证夹具内容而非仅检查 PNG 签名。</summary>
        private static (int Width, int Height) DecodePixels(string path)
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
            frame.CopyPixels(new byte[stride * frame.PixelHeight], stride, 0);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        /// <summary>仅在真实应用已退出后删除两个确定路径的文件及空目录。</summary>
        public void Dispose()
        {
            File.Delete(ValidPath);
            File.Delete(BrokenPath);
            Directory.Delete(_directory);
        }
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
        /// <summary>渲染验证不重建资源共享，只提供隔离的空修复结果。</summary>
        public ShareBackup? RepairInvalidSharing(string backupId) => null;
    }

    /// <summary>保持视图模型自身交互隔离，真实适配器由测试明确直接调用。</summary>
    private sealed class IsolatedInteraction : IUserInteraction
    {
        /// <summary>视图模型初始化期间的通知保持隔离。</summary>
        public Task ShowNoticeAsync(string title, string content) => Task.CompletedTask;
        /// <summary>业务界面测试默认取消确认，避免实际资源修复操作。</summary>
        public Task<bool> ConfirmAsync(string title, string content) => Task.FromResult(false);
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
