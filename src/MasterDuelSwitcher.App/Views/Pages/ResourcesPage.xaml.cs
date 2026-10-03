using System.Windows.Controls;
using MasterDuelSwitcher.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace MasterDuelSwitcher.App.Views.Pages;

/// <summary>官方 Fluent 资源页面，通过导航接口暴露独立视图模型。</summary>
public partial class ResourcesPage : Page, INavigableView<ResourcesPageViewModel>
{
    /// <summary>当前页面自己的来源选择与共享业务视图模型。</summary>
    public ResourcesPageViewModel ViewModel { get; }

    /// <summary>初始化页面并绑定资源页自己的视图模型。</summary>
    public ResourcesPage(ResourcesPageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = ViewModel;
    }
}
