using System.ComponentModel;
using MasterDuelSwitcher.App.ViewModels;
using Wpf.Ui.Controls;

namespace MasterDuelSwitcher.App.Views;

/// <summary>只负责 XAML 显示和窗口关闭行为的 Fluent 主视图。</summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>初始化视图并绑定由依赖注入提供的视图模型。</summary>
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>界面正在执行操作时保留窗口，避免事务中途退出。</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is MainViewModel { IsBusy: true }) e.Cancel = true;
    }
}

