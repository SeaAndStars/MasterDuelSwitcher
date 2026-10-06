using System.Windows.Controls;
using MasterDuelSwitcher.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace MasterDuelSwitcher.App.Views.Pages;

/// <summary>官方 Fluent 备份还原页面，通过导航接口暴露独立视图模型。</summary>
public partial class BackupsPage : Page, INavigableView<BackupsPageViewModel>
{
    /// <summary>当前页面使用的备份视图模型，独立于导航宿主的 DataContext。</summary>
    public BackupsPageViewModel ViewModel { get; }

    /// <summary>初始化真实 XAML 页面并绑定备份页自己的视图模型。</summary>
    public BackupsPage(BackupsPageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = ViewModel;
    }
}
