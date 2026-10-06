using System.Windows.Controls;
using MasterDuelSwitcher.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace MasterDuelSwitcher.App.Views.Pages;

/// <summary>设置页只负责展示独立设置模型并提供官方导航视图契约。</summary>
public partial class SettingsPage : Page, INavigableView<SettingsPageViewModel>
{
    /// <summary>由依赖注入提供的独立设置页模型。</summary>
    public SettingsPageViewModel ViewModel { get; }

    /// <summary>初始化官方 Fluent 设置控件并绑定独立视图模型。</summary>
    public SettingsPage(SettingsPageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }
}
