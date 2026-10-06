using System.Windows.Controls;
using System.Windows;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using MasterDuelSwitcher.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace MasterDuelSwitcher.App.Views.Pages;

/// <summary>显示真实 Steam 账号并绑定独立账号业务视图模型的导航页。</summary>
public partial class AccountsPage : Page, INavigableView<AccountsPageViewModel>
{
    /// <summary>供官方导航控件读取的独立账号页视图模型。</summary>
    public AccountsPageViewModel ViewModel { get; }

    /// <summary>初始化账号布局并绑定依赖注入提供的账号页视图模型。</summary>
    public AccountsPage(AccountsPageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = ViewModel;
    }

    /// <summary>将账号图像解码失败的界面事件交给视图模型，恢复当前账号的首字头像。</summary>
    private void OnAvatarFailed(object sender, ExceptionRoutedEventArgs args)
        => ViewModel.ReportAvatarFailure(((AccountItem)((Image)sender).DataContext).Account.SteamId);
}

/// <summary>延迟本地头像完整解码至图像控件测量，使损坏图片触发自然失败事件并恢复首字显示。</summary>
public sealed class DeferredAvatarImageConverter : IValueConverter
{
    /// <summary>将有效路径转换为延迟解码且读取后释放文件的图像，空值使用首字回退。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CreateOptions = BitmapCreateOptions.DelayCreation;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        return image;
    }

    /// <summary>头像绑定只从模型读取路径，反向转换保持原模型值。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
