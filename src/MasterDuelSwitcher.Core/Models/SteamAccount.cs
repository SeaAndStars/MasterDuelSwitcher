namespace MasterDuelSwitcher.Core.Models;

/// <summary>Steam 已在本机记住的账号，不包含密码或登录令牌。</summary>
public sealed class SteamAccount
{
    /// <summary>Steam 64 位账号标识。</summary>
    public string SteamId { get; init; } = "";
    /// <summary>Steam 登录用户名。</summary>
    public string AccountName { get; init; } = "";
    /// <summary>Steam 显示昵称。</summary>
    public string PersonaName { get; init; } = "";
    /// <summary>Steam 是否记住此账号的登录状态。</summary>
    public bool RememberPassword { get; init; }
    /// <summary>Steam 是否允许自动登录此账号。</summary>
    public bool AllowAutoLogin { get; init; }
    /// <summary>是否为 Steam 最近使用的账号。</summary>
    public bool MostRecent { get; init; }
    /// <summary>界面优先使用昵称，缺失时使用登录名。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(PersonaName) ? AccountName : PersonaName;
}
