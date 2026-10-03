using MasterDuelSwitcher.Core.Models;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>管理独立数据目录中的应用偏好和 Steam 账号关系数据。</summary>
public interface ISettingsStore
{
    /// <summary>账号数据库和工具状态所在的完整目录。</summary>
    string StateDirectory { get; }
    /// <summary>读取应用偏好和账号自定义字段；损坏数据应报告错误。</summary>
    AppSettings Load();
    /// <summary>通过单个事务保存应用偏好、备注、资源映射和隐藏状态。</summary>
    void Save(AppSettings settings);
    /// <summary>同步当前 Steam 账号元数据并保留人工维护的账号字段。</summary>
    void SynchronizeAccounts(IReadOnlyList<SteamAccount> accounts);
    /// <summary>返回仍存在于 Steam 中的账号，最近使用账号优先。</summary>
    IReadOnlyList<SteamAccount> GetAccounts();
}
