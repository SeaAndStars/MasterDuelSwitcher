namespace MasterDuelSwitcher.Core.Models;

/// <summary>一个 Master Duel 账号的下载资源目录。</summary>
public sealed class ResourceProfile
{
    /// <summary>LocalData 下的八位十六进制目录名。</summary>
    public string FolderName { get; init; } = "";
    /// <summary>账号目录的完整路径。</summary>
    public string FullPath { get; init; } = "";
    /// <summary>下载资源目录的完整路径。</summary>
    public string ResourcePath => Path.Combine(FullPath, "0000");
    /// <summary>资源文件总大小；不递归遍历目录链接。</summary>
    public long Bytes { get; init; }
    /// <summary>0000 是否已经是目录链接。</summary>
    public bool IsLinked { get; init; }
    /// <summary>目录链接指向的完整路径。</summary>
    public string? LinkTarget { get; init; }
    /// <summary>供界面选择目录时显示的名称与容量。</summary>
    public string DisplayName => $"{FolderName} · {(IsLinked ? "已共享" : $"{Bytes / 1073741824d:F2} GB")}";
}
