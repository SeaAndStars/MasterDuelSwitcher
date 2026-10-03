using System.Windows;
using System.Windows.Controls;
using MasterDuelSwitcher.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace MasterDuelSwitcher.App.Views.Pages;

/// <summary>只承载免费开包模型和官方 Fluent 控件的独立页面。</summary>
public partial class FreePacksPage : Page, INavigableView<FreePacksPageViewModel>
{
    /// <summary>由应用容器提供的独立免费开包模型。</summary>
    public FreePacksPageViewModel ViewModel { get; }

    /// <summary>初始化页面并绑定独立模型，保持构造时不操作游戏。</summary>
    public FreePacksPage(FreePacksPageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>页面离开可见导航内容时，只转发停止自身任务的请求。</summary>
    private void OnPageUnloaded(object sender, RoutedEventArgs args) => ViewModel.Stop();
}
