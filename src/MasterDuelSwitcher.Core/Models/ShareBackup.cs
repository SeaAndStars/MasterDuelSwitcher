namespace MasterDuelSwitcher.Core.Models;

/// <summary>一次资源共享操作的持久化事务清单。</summary>
public sealed class ShareBackup
{
    /// <summary>备份唯一标识，与清单文件名一致。</summary>
    public string Id { get; set; } = "";
    /// <summary>事务创建的 UTC 时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>经过校验的游戏安装路径。</summary>
    public string GamePath { get; set; } = "";
    /// <summary>共享来源 0000 的完整路径。</summary>
    public string SourcePath { get; set; } = "";
    /// <summary>每个目标目录的备份和恢复状态。</summary>
    public List<ShareEntry> Entries { get; set; } = [];
    /// <summary>全部目录完成还原后为 true。</summary>
    public bool Restored { get; set; }
    /// <summary>供界面显示的时间和目标数量。</summary>
    public string DisplayName => $"{CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Entries.Count} 个目录{(Restored ? " · 已还原" : "")}";
}

/// <summary>单个资源目录的事务状态，恢复过程中逐条持久化。</summary>
public sealed class ShareEntry
{
    /// <summary>目标账号的原始 0000 路径。</summary>
    public string ResourcePath { get; set; } = "";
    /// <summary>原资源目录被移动到的备份路径。</summary>
    public string BackupPath { get; set; } = "";
    /// <summary>操作前目标是否存在独立资源目录。</summary>
    public bool OriginalExisted { get; set; }
    /// <summary>是否已移动原始目录。</summary>
    public bool Moved { get; set; }
    /// <summary>是否已创建本事务的共享链接。</summary>
    public bool Linked { get; set; }
    /// <summary>是否已还原此条记录。</summary>
    public bool Restored { get; set; }
}
