using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MasterDuelSwitcher.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// 测试程序集可直接构造真实 NTFS 文件身份夹具，生产接口保持独立。
[assembly: InternalsVisibleTo("MasterDuelSwitcher.Tests")]

namespace MasterDuelSwitcher.Core.Services;

/// <summary>供界面依赖注入的资源扫描、共享事务和恢复接口。</summary>
public interface IResourceSharingService
{
    /// <summary>扫描指定安装的账号资源目录。</summary>
    IReadOnlyList<ResourceProfile> ScanProfiles(string gamePath);
    /// <summary>共享来源账号资源，已完成相同共享时返回空值。</summary>
    ShareBackup? EnableSharing(string gamePath, string sourceFolder, IEnumerable<string> targetFolders);
    /// <summary>读取指定游戏安装的备份事务。</summary>
    IReadOnlyList<ShareBackup> GetBackups(string gamePath);
    /// <summary>恢复指定持久化事务。</summary>
    void Restore(string backupId);
    /// <summary>归档已失去原备份的单目标事务，保留当前目标并重新共享。</summary>
    ShareBackup? RepairInvalidSharing(string backupId);
}

/// <summary>资源事务依赖的文件系统接口，允许替换磁盘错误、竞态与持久化实现。</summary>
public interface IResourceFileSystem
{
    /// <summary>读取路径本身属性；不存在时返回空值。</summary>
    FileAttributes? Attributes(string path);
    /// <summary>判断目录是否存在。</summary>
    bool DirectoryExists(string path);
    /// <summary>创建事务目录。</summary>
    void CreateDirectory(string path);
    /// <summary>在同一卷内移动目录本身。</summary>
    void MoveDirectory(string source, string target);
    /// <summary>判断普通文件是否存在。</summary>
    bool FileExists(string path);
    /// <summary>读取完整 UTF-8 清单文本。</summary>
    string ReadText(string path);
    /// <summary>读取普通文件长度。</summary>
    long FileLength(string path);
    /// <summary>物化目录的直接条目。</summary>
    string[] Entries(string path);
    /// <summary>枚举状态目录的清单文件。</summary>
    string[] ManifestFiles(string path);
    /// <summary>以独占新文件方式写入并强制刷新到磁盘。</summary>
    void WriteDurable(string path, byte[] bytes);
    /// <summary>原子替换既有清单。</summary>
    void ReplaceFile(string source, string target);
    /// <summary>原子安装首次清单。</summary>
    void MoveFile(string source, string target);
    /// <summary>移除本事务拥有的临时文件。</summary>
    void DeleteFile(string path);
}

/// <summary>使用实际 Windows 文件系统的资源事务适配器。</summary>
public sealed class WindowsResourceFileSystem : IResourceFileSystem
{
    /// <summary>读取不跟随最终重解析点的路径属性。</summary>
    public FileAttributes? Attributes(string path) => ResourcePathValidation.Attributes(path);
    /// <summary>检查目录是否存在。</summary>
    public bool DirectoryExists(string path) => Directory.Exists(path);
    /// <summary>创建清单目录。</summary>
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    /// <summary>移动目录或目录 junction 本身。</summary>
    public void MoveDirectory(string source, string target) => Directory.Move(source, target);
    /// <summary>检查普通文件是否存在。</summary>
    public bool FileExists(string path) => File.Exists(path);
    /// <summary>读取完整清单文本。</summary>
    public string ReadText(string path) => File.ReadAllText(path);
    /// <summary>读取普通文件长度。</summary>
    public long FileLength(string path) => new FileInfo(path).Length;
    /// <summary>物化目录的直接条目。</summary>
    public string[] Entries(string path) => Directory.GetFileSystemEntries(path);
    /// <summary>物化状态目录的直接 JSON 清单。</summary>
    public string[] ManifestFiles(string path) => Directory.GetFiles(path, "*.json", SearchOption.TopDirectoryOnly);
    /// <summary>写入独占临时文件并将所有缓冲强制落盘。</summary>
    public void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(true);
    }
    /// <summary>原子替换已存在的清单。</summary>
    public void ReplaceFile(string source, string target) => File.Replace(source, target, null);
    /// <summary>原子安装首次创建的清单。</summary>
    public void MoveFile(string source, string target) => File.Move(source, target);
    /// <summary>移除本事务的临时文件。</summary>
    public void DeleteFile(string path) => File.Delete(path);
}

/// <summary>事务锁依赖的互斥平台接口，用于模拟超时与进程中断。</summary>
internal interface IResourceMutex : IDisposable
{
    /// <summary>在限定时间内等待锁。</summary>
    bool Wait(TimeSpan timeout);
    /// <summary>释放当前线程持有的锁。</summary>
    void Release();
}

