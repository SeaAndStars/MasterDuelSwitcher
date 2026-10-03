using MasterDuelSwitcher.Core.Models;

namespace MasterDuelSwitcher.App;

/// <summary>账号列表的显示数据，仅包含账号标识、备注和资源绑定。</summary>
public sealed class AccountItem
{
    /// <summary>Steam 发现服务返回的真实账号。</summary>
    public required SteamAccount Account { get; init; }
    /// <summary>用户保存的本地备注。</summary>
    public string Note { get; init; } = "";
    /// <summary>用户手工确认的资源目录名。</summary>
    public string ResourceFolder { get; init; } = "";
    /// <summary>账号是否仅在工具列表中隐藏。</summary>
    public bool IsHidden { get; init; }
    /// <summary>列表优先显示用户备注，其次显示 Steam 昵称。</summary>
    public string Title => string.IsNullOrWhiteSpace(Note) ? Account.DisplayName : Note;
    /// <summary>显示 Steam 用户名；隐藏状态只影响工具列表。</summary>
    public string Subtitle => IsHidden ? $"{Account.AccountName} · 已隐藏" : Account.AccountName;
    /// <summary>使用名称首字作为本地头像，不访问网络头像服务。</summary>
    public string Initial => string.IsNullOrWhiteSpace(Title) ? "S" : Title[..1].ToUpperInvariant();
    /// <summary>显示 Steam 最近使用状态或手动绑定状态。</summary>
    public string Status => Account.MostRecent ? "最近使用" : string.IsNullOrEmpty(ResourceFolder) ? "待绑定资源" : $"资源 {ResourceFolder}";
    /// <summary>供账号详情显示的本机 Steam 登录状态提示。</summary>
    public string LoginState => Account.RememberPassword ? "Steam 已记住此账号；会话验证由 Steam 处理。" : "Steam 尚未记住登录状态；启动后按提示登录。";
    /// <summary>账号详情中的 Steam 标识标签。</summary>
    public string SteamIdLabel => $"SteamID  {Account.SteamId}";
    /// <summary>当前账号隐藏操作的按钮标签。</summary>
    public string HideLabel => IsHidden ? "取消隐藏" : "隐藏";
}

/// <summary>账号资源下拉列表的一项，空目录表示暂不绑定。</summary>
public sealed class ResourceBindingOption
{
    /// <summary>LocalData 账号目录名，空字符串表示未绑定。</summary>
    public string FolderName { get; init; } = "";
    /// <summary>向用户展示的目录名称和容量。</summary>
    public string DisplayName { get; init; } = "未绑定资源目录";
}
