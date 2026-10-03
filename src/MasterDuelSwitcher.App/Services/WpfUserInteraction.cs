using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MasterDuelSwitcher.App.Services;

/// <summary>使用 Fluent 对话框和 Windows 文件夹选择器实现实际用户交互。</summary>
public sealed class WpfUserInteraction : IUserInteraction
{
    /// <summary>以当前主窗口为所有者显示可恢复的通知。</summary>
    public async Task ShowNoticeAsync(string title, string content)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Owner = Application.Current.MainWindow,
            Title = title,
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, LineHeight = 23 },
            CloseButtonText = "知道了",
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        await dialog.ShowDialogAsync();
    }

    /// <summary>显示系统目录选择器，用户取消时返回空引用。</summary>
    public string? PickFolder(string title, string initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = initialDirectory, Multiselect = false };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }
}
