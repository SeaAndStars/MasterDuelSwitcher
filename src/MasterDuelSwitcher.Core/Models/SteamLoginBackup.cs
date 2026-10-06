using Microsoft.Win32;

namespace MasterDuelSwitcher.Core.Models;

/// <summary>Steam 登录配置的本地事务清单，不保存密码或令牌。</summary>
public sealed class SteamLoginBackup
{
    /// <summary>本次事务的唯一标识。</summary>
    public string Id { get; set; } = "";
    /// <summary>备份对应的 Steam 安装目录。</summary>
    public string SteamPath { get; set; } = "";
    /// <summary>备份创建的 UTC 时间。</summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>原始登录配置文件的 SHA256 校验值。</summary>
    public string OriginalSha256 { get; set; } = "";
    /// <summary>事务当前状态：prepared、applied、launched、restoring、rolled-back 或 restored。</summary>
    public string Status { get; set; } = "prepared";
    /// <summary>原始自动登录相关注册表值。</summary>
    public List<SteamRegistryValue> RegistryValues { get; set; } = [];
}

/// <summary>单个自动登录注册表值的存在状态、类型与原始数据。</summary>
public sealed class SteamRegistryValue
{
    /// <summary>注册表值名称，仅允许 AutoLoginUser 和 RememberPassword。</summary>
    public string Name { get; set; } = "";
    /// <summary>备份时该值是否存在。</summary>
    public bool Exists { get; set; }
    /// <summary>注册表原始数据类型。</summary>
    public RegistryValueKind Kind { get; set; } = RegistryValueKind.String;
    /// <summary>字符串及可展开字符串的原始数据。</summary>
    public string? Text { get; set; }
    /// <summary>DWORD 与 QWORD 的原始数据。</summary>
    public long? Number { get; set; }
    /// <summary>多字符串类型的原始数据。</summary>
    public string[]? Texts { get; set; }
    /// <summary>二进制类型的原始数据。</summary>
    public byte[]? Bytes { get; set; }
}
