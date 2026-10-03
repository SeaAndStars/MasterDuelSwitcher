using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MasterDuelSwitcher.App;
using MasterDuelSwitcher.App.Views;
using MasterDuelSwitcher.Core.Services;
using MasterDuelSwitcher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MasterDuelSwitcher.UiProbe;

/// <summary>打开实际 WPF 窗口，验证导航、设置保存及渲染，不触发真实账号切换。</summary>
public static class Program
{
    /// <summary>已完成检查的名称列表。</summary>
    private static readonly List<string> Checks = [];

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
        app.MainWindow = window;
        var result = 1;
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                await viewModel.InitializeAsync();
                await WaitForReady(window);
                Assert(window.ActualWidth >= 900 && window.ActualHeight >= 620, "实际窗口尺寸");
                Screenshot(window, Path.Combine(output, "accounts-light.png"));
                Navigate(window, "ResourcesNav", "ResourcesPage");
                Screenshot(window, Path.Combine(output, "resources-light.png"));
                Navigate(window, "BackupsNav", "BackupsPage");
                Navigate(window, "SettingsNav", "SettingsPage");
                Screenshot(window, Path.Combine(output, "settings-light.png"));
                var theme = Find<System.Windows.Controls.Primitives.ToggleButton>(window, "DarkThemeToggle");
                theme.IsChecked = true;
                await viewModel.ToggleThemeCommand.ExecuteAsync();
                await Task.Delay(200);
                Pump();
                Assert(new SettingsStore(isolatedState).Load().DarkTheme, "深色主题配置已保存");
                Screenshot(window, Path.Combine(output, "settings-dark.png"));
                Navigate(window, "AccountsNav", "AccountsPage");
                window.Width = 900;
                window.Height = 620;
                Pump();
                Screenshot(window, Path.Combine(output, "accounts-compact-dark.png"));
                Assert(Find<TextBlock>(window, "StatusText").IsVisible, "状态区域可见");
                File.WriteAllText(Path.Combine(output, "ui-verification.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Checks,
                    WpfUiVersion = "4.3.0",
                    ActualWindowType = window.GetType().BaseType?.FullName,
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

    /// <summary>等待实际异步发现结束，避免对启动动画期间的状态作断言。</summary>
    private static async Task WaitForReady(MainWindow window)
    {
        var workspace = Find<FrameworkElement>(window, "Workspace");
        for (var attempt = 0; attempt < 150 && !workspace.IsEnabled; attempt++) await Task.Delay(100);
        Assert(workspace.IsEnabled, "启动发现已结束");
        await Task.Delay(250);
        Pump();
    }

    /// <summary>执行窗口实际按钮绑定的导航命令并核对页面可见性。</summary>
    private static void Navigate(MainWindow window, string buttonName, string pageName)
    {
        var button = Find<Button>(window, buttonName);
        if (button.Command?.CanExecute(button.CommandParameter) == true) button.Command.Execute(button.CommandParameter);
        else throw new InvalidOperationException($"导航命令未就绪：{buttonName}");
        Pump();
        Assert(Find<FrameworkElement>(window, pageName).Visibility == Visibility.Visible, $"导航 {pageName}");
    }

    /// <summary>查找 XAML 命名控件，缺失时报告具体控件名。</summary>
    private static T Find<T>(MainWindow window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException($"控件缺失或类型错误：{name}");

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
