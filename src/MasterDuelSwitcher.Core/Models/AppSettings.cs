namespace MasterDuelSwitcher.Core.Models;

/// <summary>工具的本地偏好设置，只保存路径、账号标识与备注。</summary>
public sealed class AppSettings
{
    /// <summary>用户手动指定的 Steam 安装路径。</summary>
    public string SteamPath { get; set; } = "";
    /// <summary>用户手动指定的游戏安装路径。</summary>
    public string GamePath { get; set; } = "";
    /// <summary>用户选定的共享资源来源账号目录名。</summary>
    public string SourceProfile { get; set; } = "";
    /// <summary>SteamId 到资源目录名的人工确认映射。</summary>
    public Dictionary<string, string> AccountBindings { get; set; } = [];
    /// <summary>SteamId 到用户自定义备注的映射。</summary>
    public Dictionary<string, string> AccountNotes { get; set; } = [];
    /// <summary>仅在工具中隐藏的账号标识。</summary>
    public HashSet<string> HiddenAccounts { get; set; } = [];
    /// <summary>用户星标的账号标识，账号暂时离线后仍保留。</summary>
    public HashSet<string> StarredAccounts { get; set; } = [];
    /// <summary>界面是否使用深色主题。</summary>
    public bool DarkTheme { get; set; }
}
