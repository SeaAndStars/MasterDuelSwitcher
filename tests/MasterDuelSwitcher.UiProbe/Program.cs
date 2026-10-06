using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MasterDuelSwitcher.App;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.App.Views;
using MasterDuelSwitcher.App.Views.Pages;
using MasterDuelSwitcher.Core.Services;
using MasterDuelSwitcher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MasterDuelSwitcher.UiProbe;

/// <summary>打开实际 WPF 窗口，验证导航、设置保存及渲染，不触发真实账号切换。</summary>
public static class Program
{
    /// <summary>已完成检查的名称列表。</summary>
    private static readonly List<string> Checks = [];

    /// <summary>实际记住账号中已成功取得并解码的头像数量，不输出账号标识。</summary>
    private static int AvatarCount;

    /// <summary>实际只读发现的账号总数，用于报告头像可用范围。</summary>
    private static int AccountTotal;

    /// <summary>STA 入口，在独立配置目录运行窗口并写出截图及验证记录。</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "mdswitch-ui-probe"));
        Directory.CreateDirectory(output);
        var isolatedState = Path.Combine(output, "state");
        var app = new ProbeApplication();
        app.Resources.MergedDictionaries.Add(new FluentThemeResources());
        app.Resources.MergedDictionaries.Add(new FluentControlResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MasterDuelSwitcher;component/Views/ApplicationResources.xaml", UriKind.Absolute)
        });
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var provider = Bootstrapper.Build(["--state-dir", isolatedState]);
        var viewModel = provider.GetRequiredService<MainViewModel>();
        var window = provider.GetRequiredService<MainWindow>();
        window.ShowActivated = false;
        app.MainWindow = window;
        var result = 1;
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                await viewModel.InitializeAsync();
                await WaitForReady(window);
                Assert(window.ActualWidth >= 900 && window.ActualHeight >= 620, "实际窗口尺寸");
                await VerifyNavigationFooterAsync(window, provider.GetRequiredService<AccountsPage>(), "标准浅色");
                await VerifyAccountFeaturesAsync(window, provider.GetRequiredService<AccountsPage>(), isolatedState);
                Screenshot(window, Path.Combine(output, "accounts-light.png"));
                await NavigateAsync(window, provider.GetRequiredService<ResourcesPage>());
                Screenshot(window, Path.Combine(output, "resources-light.png"));
                await NavigateAsync(window, provider.GetRequiredService<BackupsPage>());
                var freePacksPage = provider.GetRequiredService<FreePacksPage>();
                await NavigateAsync(window, freePacksPage);
                Assert(Find<System.Windows.Controls.Primitives.ButtonBase>(freePacksPage, "FreePacksStart").IsEnabled, "免费开包开始按钮可用");
                Assert(!Find<System.Windows.Controls.Primitives.ButtonBase>(freePacksPage, "FreePacksStop").IsEnabled, "免费开包空闲停止按钮禁用");
                Assert(Find<TextBlock>(freePacksPage, "ScannedPackCount").Text == "0", "免费开包扫描初始计数");
                Assert(Find<TextBlock>(freePacksPage, "OpenedPackCount").Text == "0", "免费开包初始完成计数");
                Screenshot(window, Path.Combine(output, "free-packs-light.png"));
                var settingsPage = provider.GetRequiredService<SettingsPage>();
                await NavigateAsync(window, settingsPage);
                Screenshot(window, Path.Combine(output, "settings-light.png"));
                var theme = Find<System.Windows.Controls.Primitives.ToggleButton>(settingsPage, "DarkThemeToggle");
                theme.IsChecked = true;
                await settingsPage.ViewModel.ToggleThemeCommand.ExecuteAsync();
                await Task.Delay(200);
                Pump();
                Assert(new SettingsStore(isolatedState).Load().DarkTheme, "深色主题配置已保存");
                await VerifyNavigationFooterAsync(window, settingsPage, "标准深色");
                Screenshot(window, Path.Combine(output, "settings-dark.png"));
                await NavigateAsync(window, freePacksPage);
                Screenshot(window, Path.Combine(output, "free-packs-dark.png"));
                await CaptureOfficialDialogAsync(window, provider.GetRequiredService<IUserInteraction>(), Path.Combine(output, "content-dialog-dark.png"));
                await NavigateAsync(window, provider.GetRequiredService<AccountsPage>());
                window.Width = 900;
                window.Height = 620;
                Pump();
                await VerifyNavigationFooterAsync(window, provider.GetRequiredService<AccountsPage>(), "最小深色");
                Screenshot(window, Path.Combine(output, "accounts-compact-dark.png"));
                Assert(Find<TextBlock>(window, "StatusText").IsVisible, "状态区域可见");
                File.WriteAllText(Path.Combine(output, "ui-verification.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Checks,
                    WpfUiVersion = "4.3.0",
                    ActualWindowType = window.GetType().BaseType?.FullName,
                    AvatarCount,
                    AccountTotal,
                    VerifiedAtUtc = DateTimeOffset.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true }));
                result = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "ui-verification.json"), JsonSerializer.Serialize(new
                    { Passed = false, Checks, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
                Console.Error.WriteLine(ex);
            }
            finally
            {
                window.Close();
                app.Shutdown(result);
            }
        };
        window.Show();
        app.Run();
        Console.WriteLine($"WPF 实际窗口检查：{Checks.Count} 项，退出码 {result}");
        return result;
    }

    /// <summary>真实窗口在主题切换、缩放与日志折叠后，导航设置及状态信息始终贴近工作区底部。</summary>
    private static async Task VerifyNavigationFooterAsync(MainWindow window, Page page, string layout)
    {
        var workspace = Find<Grid>(window, "Workspace");
        var region = Find<Border>(window, "StatusRegion");
        var inner = (Border)region.Child;
        var expander = ((StackPanel)inner.Child).Children.OfType<Wpf.Ui.Controls.CardExpander>().Single();
        var logs = Find<ListBox>(window, "StatusLog");
        var footer = Find<StackPanel>(window, "NavigationStatusFooter");
        var settings = Find<Wpf.Ui.Controls.NavigationViewItem>(window, "SettingsNav");
        Rect? fixedBounds = null;
        try
        {
            foreach (var expanded in new[] { false, true })
            {
                expander.IsExpanded = expanded;
                for (var attempt = 0; attempt < 50 && logs.IsVisible != expanded; attempt++) await Task.Delay(10);
                Pump();
                window.UpdateLayout();
                var workspaceBounds = new Rect(workspace.TranslatePoint(new Point(), window), workspace.RenderSize);
                var footerBounds = new Rect(footer.TranslatePoint(new Point(), window), footer.RenderSize);
                var settingsBounds = new Rect(settings.TranslatePoint(new Point(), window), settings.RenderSize);
                var regionTop = region.TranslatePoint(new Point(), window).Y;
                var pageBottom = page.TranslatePoint(new Point(0, page.ActualHeight), window).Y;
                Assert(logs.IsVisible == expanded, $"{layout}日志状态为{expanded}");
                Assert(workspaceBounds.Bottom - footerBounds.Bottom is >= 17 and <= 19, $"{layout}导航信息固定底部（日志{expanded}）");
                Assert(settingsBounds.Bottom <= footerBounds.Top && footerBounds.Top - settingsBounds.Bottom <= 24, $"{layout}设置位于底部信息上方（日志{expanded}）");
                Assert(pageBottom <= regionTop + 1, $"{layout}右侧页面避让日志区域（日志{expanded}）");
                if (fixedBounds is { } previous) Assert(previous == footerBounds, $"{layout}展开日志不移动左侧页脚");
                fixedBounds = footerBounds;
            }
        }
        finally
        {
            expander.IsExpanded = false;
            for (var attempt = 0; attempt < 50 && logs.IsVisible; attempt++) await Task.Delay(10);
            Pump();
            window.UpdateLayout();
        }
    }

    /// <summary>等待实际异步发现结束，避免对启动动画期间的状态作断言。</summary>
    private static async Task WaitForReady(MainWindow window)
    {
        var workspace = Find<FrameworkElement>(window, "Workspace");
        for (var attempt = 0; attempt < 150 && !workspace.IsEnabled; attempt++) await Task.Delay(100);
        Assert(workspace.IsEnabled, "启动发现已结束");
        await Task.Delay(250);
        Pump();
    }

    /// <summary>通过实际官方导航控件切换 DI 页面并核对显示实例与独立视图模型。</summary>
    private static async Task NavigateAsync(MainWindow window, Page page)
    {
        var navigation = Find<Wpf.Ui.Controls.NavigationView>(window, "MainNavigation");
        Assert(navigation.Navigate(page.GetType()), $"官方导航接受 {page.GetType().Name}");
        await Task.Delay(navigation.TransitionDuration);
        Pump();
        Assert(page.IsVisible && navigation.SelectedItem?.TargetPageType == page.GetType(), $"导航显示 {page.GetType().Name}");
        Assert(page.DataContext?.GetType().Name == page.GetType().Name + "ViewModel", $"独立模型 {page.GetType().Name}");
    }

    /// <summary>查找 XAML 命名控件，缺失时报告具体控件名。</summary>
    private static T Find<T>(FrameworkElement element, string name) where T : class => element.FindName(name) as T
        ?? throw new InvalidOperationException($"控件缺失或类型错误：{name}");

    /// <summary>显示真实交互服务的官方内容对话框，记录渲染并正常关闭。</summary>
    private static async Task CaptureOfficialDialogAsync(MainWindow window, IUserInteraction interaction, string output)
    {
        var pending = interaction.ShowNoticeAsync("共享状态检查", "已保留现有资源目录。操作记录保存在系统数据目录，可在备份还原页检查。");
        await Task.Delay(350);
        Pump();
        var host = Find<Wpf.Ui.Controls.ContentDialogHost>(window, "DialogHost");
        var dialog = host.Content as Wpf.Ui.Controls.ContentDialog
            ?? throw new InvalidOperationException("实际通知没有使用官方 ContentDialog。");
        Assert(dialog.IsVisible, "官方 ContentDialog 已显示");
        Screenshot(window, output);
        dialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Close);
        await pending;
        await Task.Delay(250);
        Assert(!dialog.IsVisible, "官方 ContentDialog 正常关闭");
    }

    /// <summary>在隔离数据库验证实际搜索绑定、星标保存和账号列表的独立滚动。</summary>
    private static async Task VerifyAccountFeaturesAsync(MainWindow window, AccountsPage page, string isolatedState)
    {
        var model = page.ViewModel;
        Assert(model.Accounts.Count > 1, "本机账号已只读发现供实际交互验证");
        AccountTotal = model.Accounts.Count;
        await model.AvatarLoadingTask.WaitAsync(TimeSpan.FromSeconds(30));
        Pump();
        Assert(!model.Workspace.IsBusy, "后台头像加载不占用工作区忙碌状态");
        AvatarCount = model.Accounts.Count(account => account.HasAvatar);
        foreach (var account in model.Accounts.Where(account => account.HasAvatar))
        {
            using var avatarFile = File.OpenRead(account.AvatarPath!);
            var decoder = BitmapDecoder.Create(avatarFile, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0 || decoder.Frames[0].PixelWidth == 0 || decoder.Frames[0].PixelHeight == 0)
                throw new InvalidOperationException("实际缓存头像没有可解码的像素。");
        }
        Checks.Add("全部已取得的真实头像缓存可完整解码");
        var avatarAccount = model.Accounts.FirstOrDefault(account => account.HasAvatar);
        if (avatarAccount is not null)
        {
            var avatarList = Find<ListBox>(page, "AccountList");
            avatarList.ScrollIntoView(avatarAccount);
            await Task.Delay(200);
            Pump();
            var image = Descendants<Image>(avatarList).FirstOrDefault(control => ReferenceEquals(control.DataContext, avatarAccount));
            Assert(image?.Source is BitmapSource source && source.PixelWidth > 0 && source.PixelHeight > 0, "真实Steam账号头像已解码显示");
        }
        var selected = model.Accounts.Last();
        var id = selected.Account.SteamId;
        model.SelectedAccount = selected;
        Pump();
        Assert(Find<TextBlock>(page, "SelectedAccountTitle").Text == selected.Account.DisplayName
            && Find<TextBlock>(page, "SelectedAccountId").Text == selected.SteamIdLabel,
            "选中账号的名称和标识实际显示在右侧详情");
        Assert(Equals(Find<Wpf.Ui.Controls.Button>(page, "SelectedStarButton").Content, selected.StarLabel)
            && Equals(Find<Wpf.Ui.Controls.Button>(page, "HideAccountButton").Content, selected.HideLabel),
            "选中账号的星标和隐藏操作具有正确文字");
        var list = Find<ListBox>(page, "AccountList");
        var search = Find<Wpf.Ui.Controls.TextBox>(page, "AccountSearch");
        var listScroll = Descendants<ScrollViewer>(list).First();
        var detailScroll = Ancestor<ScrollViewer>(Find<Wpf.Ui.Controls.TextBox>(page, "AccountNote"));
        var searchPosition = search.TranslatePoint(new Point(), window);
        double detailOffset = detailScroll.VerticalOffset;
        Assert(listScroll.ScrollableHeight > 0, "左侧账号列表具有内部滚动范围");
        listScroll.ScrollToBottom();
        await Task.Delay(200);
        Pump();
        Assert(listScroll.VerticalOffset > 0, "左侧账号列表独立滚动");
        Assert(detailScroll.VerticalOffset == detailOffset && search.TranslatePoint(new Point(), window) == searchPosition, "搜索栏和右侧详情保持固定");
        model.Note = "界面验证中的未保存编辑";
        var pendingBinding = model.BindingOptions.Last();
        model.SelectedBinding = pendingBinding;
        search.Text = id;
        Pump();
        Assert(model.SearchText == id && model.Accounts.Count == 1 && model.Accounts[0].Account.SteamId == id, "实际搜索控件双向绑定并精确筛选账号");
        Assert(model.SelectedAccount?.Account.SteamId == id && model.Note == "界面验证中的未保存编辑" && model.SelectedBinding?.FolderName == pendingBinding.FolderName, "搜索保留选中账号及未保存编辑");
        var star = Find<Wpf.Ui.Controls.Button>(page, "SelectedStarButton");
        var starCommand = star.Command as AsyncCommand ?? throw new InvalidOperationException("星标按钮缺少实际异步命令。");
        Checks.Add("星标按钮绑定实际异步命令");
        await starCommand.ExecuteAsync(star.CommandParameter);
        Assert(new SettingsStore(isolatedState).Load().StarredAccounts.Contains(id), "星标已保存到实际SQLite");
        search.Text = "";
        Pump();
        Assert(model.SelectedAccount?.Account.SteamId == id
            && Find<TextBlock>(page, "SelectedAccountTitle").Text == selected.Account.DisplayName,
            "筛选恢复后账号详情仍显示原账号");
        Assert(model.Accounts[0].Account.SteamId == id, "星标账号置顶");
        await starCommand.ExecuteAsync(star.CommandParameter);
        Assert(!new SettingsStore(isolatedState).Load().StarredAccounts.Contains(id), "取消星标已保存到SQLite");
        search.Text = "__mdswitch-no-match__";
        Pump();
        Assert(model.NoAccounts && model.EmptyTitle.Contains("匹配", StringComparison.Ordinal), "无搜索结果显示正确空状态");
        search.Text = "";
        Pump();
    }

    /// <summary>从实际显示中的可视树枚举指定控件，用于检查模板内滚动容器。</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    /// <summary>定位账号编辑器所属滚动容器，避免与列表模板滚动混淆。</summary>
    private static T Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        throw new InvalidOperationException($"没有找到父级控件 {typeof(T).Name}。");
    }

    /// <summary>处理待渲染消息，使截图反映交互后的实际布局。</summary>
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    /// <summary>对显示中的 WPF 可视树作真实渲染并保存 PNG。</summary>
    private static void Screenshot(Window window, string path)
    {
        window.UpdateLayout();
        Pump();
        var target = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Checks.Add($"渲染 {Path.GetFileName(path)}");
    }

    /// <summary>在条件不成立时立即中止，防止产生错误的通过记录。</summary>
    private static void Assert(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException(check);
        Checks.Add(check);
    }
}

/// <summary>复用真实应用资源，同时让探针完全控制独立配置目录及窗口生命周期。</summary>
internal sealed class ProbeApplication : MasterDuelSwitcher.App.App
{
    /// <summary>探针自行创建真实主窗口，避免应用启动事件另外创建默认配置窗口。</summary>
    protected override void OnStartup(StartupEventArgs e) { }
}
