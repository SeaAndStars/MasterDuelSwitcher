using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.Core.Services;

namespace MasterDuelSwitcher.App.ViewModels;

/// <summary>独立管理设置页的安装路径编辑、校验、自动发现和主题偏好。</summary>
public sealed class SettingsPageViewModel : ObservableViewModel
{
    /// <summary>用于保存用户明确确认的工具偏好。</summary>
    private readonly ISettingsStore _store;
    /// <summary>在保存前校验 Steam 与游戏安装目录。</summary>
    private readonly ISteamDiscoveryService _discovery;
    /// <summary>提供官方 Fluent 通知和系统文件夹选择器。</summary>
    private readonly IUserInteraction _interaction;
    /// <summary>在成功保存主题偏好后应用官方明暗主题。</summary>
    private readonly IThemeService _theme;
    /// <summary>尚未保存的 Steam 安装路径编辑值。</summary>
    private string _steamPath = "";
    /// <summary>尚未保存的游戏安装路径编辑值。</summary>
    private string _gamePath = "";
    /// <summary>用户选择的深色主题编辑值。</summary>
    private bool _darkTheme;

    /// <summary>供视图绑定共同忙碌状态和固定工具数据位置。</summary>
    public WorkspaceService Workspace { get; }
    /// <summary>当前编辑的 Steam 路径，与其他页实际操作使用的检测路径分离。</summary>
    public string SteamPath { get => _steamPath; set => Set(ref _steamPath, value); }
    /// <summary>当前编辑的游戏路径，与其他页实际操作使用的检测路径分离。</summary>
    public string GamePath { get => _gamePath; set => Set(ref _gamePath, value); }
    /// <summary>当前选择的深色主题偏好。</summary>
    public bool DarkTheme { get => _darkTheme; set => Set(ref _darkTheme, value); }
    /// <summary>选择对应安装目录的独立设置页命令。</summary>
    public AsyncCommand BrowseFolderCommand { get; }
    /// <summary>先校验再保存安装目录的独立设置页命令。</summary>
    public AsyncCommand SaveSettingsCommand { get; }
    /// <summary>清除路径覆盖并重新发现安装的独立设置页命令。</summary>
    public AsyncCommand AutoDetectCommand { get; }
    /// <summary>保存并应用明暗主题的独立设置页命令。</summary>
    public AsyncCommand ToggleThemeCommand { get; }

    /// <summary>注入本页业务边界并订阅共享快照，构造时不执行存储读取或安装发现。</summary>
    public SettingsPageViewModel(WorkspaceService workspace, ISettingsStore store, ISteamDiscoveryService discovery, IUserInteraction interaction, IThemeService theme)
    {
        Workspace = workspace;
        _store = store;
        _discovery = discovery;
        _interaction = interaction;
        _theme = theme;
        BrowseFolderCommand = workspace.CreateCommand(nameof(BrowseFolderCommand), parameter => workspace.RunOperationAsync("正在选择安装目录…", () => { BrowseFolder(parameter as string); return Task.CompletedTask; }));
        SaveSettingsCommand = workspace.CreateCommand(nameof(SaveSettingsCommand), _ => workspace.RunOperationAsync("正在保存并检测安装路径…", SaveSettingsAsync));
        AutoDetectCommand = workspace.CreateCommand(nameof(AutoDetectCommand), _ => workspace.RunOperationAsync("正在自动检测安装路径…", AutoDetectAsync));
        ToggleThemeCommand = workspace.CreateCommand(nameof(ToggleThemeCommand), _ => workspace.RunOperationAsync("正在保存外观设置…", ToggleThemeAsync));
        workspace.DataChanged += OnWorkspaceDataChanged;
        UpdateEditors();
    }

    /// <summary>共享发现完成后更新本页的路径与主题编辑值。</summary>
    private void OnWorkspaceDataChanged(object? sender, EventArgs e) => UpdateEditors();

    /// <summary>优先显示检测出的实际安装目录，缺失时保留已保存的手动覆盖值。</summary>
    private void UpdateEditors()
    {
        SteamPath = string.IsNullOrEmpty(Workspace.ActiveSteamPath) ? Workspace.Settings.SteamPath : Workspace.ActiveSteamPath;
        GamePath = string.IsNullOrEmpty(Workspace.ActiveGamePath) ? Workspace.Settings.GamePath : Workspace.ActiveGamePath;
        DarkTheme = Workspace.Settings.DarkTheme;
    }

    /// <summary>打开对应系统目录选择器，取消时保留当前编辑值。</summary>
    private void BrowseFolder(string? target)
    {
        var steam = target == "Steam";
        var folder = _interaction.PickFolder(steam ? "选择包含 Steam.exe 的目录" : "选择包含 masterduel.exe 的目录", steam ? SteamPath : GamePath);
        if (folder is null) return;
        if (steam) SteamPath = folder;
        else GamePath = folder;
    }

    /// <summary>完整校验修剪后的路径，再保存并刷新所有页面共享的实际安装快照。</summary>
    private async Task SaveSettingsAsync()
    {
        Workspace.EnsureStorageReady();
        var steamPath = SteamPath.Trim();
        var gamePath = GamePath.Trim();
        await Task.Run(() => _discovery.Discover(EmptyToNull(steamPath), EmptyToNull(gamePath)));
        Workspace.Settings.SteamPath = steamPath;
        Workspace.Settings.GamePath = gamePath;
        _store.Save(Workspace.Settings);
        await Workspace.RefreshDataAsync();
        Workspace.AddLog("安装路径已保存。");
    }

    /// <summary>清除两项路径覆盖，保存后重新发现实际安装目录。</summary>
    private async Task AutoDetectAsync()
    {
        Workspace.EnsureStorageReady();
        Workspace.Settings.SteamPath = "";
        Workspace.Settings.GamePath = "";
        _store.Save(Workspace.Settings);
        await Workspace.RefreshDataAsync();
        Workspace.AddLog("已重新自动检测安装路径。");
    }

    /// <summary>先保存主题偏好，再应用官方 Fluent 主题。</summary>
    private Task ToggleThemeAsync()
    {
        Workspace.EnsureStorageReady();
        Workspace.Settings.DarkTheme = DarkTheme;
        _store.Save(Workspace.Settings);
        _theme.Apply(DarkTheme);
        Workspace.AddLog("主题偏好已保存。");
        return Task.CompletedTask;
    }
}
