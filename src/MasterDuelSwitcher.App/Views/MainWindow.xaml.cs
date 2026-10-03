using System.ComponentModel;
using System.Windows;
using MasterDuelSwitcher.App.ViewModels;
using MasterDuelSwitcher.App.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace MasterDuelSwitcher.App.Views;

/// <summary>只负责 XAML 显示和窗口关闭行为的 Fluent 主视图。</summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>通过官方导航服务显示由 DI 解析的独立页面。</summary>
    private readonly INavigationService _navigation;
    /// <summary>仅承载导航标题和共享工作区引用的窗口视图模型。</summary>
    private readonly MainViewModel _viewModel;

    /// <summary>初始化视图并绑定由依赖注入提供的视图模型。</summary>
    public MainWindow(MainViewModel viewModel, INavigationService navigation)
    {
        _viewModel = viewModel;
        _navigation = navigation;
        InitializeComponent();
        DataContext = viewModel;
        _navigation.SetNavigationControl(MainNavigation);
    }

    /// <summary>视图模板就绪后通过官方页面服务显示账号页。</summary>
    private void OnWindowLoaded(object sender, RoutedEventArgs e) => _navigation.Navigate(typeof(AccountsPage));

    /// <summary>只同步已被官方导航选中的页面标题，不执行页面业务。</summary>
    private void OnNavigationSelectionChanged(NavigationView sender, RoutedEventArgs e) => _viewModel.CurrentPage = sender.SelectedItem?.TargetPageTag ?? "Accounts";

    /// <summary>界面正在执行操作时保留窗口，避免事务中途退出。</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && viewModel.Workspace.IsBusy) e.Cancel = true;
    }
}

