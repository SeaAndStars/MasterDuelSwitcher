namespace MasterDuelSwitcher.App.Services;

/// <summary>将用户通知和文件夹选择隔离为可替换的界面交互接口。</summary>
public interface IUserInteraction
{
    /// <summary>显示可恢复的操作提示。</summary>
    Task ShowNoticeAsync(string title, string content);
    /// <summary>选择安装目录；用户取消时返回空引用。</summary>
    string? PickFolder(string title, string initialDirectory);
}

/// <summary>将 Fluent 外观应用隔离为可测试的主题服务。</summary>
public interface IThemeService
{
    /// <summary>应用指定的明暗主题。</summary>
    void Apply(bool dark);
}
