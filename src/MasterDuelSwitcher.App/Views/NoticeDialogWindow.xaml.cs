using Wpf.Ui.Controls;

namespace MasterDuelSwitcher.App.Views;

/// <summary>主窗口尚未就绪时承载同一 Fluent 内容对话框的独立视图。</summary>
public partial class NoticeDialogWindow : FluentWindow
{
    /// <summary>初始化只含标题栏和官方内容对话框宿主的视图。</summary>
    public NoticeDialogWindow() => InitializeComponent();
}
