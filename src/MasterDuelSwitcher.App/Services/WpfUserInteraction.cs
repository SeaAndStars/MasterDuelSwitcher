using System.Windows;
using System.Windows.Controls;
using MasterDuelSwitcher.App.Views;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace MasterDuelSwitcher.App.Services;

/// <summary>使用 Fluent 对话框和 Windows 文件夹选择器实现实际用户交互。</summary>
public sealed class WpfUserInteraction : IUserInteraction
{
    /// <summary>在当前窗口的官方内容对话框宿主中显示通知，启动阶段使用独立 Fluent 宿主。</summary>
    public Task ShowNoticeAsync(string title, string content) => ShowContentDialogAsync(title, content, false);

    /// <summary>以官方内容对话框显示明确确认，取消或关闭时保留现有状态。</summary>
    public async Task<bool> ConfirmAsync(string title, string content) => await ShowContentDialogAsync(title, content, true) == ContentDialogResult.Primary;

    /// <summary>使用相同官方宿主显示通知或确认，并在启动阶段恢复原主窗口引用。</summary>
    private async Task<ContentDialogResult> ShowContentDialogAsync(string title, string content, bool confirmation)
    {
        var previousWindow = Application.Current.MainWindow;
        var host = previousWindow?.FindName("DialogHost") as ContentDialogHost;
        NoticeDialogWindow? temporaryWindow = null;
        if (host is null)
        {
            temporaryWindow = new NoticeDialogWindow();
            host = temporaryWindow.DialogHost;
            temporaryWindow.Show();
        }
        var dialog = new ContentDialog(host)
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, LineHeight = 23 },
            PrimaryButtonText = confirmation ? "继续" : "",
            CloseButtonText = confirmation ? "取消" : "知道了",
            DefaultButton = confirmation ? ContentDialogButton.Primary : ContentDialogButton.Close,
            DialogMaxWidth = 500
        };
        try { return await dialog.ShowAsync(); }
        finally
        {
            if (temporaryWindow is not null)
            {
                Application.Current.MainWindow = previousWindow;
                temporaryWindow.Close();
            }
        }
    }

    /// <summary>显示系统目录选择器，用户取消时返回空引用。</summary>
    public string? PickFolder(string title, string initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = initialDirectory, Multiselect = false };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }
}
