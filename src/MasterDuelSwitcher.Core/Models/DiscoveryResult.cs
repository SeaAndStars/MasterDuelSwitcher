namespace MasterDuelSwitcher.Core.Models;

/// <summary>Steam 安装、游戏安装与已记住账号的发现结果。</summary>
public sealed class DiscoveryResult
{
    /// <summary>Steam 安装目录；未发现时为空。</summary>
    public string SteamPath { get; init; } = "";
    /// <summary>Master Duel 安装目录；未发现时为空。</summary>
    public string GamePath { get; init; } = "";
    /// <summary>从 loginusers.vdf 读取的账号。</summary>
    public IReadOnlyList<SteamAccount> Accounts { get; init; } = [];
}