/// <summary>使用 Windows 命名互斥对象的默认事务锁适配器。</summary>
internal sealed class WindowsResourceMutex : IResourceMutex
{
    /// <summary>实际 Windows 互斥句柄。</summary>
    private readonly Mutex mutex;
    /// <summary>打开指定名称的互斥对象。</summary>
    internal WindowsResourceMutex(string name) => mutex = new Mutex(false, name);
    /// <summary>在限定时间内等待互斥对象。</summary>
    public bool Wait(TimeSpan timeout) => mutex.WaitOne(timeout);
    /// <summary>释放当前线程拥有的互斥对象。</summary>
    public void Release() => mutex.ReleaseMutex();
    /// <summary>释放互斥句柄。</summary>
    public void Dispose() => mutex.Dispose();
}

/// <summary>管理下载资源目录的共享和原始目录恢复。</summary>
public sealed class ResourceSharingService : IResourceSharingService
{
    /// <summary>事务清单存放目录，与游戏资源目录隔离。</summary>
    private readonly string stateDirectory;
    /// <summary>游戏运行状态检查，测试时可注入临时状态。</summary>
    private readonly Func<bool> isGameRunning;
    /// <summary>事务所使用的可注入文件系统。</summary>
    private readonly IResourceFileSystem files;
    /// <summary>资源操作与事务步骤的结构化日志，不记录资源内容或登录凭据。</summary>
    private readonly ILogger<ResourceSharingService> logger;
    /// <summary>用于清单持久化的 JSON 序列化设置。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    /// <summary>每份清单记录的原目录稳定身份，随清单扩展字段一起持久化。</summary>
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> originalDirectoryIdentities = new(StringComparer.Ordinal);

    /// <summary>创建资源服务，以应用状态根目录下的 resource-backups 隔离事务；构造阶段不创建目录。</summary>
    public ResourceSharingService(string stateDirectory, Func<bool>? isGameRunning = null, ILogger<ResourceSharingService>? logger = null) : this(stateDirectory, new WindowsResourceFileSystem(), isGameRunning, logger) { }

    /// <summary>通过文件系统接口创建资源服务，供依赖注入和磁盘故障验证。</summary>
    public ResourceSharingService(string stateDirectory, IResourceFileSystem fileSystem, Func<bool>? isGameRunning = null, ILogger<ResourceSharingService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        this.stateDirectory = Path.Combine(ResourcePathValidation.Normalize(stateDirectory), "resource-backups");
        this.isGameRunning = isGameRunning ?? DetectRunningGame;
        files = fileSystem;
        this.logger = logger ?? NullLogger<ResourceSharingService>.Instance;
    }

    /// <summary>扫描严格八位账号目录，计算资源大小时不跟随任意深度的重解析点。</summary>
    public IReadOnlyList<ResourceProfile> ScanProfiles(string gamePath)
    {
        logger.LogInformation("资源扫描开始。");
        try
        {
            var profiles = ScanProfilesCore(gamePath);
            logger.LogInformation("资源扫描完成，共 {ProfileCount} 个账号目录。", profiles.Count);
            return profiles;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "资源扫描失败。");
            throw;
        }
    }

    /// <summary>执行账号资源扫描，个别目录访问失败时保留其余扫描结果。</summary>
    private IReadOnlyList<ResourceProfile> ScanProfilesCore(string gamePath)
    {
        var game = ResourcePathValidation.Game(gamePath);
        var data = Path.Combine(game, "LocalData");
        ResourcePathValidation.EnsureNoReparseAncestors(data);
        if (!files.DirectoryExists(data)) return [];
        var profiles = new List<ResourceProfile>();
        foreach (var account in SafeEntries(data))
        {
            var folder = Path.GetFileName(account);
            if (!ResourcePathValidation.IsAccount(folder)) continue;
            try
            {
                if (files.Attributes(account) is not { } attributes || (attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) continue;
                var resource = Path.Combine(account, "0000");
                var resourceAttributes = files.Attributes(resource);
                var linked = resourceAttributes is { } resourceFlags && (resourceFlags & FileAttributes.ReparsePoint) != 0;
                profiles.Add(new ResourceProfile
                {
                    FolderName = folder,
                    FullPath = account,
                    IsLinked = linked,
                    LinkTarget = linked ? JunctionOperations.GetTarget(resource) : null,
                    Bytes = linked ? 0 : MeasureRealFiles(resource)
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(exception, "资源扫描跳过不可访问账号 {AccountPath}。", account);
            }
        }
        return profiles.OrderBy(profile => profile.FolderName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>将目标账号的 0000 备份到同一账号目录，再创建指向来源的真实 NTFS junction；已有相同共享时返回空值。</summary>
    public ShareBackup? EnableSharing(string gamePath, string sourceFolder, IEnumerable<string> targetFolders)
    {
        logger.LogInformation("资源共享开始。");
        try
        {
            var backup = EnableSharingCore(gamePath, sourceFolder, targetFolders);
            logger.LogInformation("资源共享完成，事务 {BackupId}，新目标 {TargetCount}。", backup?.Id, backup?.Entries.Count ?? 0);
            return backup;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "资源共享失败，原资源和已保存清单保留供还原检查。");
            throw;
        }
    }

    /// <summary>校验来源和目标，先保存意图，再逐项移动原目录并安装暂存 junction。</summary>
    private ShareBackup? EnableSharingCore(string gamePath, string sourceFolder, IEnumerable<string> targetFolders, string? requiredTargetIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(targetFolders);
        EnsureStopped();
        var game = ResourcePathValidation.Game(gamePath);
        ResourcePathValidation.State(stateDirectory, game);
        using var transactionLock = AcquireGameLock(game);
        var source = ResourcePathValidation.Resource(game, sourceFolder);
        ResourcePathValidation.RealDirectory(source);
        if (MeasureRealFiles(source) == 0) throw new InvalidOperationException("来源尚无可用下载资源，请先完成游戏资源更新。");
        var folders = targetFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (folders.Length == 0) throw new ArgumentException("至少选择一个目标账号。", nameof(targetFolders));
        var targets = folders.Select(folder => ResourcePathValidation.Resource(game, folder)).ToArray();
        if (targets.Any(target => ResourcePathValidation.Equal(source, target)))
            throw new ArgumentException("来源账号与目标账号重复。", nameof(targetFolders));
        var active = GetBackups(game).Where(backup => !backup.Restored).ToArray();
        var backup = new ShareBackup { Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow, GamePath = game, SourcePath = source };
        foreach (var target in targets)
        {
            if (active.Any(item => ResourcePathValidation.Equal(item.SourcePath, target)))
                throw new InvalidOperationException($"目标仍是活动共享事务的资源来源，请先还原依赖账号：{target}");
            var attributes = files.Attributes(target);
            if (requiredTargetIdentity is not null && !string.Equals(JunctionOperations.GetDirectoryIdentity(target), requiredTargetIdentity, StringComparison.Ordinal))
                throw new InvalidOperationException($"失效修复归档后目标已被替换，保留第三方目录：{target}");
            var pending = active.Where(item => item.Entries.Any(entry => !entry.Restored && ResourcePathValidation.Equal(entry.ResourcePath, target))).ToArray();
            if (attributes is { } linkFlags && (linkFlags & FileAttributes.ReparsePoint) != 0)
            {
                if (!IsMatchingJunction(target, source) || pending.Any(item => !ResourcePathValidation.Equal(item.SourcePath, source)))
                    throw new InvalidOperationException($"目标已有其他共享链接，请先处理原事务：{target}");
                continue;
            }
            if (pending.Length > 0) throw new InvalidOperationException($"目标存在尚未还原的资源事务：{target}");
            if (attributes is { } directoryFlags && (directoryFlags & FileAttributes.Directory) == 0)
                throw new InvalidOperationException($"目标资源路径被文件占用：{target}");
            var entry = new ShareEntry { ResourcePath = target, BackupPath = target + ".mdbackup-" + backup.Id, OriginalExisted = attributes is not null };
            if (files.Attributes(entry.BackupPath) is not null) throw new InvalidOperationException($"备份路径已经存在：{entry.BackupPath}");
            if (files.Attributes(StagingPath(backup, entry)) is not null) throw new InvalidOperationException($"junction 暂存路径已经存在：{StagingPath(backup, entry)}");
            backup.Entries.Add(entry);
        }
        if (backup.Entries.Count == 0) return null;
        originalDirectoryIdentities[backup.Id] = backup.Entries.Where(entry => entry.OriginalExisted)
            .ToDictionary(entry => entry.ResourcePath, entry => requiredTargetIdentity ?? JunctionOperations.GetDirectoryIdentity(entry.ResourcePath), StringComparer.OrdinalIgnoreCase);
        EnsureStopped();
        // 所有目标和原始存在状态先原子落盘，目录移动前即拥有可恢复的完整意图。
        SaveBackup(backup);
        logger.LogDebug("资源事务 {BackupId} 已保存完整意图，来源 {SourcePath}，目标 {TargetCount}。", backup.Id, source, backup.Entries.Count);
        foreach (var entry in backup.Entries)
        {
            EnsureStopped();
            ValidateMutationPaths(backup, entry);
            var current = files.Attributes(entry.ResourcePath);
            if (entry.OriginalExisted)
            {
                if (current is not { } currentFlags || (currentFlags & FileAttributes.ReparsePoint) != 0 || (currentFlags & FileAttributes.Directory) == 0)
                    throw new InvalidOperationException($"目标在事务期间发生变化，保留清单：{entry.ResourcePath}");
                EnsureOriginalIdentity(backup, entry, entry.ResourcePath);
                files.MoveDirectory(entry.ResourcePath, entry.BackupPath);
                logger.LogDebug("资源事务 {BackupId} 移动原目录 {ResourcePath} 至 {BackupPath}。", backup.Id, entry.ResourcePath, entry.BackupPath);
                entry.Moved = true;
                SaveBackup(backup);
            }
            else if (current is not null) throw new InvalidOperationException($"目标在事务期间被创建，保留现场：{entry.ResourcePath}");
            EnsureStopped();
            ValidateMutationPaths(backup, entry);
            ResourcePathValidation.RealDirectory(source);
            var staging = StagingPath(backup, entry);
            JunctionOperations.Create(staging, source);
            logger.LogDebug("资源事务 {BackupId} 创建暂存 junction {StagingPath}，来源 {SourcePath}。", backup.Id, staging, source);
            SaveBackup(backup);
            EnsureStopped();
            ValidateMutationPaths(backup, entry);
            if (files.Attributes(entry.ResourcePath) is not null || !IsMatchingJunction(staging, source))
                throw new InvalidOperationException($"安装共享链接前路径发生变化，保留事务现场：{entry.ResourcePath}");
            files.MoveDirectory(staging, entry.ResourcePath);
            logger.LogDebug("资源事务 {BackupId} 安装 junction 至 {ResourcePath}。", backup.Id, entry.ResourcePath);
            entry.Linked = true;
            SaveBackup(backup);
        }
        return backup;
    }

    /// <summary>读取与指定安装对应的所有事务；损坏、越界或链接清单以异常报告，原文件保持原样。</summary>
    public IReadOnlyList<ShareBackup> GetBackups(string gamePath)
    {
        logger.LogInformation("资源清单读取开始。");
        try
        {
            var backups = GetBackupsCore(gamePath);
            logger.LogInformation("资源清单读取完成，共 {BackupCount} 个事务。", backups.Count);
            return backups;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "资源清单读取失败，清单文件保持原样。");
            throw;
        }
    }

    /// <summary>读取并校验指定游戏的全部事务清单。</summary>
    private IReadOnlyList<ShareBackup> GetBackupsCore(string gamePath)
    {
        var game = ResourcePathValidation.Game(gamePath);
        ResourcePathValidation.State(stateDirectory, game);
        if (!files.DirectoryExists(stateDirectory)) return [];
        return files.ManifestFiles(stateDirectory)
            .Select(ReadBackup).Where(backup => ResourcePathValidation.Equal(backup.GamePath, game))
            .OrderByDescending(backup => backup.CreatedAt).ToArray();
    }

    /// <summary>以目录实际状态对账恢复；只删除本事务目标一致的 junction，不删除来源或第三方目录。</summary>
    public void Restore(string backupId)
    {
        logger.LogInformation("资源还原开始，事务 {BackupId}。", backupId);
        try
        {
            RestoreCore(backupId);
            logger.LogInformation("资源还原完成，事务 {BackupId}。", backupId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "资源还原失败，事务 {BackupId} 的备份及冲突现场保留。", backupId);
            throw;
        }
    }

    /// <summary>在明确确认旧备份已丢失后，保留现场并重新建立共享。</summary>
    public ShareBackup? RepairInvalidSharing(string backupId)
    {
        logger.LogInformation("失效共享修复开始，旧事务 {BackupId}。", backupId);
        try
        {
            var repaired = RepairInvalidSharingCore(backupId);
            logger.LogInformation("失效共享修复完成，旧事务 {BackupId} 已归档。", backupId);
            return repaired;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "失效共享修复失败，旧历史及当前资源现场保留，事务 {BackupId}。", backupId);
            throw;
        }
    }

    /// <summary>在同一游戏锁内归档单目标失效清单，再通过正常事务保留当前目录重新共享。</summary>
    private ShareBackup? RepairInvalidSharingCore(string backupId)
    {
        ResourcePathValidation.ValidateId(backupId);
        EnsureStopped();
        ResourcePathValidation.EnsureNoReparseAncestors(stateDirectory);
        var manifest = Path.Combine(stateDirectory, backupId + ".json");
        var backup = ReadBackup(manifest);
        var game = ResourcePathValidation.Game(backup.GamePath);
        ResourcePathValidation.State(stateDirectory, game);
        using var transactionLock = AcquireGameLock(game);
        var originalText = files.ReadText(manifest);
        backup = ReadBackup(manifest);
        if (!ResourcePathValidation.Equal(backup.GamePath, game)) throw new InvalidOperationException("修复期间清单游戏路径已改变，保留现场。");
        if (backup.Restored || backup.Entries.Count != 1 || backup.Entries[0].Restored || !backup.Entries[0].OriginalExisted)
            throw new InvalidOperationException("修复仅适用于原备份已删除的单目标未还原事务，请逐条核对其他记录。");
        var entry = backup.Entries[0];
        var currentIdentity = ValidateRepairCandidate(backup, entry);
        var history = Path.Combine(stateDirectory, "invalid-history");
        var archived = Path.Combine(history, backupId + ".json");
        ResourcePathValidation.State(history, game);
        files.CreateDirectory(history);
        ResourcePathValidation.State(history, game);
        if (files.Attributes(archived) is not null) throw new InvalidOperationException($"失效历史已存在，保留当前清单：{archived}");
        EnsureStopped();
        if (!string.Equals(currentIdentity, ValidateRepairCandidate(backup, entry), StringComparison.Ordinal))
            throw new InvalidOperationException("归档前当前目标目录已被替换，保留现场。");
        ReadBackup(manifest);
        if (!string.Equals(originalText, files.ReadText(manifest), StringComparison.Ordinal))
            throw new InvalidOperationException("归档前旧清单内容已改变，保留现场。");
        ResourcePathValidation.State(history, game);
        // 原始字节原子移动到失效历史，保留旧 Restored=false 的真实事实。
        files.MoveFile(manifest, archived);
        logger.LogDebug("失效资源事务 {BackupId} 原样归档至 {ArchivePath}；当前目录将由新事务保留。", backupId, archived);
        // 归档后发生错误时保留新事务现场；旧清单不回搬，不造成两个活动事务重叠。
        return EnableSharingCore(game, Path.GetFileName(Path.GetDirectoryName(backup.SourcePath)!), [Path.GetFileName(Path.GetDirectoryName(entry.ResourcePath)!)], currentIdentity);
    }

    /// <summary>核对旧备份和暂存均缺失、当前目录为不同身份的真实目录，且没有其他活动依赖。</summary>
    private string ValidateRepairCandidate(ShareBackup backup, ShareEntry entry)
    {
        ValidateMutationPaths(backup, entry);
        if (files.Attributes(entry.BackupPath) is not null || files.Attributes(StagingPath(backup, entry)) is not null)
            throw new InvalidOperationException("原备份或暂存目录仍存在，请使用还原或先检查现场。");
        if (files.Attributes(entry.ResourcePath) is not { } attributes || (attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("修复目标必须是当前已存在的独立资源目录，请先刷新检查。");
        var currentIdentity = JunctionOperations.GetDirectoryIdentity(entry.ResourcePath);
        if (string.Equals(currentIdentity, originalDirectoryIdentities[backup.Id][entry.ResourcePath], StringComparison.Ordinal))
            throw new InvalidOperationException("当前目录仍是旧原资源，请使用正常还原完成记录。");
        ResourcePathValidation.RealDirectory(backup.SourcePath);
        var source = ScanProfiles(backup.GamePath).FirstOrDefault(profile => ResourcePathValidation.Equal(profile.ResourcePath, backup.SourcePath));
        if (source is null || source.IsLinked || source.Bytes <= 0)
            throw new InvalidOperationException("来源未成功扫描到独立下载资源，旧记录保持活动状态。");
        var others = GetBackups(backup.GamePath).Where(item => !item.Restored && !string.Equals(item.Id, backup.Id, StringComparison.Ordinal));
        if (others.Any(item => ResourcePathValidation.Equal(item.SourcePath, entry.ResourcePath) ||
            item.Entries.Any(other => !other.Restored && ResourcePathValidation.Equal(other.ResourcePath, entry.ResourcePath))))
            throw new InvalidOperationException("当前目标仍被其他活动事务使用，请先处理其依赖。");
        return currentIdentity;
    }

    /// <summary>按持久清单和实际目录身份恢复资源，保存每一步完成状态。</summary>
    private void RestoreCore(string backupId)
    {
        ResourcePathValidation.ValidateId(backupId);
        EnsureStopped();
        ResourcePathValidation.EnsureNoReparseAncestors(stateDirectory);
        var manifest = Path.Combine(stateDirectory, backupId + ".json");
        var backup = ReadBackup(manifest);
        var game = ResourcePathValidation.Game(backup.GamePath);
        ResourcePathValidation.State(stateDirectory, game);
        using var transactionLock = AcquireGameLock(game);
        backup = ReadBackup(manifest);
        if (backup.Restored) return;
        foreach (var entry in backup.Entries)
        {
            if (entry.Restored) continue;
            EnsureStopped();
            ValidateMutationPaths(backup, entry);
            var staging = StagingPath(backup, entry);
            var stagingAttributes = files.Attributes(staging);
            if (stagingAttributes is { } stagingFlags && (stagingFlags & FileAttributes.ReparsePoint) != 0 && IsMatchingJunction(staging, backup.SourcePath))
            {
                EnsureStopped();
                JunctionOperations.RemoveMatching(staging, backup.SourcePath);
                logger.LogDebug("资源事务 {BackupId} 移除匹配暂存 junction {StagingPath}。", backup.Id, staging);
                SaveBackup(backup);
            }
            // 未完成的普通暂存目录或第三方暂存链接只保留，不占用原始 0000 的回迁路径。
            var targetAttributes = files.Attributes(entry.ResourcePath);
            var backupAttributes = files.Attributes(entry.BackupPath);
            if (backupAttributes is { } backupFlags && ((backupFlags & FileAttributes.ReparsePoint) != 0 || (backupFlags & FileAttributes.Directory) == 0))
                throw new InvalidOperationException($"原始备份被文件或链接替换，保留现场：{entry.BackupPath}");
            if (!entry.OriginalExisted && backupAttributes is not null)
                throw new InvalidOperationException($"没有原始目录的事务出现未知备份，保留现场：{entry.BackupPath}");
            if (backupAttributes is not null) EnsureOriginalIdentity(backup, entry, entry.BackupPath);
            if (targetAttributes is { } targetFlags && (targetFlags & FileAttributes.ReparsePoint) != 0)
            {
                if (!IsMatchingJunction(entry.ResourcePath, backup.SourcePath))
                    throw new InvalidOperationException($"目标已指向其他来源，保留现场：{entry.ResourcePath}");
                if (entry.OriginalExisted && backupAttributes is null)
                    throw new InvalidOperationException($"原始目录备份缺失，保留共享链接：{entry.BackupPath}");
                EnsureStopped();
                ValidateMutationPaths(backup, entry);
                JunctionOperations.RemoveMatching(entry.ResourcePath, backup.SourcePath);
                logger.LogDebug("资源事务 {BackupId} 移除匹配共享 junction {ResourcePath}。", backup.Id, entry.ResourcePath);
                entry.Linked = false;
                SaveBackup(backup);
                targetAttributes = null;
            }
            else if (targetAttributes is { } replacementFlags)
            {
                if ((replacementFlags & FileAttributes.Directory) == 0 || backupAttributes is not null || !entry.OriginalExisted)
                    throw new InvalidOperationException($"目标已出现第三方文件或目录，保留现场：{entry.ResourcePath}");
                EnsureOriginalIdentity(backup, entry, entry.ResourcePath);
                // 原目录未移动，或回迁完成但状态未落盘；无需再次移动或删除。
            }
            if (backupAttributes is not null)
            {
                EnsureStopped();
                ValidateMutationPaths(backup, entry);
                if (files.Attributes(entry.ResourcePath) is not null)
                    throw new InvalidOperationException($"回迁前目标被占用，保留原始备份：{entry.ResourcePath}");
                files.MoveDirectory(entry.BackupPath, entry.ResourcePath);
                logger.LogDebug("资源事务 {BackupId} 移动原备份 {BackupPath} 回 {ResourcePath}。", backup.Id, entry.BackupPath, entry.ResourcePath);
                entry.Moved = false;
                SaveBackup(backup);
            }
            else if (entry.OriginalExisted && targetAttributes is null)
            {
                throw new InvalidOperationException($"原资源目录及其备份均缺失，保留未完成清单：{entry.ResourcePath}");
            }
            entry.Linked = false;
            entry.Moved = false;
            entry.Restored = true;
            logger.LogDebug("资源事务 {BackupId} 条目还原完成 {ResourcePath}。", backup.Id, entry.ResourcePath);
            SaveBackup(backup);
        }
        backup.Restored = backup.Entries.All(entry => entry.Restored);
        SaveBackup(backup);
    }

    /// <summary>再次校验目录祖先和预期备份边界，防止检查后路径改变。</summary>
    private void ValidateMutationPaths(ShareBackup backup, ShareEntry entry)
    {
        ResourcePathValidation.ManifestResource(backup.GamePath, entry.ResourcePath);
        ResourcePathValidation.EnsureNoReparseAncestors(entry.BackupPath);
        var account = Path.GetDirectoryName(entry.ResourcePath)!;
        if (!files.DirectoryExists(account)) throw new DirectoryNotFoundException($"账号目录已被移除：{account}");
    }

    /// <summary>读取并检查单份清单，不将反序列化字段直接作为可信文件路径。</summary>
    private ShareBackup ReadBackup(string path)
    {
        try
        {
            ResourcePathValidation.EnsureNoReparseAncestors(path, false);
            if (files.Attributes(path) is not { } attributes) throw new FileNotFoundException("备份清单不存在。", path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new InvalidDataException("备份清单应为普通文件。");
            using var document = JsonDocument.Parse(files.ReadText(path));
            var backup = document.RootElement.Deserialize<ShareBackup>(JsonOptions) ?? throw new InvalidDataException("备份清单内容为空。");
            if (!document.RootElement.TryGetProperty("FormatVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
                throw new InvalidDataException("资源清单格式版本错误。");
            ResourcePathValidation.ValidateId(backup.Id);
            if (!string.Equals(Path.GetFileName(path), backup.Id + ".json", StringComparison.Ordinal)) throw new InvalidDataException("清单标识与文件名不同。");
            var game = ResourcePathValidation.Normalize(backup.GamePath);
            if (!string.Equals(game, backup.GamePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("清单游戏路径未规范化。");
            ResourcePathValidation.State(stateDirectory, game);
            ResourcePathValidation.ManifestResource(game, backup.SourcePath);
            if (backup.Entries is null || backup.Entries.Count == 0) throw new InvalidDataException("备份清单缺少目标目录。");
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in backup.Entries)
            {
                if (entry is null) throw new InvalidDataException("清单含空目标记录。");
                ResourcePathValidation.ManifestResource(game, entry.ResourcePath);
                if (ResourcePathValidation.Equal(entry.ResourcePath, backup.SourcePath) || !targets.Add(entry.ResourcePath)) throw new InvalidDataException("清单来源与目标重复。");
                if (!string.Equals(entry.BackupPath, entry.ResourcePath + ".mdbackup-" + backup.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("原资源备份路径超出本事务范围。");
                if (backup.Restored && !entry.Restored) throw new InvalidDataException("清单整体恢复状态与条目不一致。");
            }
            var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!document.RootElement.TryGetProperty("OriginalDirectoryIdentities", out var identityData))
                throw new InvalidDataException("清单缺少原目录身份记录。");
            {
                var persisted = identityData.Deserialize<Dictionary<string, string>>(JsonOptions) ?? throw new InvalidDataException("原目录标识记录为空。");
                foreach (var pair in persisted)
                {
                    if (!backup.Entries.Any(entry => entry.OriginalExisted && string.Equals(entry.ResourcePath, pair.Key, StringComparison.OrdinalIgnoreCase)) ||
                        pair.Value is null || !Regex.IsMatch(pair.Value, "\\A[0-9A-Fa-f]{8}:[0-9A-Fa-f]{16}\\z", RegexOptions.CultureInvariant) || !identities.TryAdd(pair.Key, pair.Value))
                        throw new InvalidDataException("原目录标识不符合事务范围。");
                }
            }
            if (identities.Count != backup.Entries.Count(entry => entry.OriginalExisted))
                throw new InvalidDataException("原目录身份记录不完整。");
            if (document.RootElement.TryGetProperty("StagingPaths", out var stagingData))
            {
                var stagingPaths = stagingData.Deserialize<Dictionary<string, string>>(JsonOptions) ?? throw new InvalidDataException("暂存路径记录为空。");
                if (stagingPaths.Count != backup.Entries.Count || backup.Entries.Any(entry => !stagingPaths.TryGetValue(entry.ResourcePath, out var staging) || !string.Equals(staging, StagingPath(backup, entry), StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("junction 暂存路径超出本事务范围。");
            }
            originalDirectoryIdentities[backup.Id] = identities;
            return backup;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            throw new InvalidDataException($"备份清单异常，文件已保留：{path}。{exception.Message}", exception);
        }
    }

    /// <summary>先写入并强制刷新临时文件，再原子替换清单，避免中断留下半份 JSON。</summary>
    private void SaveBackup(ShareBackup backup)
    {
        ResourcePathValidation.State(stateDirectory, backup.GamePath);
        files.CreateDirectory(stateDirectory);
        ResourcePathValidation.EnsureNoReparseAncestors(stateDirectory);
        var path = Path.Combine(stateDirectory, backup.Id + ".json");
        if (files.Attributes(path) is { } attributes && (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException($"清单路径被链接或目录占用：{path}");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var document = JsonSerializer.SerializeToNode(backup, JsonOptions)!.AsObject();
            document["FormatVersion"] = 1;
            document["StagingPaths"] = JsonSerializer.SerializeToNode(backup.Entries.ToDictionary(entry => entry.ResourcePath, entry => StagingPath(backup, entry)), JsonOptions);
            document["OriginalDirectoryIdentities"] = JsonSerializer.SerializeToNode(originalDirectoryIdentities[backup.Id], JsonOptions);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            files.WriteDurable(temporary, bytes);
            if (files.FileExists(path)) files.ReplaceFile(temporary, path);
            else files.MoveFile(temporary, path);
            logger.LogDebug("资源事务 {BackupId} 清单已原子写入 {ManifestPath}，条目 {EntryCount}，全部还原 {Restored}。", backup.Id, path, backup.Entries.Count, backup.Restored);
        }
        finally
        {
            if (files.FileExists(temporary)) files.DeleteFile(temporary);
        }
    }

    /// <summary>核验原目录稳定身份，阻止备份缺失后误认第三方新目录。</summary>
    private void EnsureOriginalIdentity(ShareBackup backup, ShareEntry entry, string observedPath)
    {
        // 启用前完整采集身份，读取清单时完整校验身份；此处只消费已验证的身份记录。
        var expected = originalDirectoryIdentities[backup.Id][entry.ResourcePath];
        if (!string.Equals(JunctionOperations.GetDirectoryIdentity(observedPath), expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"目录已被第三方替换，保留未完成清单：{observedPath}");
    }

    /// <summary>推导本事务唯一 junction 暂存路径，永不把未完成空壳放到实际 0000。</summary>
    private static string StagingPath(ShareBackup backup, ShareEntry entry) => entry.ResourcePath + ".mdjunction-" + backup.Id;

    /// <summary>判断路径是否为指向指定来源的 junction，符号链接及其他重解析类型不匹配。</summary>
    private static bool IsMatchingJunction(string path, string source)
    {
        var target = JunctionOperations.GetTarget(path);
        return target is not null && ResourcePathValidation.Equal(target, source);
    }

    /// <summary>迭代统计普通文件大小，对链接、无访问权限或被同时移除的条目直接跳过。</summary>
    private long MeasureRealFiles(string root)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        long total = 0;
        while (directories.TryPop(out var directory))
        {
            try
            {
                if (files.Attributes(directory) is not { } attributes || (attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) continue;
                foreach (var path in SafeEntries(directory))
                {
                    try
                    {
                        if (files.Attributes(path) is not { } child || (child & FileAttributes.ReparsePoint) != 0) continue;
                        if ((child & FileAttributes.Directory) != 0) directories.Push(path);
                        else total = checked(total + files.FileLength(path));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        logger.LogDebug(exception, "资源容量统计跳过不可访问条目 {ResourcePath}。", path);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(exception, "资源容量统计跳过不可访问目录 {ResourcePath}。", directory);
            }
        }
        return total;
    }

    /// <summary>物化目录条目后返回，枚举期间遇到权限或并发移除则返回空集合。</summary>
    private string[] SafeEntries(string path)
    {
        try { return files.Entries(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "资源目录枚举失败，跳过 {ResourcePath}。", path);
            return [];
        }
    }

    /// <summary>每次资源变更前确认 Master Duel 已退出。</summary>
    private void EnsureStopped()
    {
        if (isGameRunning()) throw new InvalidOperationException("请先退出 Master Duel，再进行资源共享或还原。");
    }

    /// <summary>查询真实 Master Duel 进程，并释放全部进程句柄。</summary>
    private static bool DetectRunningGame()
    {
        var processes = Process.GetProcessesByName("masterduel");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    /// <summary>以规范化游戏路径生成跨实例命名互斥锁，串行化共享和还原。</summary>
    private static GameTransactionLock AcquireGameLock(string game) => new(game);

    /// <summary>同一游戏安装的跨进程资源事务互斥锁。</summary>
    internal sealed class GameTransactionLock : IDisposable
    {
        /// <summary>当前拥有的 Windows 命名互斥对象。</summary>
        private readonly IResourceMutex mutex;

        /// <summary>等待资源事务锁，接管退出进程留下的锁后允许观察清单恢复。</summary>
        internal GameTransactionLock(string game, IResourceMutex? injectedMutex = null)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(game.ToUpperInvariant())));
            mutex = injectedMutex ?? new WindowsResourceMutex(@"Local\MasterDuelSwitcher.Resources." + key);
            try
            {
                if (!mutex.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new InvalidOperationException("同一游戏的资源事务正在执行，请稍后重试。");
                }
            }
            catch (AbandonedMutexException)
            {
                // 退出进程遗留的锁已由当前线程获得，后续依照持久清单检查真实目录。
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }

        /// <summary>释放当前事务持有的命名锁。</summary>
        public void Dispose()
        {
            mutex.Release();
            mutex.Dispose();
        }
    }
}
