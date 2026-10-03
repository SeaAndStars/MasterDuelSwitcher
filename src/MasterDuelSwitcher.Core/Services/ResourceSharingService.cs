using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MasterDuelSwitcher.Core.Models;

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
}

/// <summary>管理下载资源目录的共享和原始目录恢复。</summary>
public sealed class ResourceSharingService : IResourceSharingService
{
    /// <summary>事务清单存放目录，与游戏资源目录隔离。</summary>
    private readonly string stateDirectory;
    /// <summary>游戏运行状态检查，测试时可注入临时状态。</summary>
    private readonly Func<bool> isGameRunning;
    /// <summary>用于清单持久化的 JSON 序列化设置。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    /// <summary>每份清单记录的原目录稳定身份，随清单扩展字段一起持久化。</summary>
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> originalDirectoryIdentities = new(StringComparer.Ordinal);

    /// <summary>创建资源服务，以应用状态根目录下的 resource-backups 隔离事务；构造阶段不创建目录。</summary>
    public ResourceSharingService(string stateDirectory, Func<bool>? isGameRunning = null)
    {
        this.stateDirectory = Path.Combine(ResourcePathValidation.Normalize(stateDirectory), "resource-backups");
        this.isGameRunning = isGameRunning ?? DetectRunningGame;
    }

    /// <summary>扫描严格八位账号目录，计算资源大小时不跟随任意深度的重解析点。</summary>
    public IReadOnlyList<ResourceProfile> ScanProfiles(string gamePath)
    {
        var game = ResourcePathValidation.Game(gamePath);
        var data = Path.Combine(game, "LocalData");
        ResourcePathValidation.EnsureNoReparseAncestors(data);
        if (!Directory.Exists(data)) return [];
        var profiles = new List<ResourceProfile>();
        foreach (var account in SafeEntries(data))
        {
            var folder = Path.GetFileName(account);
            if (!ResourcePathValidation.IsAccount(folder)) continue;
            try
            {
                var attributes = ResourcePathValidation.Attributes(account);
                if (attributes is null || (attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) continue;
                var resource = Path.Combine(account, "0000");
                var resourceAttributes = ResourcePathValidation.Attributes(resource);
                var linked = resourceAttributes is not null && (resourceAttributes & FileAttributes.ReparsePoint) != 0;
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
                // 个别账号无访问权限时跳过该账号，其余账号仍可扫描。
            }
        }
        return profiles.OrderBy(profile => profile.FolderName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>将目标账号的 0000 备份到同一账号目录，再创建指向来源的真实 NTFS junction；已有相同共享时返回空值。</summary>
    public ShareBackup? EnableSharing(string gamePath, string sourceFolder, IEnumerable<string> targetFolders)
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
            var attributes = ResourcePathValidation.Attributes(target);
            var pending = active.Where(item => item.Entries.Any(entry => !entry.Restored && ResourcePathValidation.Equal(entry.ResourcePath, target))).ToArray();
            if (attributes is not null && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (!IsMatchingJunction(target, source) || pending.Any(item => !ResourcePathValidation.Equal(item.SourcePath, source)))
                    throw new InvalidOperationException($"目标已有其他共享链接，请先处理原事务：{target}");
                continue;
            }
            if (pending.Length > 0) throw new InvalidOperationException($"目标存在尚未还原的资源事务：{target}");
            if (attributes is not null && (attributes & FileAttributes.Directory) == 0)
                throw new InvalidOperationException($"目标资源路径被文件占用：{target}");
            var entry = new ShareEntry { ResourcePath = target, BackupPath = target + ".mdbackup-" + backup.Id, OriginalExisted = attributes is not null };
            if (ResourcePathValidation.Attributes(entry.BackupPath) is not null) throw new InvalidOperationException($"备份路径已经存在：{entry.BackupPath}");
            if (ResourcePathValidation.Attributes(StagingPath(backup, entry)) is not null) throw new InvalidOperationException($"junction 暂存路径已经存在：{StagingPath(backup, entry)}");
            backup.Entries.Add(entry);
        }
        if (backup.Entries.Count == 0) return null;
        originalDirectoryIdentities[backup.Id] = backup.Entries.Where(entry => entry.OriginalExisted)
            .ToDictionary(entry => entry.ResourcePath, entry => JunctionOperations.GetDirectoryIdentity(entry.ResourcePath), StringComparer.OrdinalIgnoreCase);
        EnsureStopped();
        // 所有目标和原始存在状态先原子落盘，目录移动前即拥有可恢复的完整意图。
        SaveBackup(backup);
        foreach (var entry in backup.Entries)
        {
            EnsureStopped();
            ValidateMutationPaths(backup, entry);
            var current = ResourcePathValidation.Attributes(entry.ResourcePath);
            if (entry.OriginalExisted)
            {
                if (current is null || (current & FileAttributes.ReparsePoint) != 0 || (current & FileAttributes.Directory) == 0)
                    throw new InvalidOperationException($"目标在事务期间发生变化，保留清单：{entry.ResourcePath}");
                EnsureOriginalIdentity(backup, entry, entry.ResourcePath);
                Directory.Move(entry.ResourcePath, entry.BackupPath);
                entry.Moved = true;
                SaveBackup(backup);
            }
            else if (current is not null) throw new InvalidOperationException($"目标在事务期间被创建，保留现场：{entry.ResourcePath}");
            EnsureStopped();
            ValidateMutationPaths(backup, entry);
            ResourcePathValidation.RealDirectory(source);
            var staging = StagingPath(backup, entry);
            JunctionOperations.Create(staging, source);
            SaveBackup(backup);
            EnsureStopped();
            ValidateMutationPaths(backup, entry);
            if (ResourcePathValidation.Attributes(entry.ResourcePath) is not null || !IsMatchingJunction(staging, source))
                throw new InvalidOperationException($"安装共享链接前路径发生变化，保留事务现场：{entry.ResourcePath}");
            Directory.Move(staging, entry.ResourcePath);
            entry.Linked = true;
            SaveBackup(backup);
        }
        return backup;
    }

    /// <summary>读取与指定安装对应的所有事务；损坏、越界或链接清单以异常报告，原文件保持原样。</summary>
    public IReadOnlyList<ShareBackup> GetBackups(string gamePath)
    {
        var game = ResourcePathValidation.Game(gamePath);
        ResourcePathValidation.State(stateDirectory, game);
        if (!Directory.Exists(stateDirectory)) return [];
        return Directory.EnumerateFiles(stateDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(ReadBackup).Where(backup => ResourcePathValidation.Equal(backup.GamePath, game))
            .OrderByDescending(backup => backup.CreatedAt).ToArray();
    }

    /// <summary>以目录实际状态对账恢复；只删除本事务目标一致的 junction，不删除来源或第三方目录。</summary>
    public void Restore(string backupId)
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
            var stagingAttributes = ResourcePathValidation.Attributes(staging);
            if (stagingAttributes is not null && (stagingAttributes & FileAttributes.ReparsePoint) != 0 && IsMatchingJunction(staging, backup.SourcePath))
            {
                EnsureStopped();
                JunctionOperations.RemoveMatching(staging, backup.SourcePath);
                SaveBackup(backup);
            }
            // 未完成的普通暂存目录或第三方暂存链接只保留，不占用原始 0000 的回迁路径。
            var targetAttributes = ResourcePathValidation.Attributes(entry.ResourcePath);
            var backupAttributes = ResourcePathValidation.Attributes(entry.BackupPath);
            if (backupAttributes is not null && ((backupAttributes & FileAttributes.ReparsePoint) != 0 || (backupAttributes & FileAttributes.Directory) == 0))
                throw new InvalidOperationException($"原始备份被文件或链接替换，保留现场：{entry.BackupPath}");
            if (backupAttributes is not null) EnsureOriginalIdentity(backup, entry, entry.BackupPath);
            if (!entry.OriginalExisted && backupAttributes is not null)
                throw new InvalidOperationException($"没有原始目录的事务出现未知备份，保留现场：{entry.BackupPath}");
            if (targetAttributes is not null && (targetAttributes & FileAttributes.ReparsePoint) != 0)
            {
                if (!IsMatchingJunction(entry.ResourcePath, backup.SourcePath))
                    throw new InvalidOperationException($"目标已指向其他来源，保留现场：{entry.ResourcePath}");
                if (entry.OriginalExisted && backupAttributes is null)
                    throw new InvalidOperationException($"原始目录备份缺失，保留共享链接：{entry.BackupPath}");
                EnsureStopped();
                ValidateMutationPaths(backup, entry);
                JunctionOperations.RemoveMatching(entry.ResourcePath, backup.SourcePath);
                entry.Linked = false;
                SaveBackup(backup);
                targetAttributes = null;
            }
            else if (targetAttributes is not null)
            {
                if ((targetAttributes & FileAttributes.Directory) == 0 || backupAttributes is not null || !entry.OriginalExisted)
                    throw new InvalidOperationException($"目标已出现第三方文件或目录，保留现场：{entry.ResourcePath}");
                EnsureOriginalIdentity(backup, entry, entry.ResourcePath);
                // 原目录未移动，或回迁完成但状态未落盘；无需再次移动或删除。
            }
            if (backupAttributes is not null)
            {
                EnsureStopped();
                ValidateMutationPaths(backup, entry);
                if (ResourcePathValidation.Attributes(entry.ResourcePath) is not null)
                    throw new InvalidOperationException($"回迁前目标被占用，保留原始备份：{entry.ResourcePath}");
                Directory.Move(entry.BackupPath, entry.ResourcePath);
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
            SaveBackup(backup);
        }
        backup.Restored = backup.Entries.All(entry => entry.Restored);
        SaveBackup(backup);
    }

    /// <summary>再次校验目录祖先和预期备份边界，防止检查后路径改变。</summary>
    private static void ValidateMutationPaths(ShareBackup backup, ShareEntry entry)
    {
        ResourcePathValidation.ManifestResource(backup.GamePath, entry.ResourcePath);
        ResourcePathValidation.EnsureNoReparseAncestors(entry.BackupPath);
        var account = Path.GetDirectoryName(entry.ResourcePath)!;
        if (!Directory.Exists(account)) throw new DirectoryNotFoundException($"账号目录已被移除：{account}");
    }

    /// <summary>读取并检查单份清单，不将反序列化字段直接作为可信文件路径。</summary>
    private ShareBackup ReadBackup(string path)
    {
        try
        {
            ResourcePathValidation.EnsureNoReparseAncestors(path, false);
            if (ResourcePathValidation.Attributes(path) is not { } attributes) throw new FileNotFoundException("备份清单不存在。", path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new InvalidDataException("备份清单应为普通文件。");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
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
                        pair.Value is null || pair.Value.Length != 25 || pair.Value[8] != ':' || pair.Value.Where(character => character != ':').Any(character => !Uri.IsHexDigit(character)) || !identities.TryAdd(pair.Key, pair.Value))
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
        Directory.CreateDirectory(stateDirectory);
        ResourcePathValidation.EnsureNoReparseAncestors(stateDirectory);
        var path = Path.Combine(stateDirectory, backup.Id + ".json");
        if (ResourcePathValidation.Attributes(path) is { } attributes && (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException($"清单路径被链接或目录占用：{path}");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var document = JsonSerializer.SerializeToNode(backup, JsonOptions)!.AsObject();
            document["FormatVersion"] = 1;
            document["StagingPaths"] = JsonSerializer.SerializeToNode(backup.Entries.ToDictionary(entry => entry.ResourcePath, entry => StagingPath(backup, entry)), JsonOptions);
            if (originalDirectoryIdentities.TryGetValue(backup.Id, out var identities))
                document["OriginalDirectoryIdentities"] = JsonSerializer.SerializeToNode(identities, JsonOptions);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>核验原目录稳定身份，阻止备份缺失后误认第三方新目录。</summary>
    private void EnsureOriginalIdentity(ShareBackup backup, ShareEntry entry, string observedPath)
    {
        if (originalDirectoryIdentities.TryGetValue(backup.Id, out var identities) && identities.TryGetValue(entry.ResourcePath, out var expected))
        {
            if (!string.Equals(JunctionOperations.GetDirectoryIdentity(observedPath), expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"目录已被第三方替换，保留未完成清单：{observedPath}");
        }
        else
        {
            throw new InvalidDataException($"清单缺少原目录标识，保留现场：{observedPath}");
        }
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
    private static long MeasureRealFiles(string root)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        long total = 0;
        while (directories.TryPop(out var directory))
        {
            try
            {
                var attributes = ResourcePathValidation.Attributes(directory);
                if (attributes is null || (attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) continue;
                foreach (var path in SafeEntries(directory))
                {
                    try
                    {
                        var child = ResourcePathValidation.Attributes(path);
                        if (child is null || (child & FileAttributes.ReparsePoint) != 0) continue;
                        if ((child & FileAttributes.Directory) != 0) directories.Push(path);
                        else total = checked(total + new FileInfo(path).Length);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    /// <summary>物化目录条目后返回，枚举期间遇到权限或并发移除则返回空集合。</summary>
    private static string[] SafeEntries(string path)
    {
        try { return Directory.GetFileSystemEntries(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
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
    private sealed class GameTransactionLock : IDisposable
    {
        /// <summary>当前拥有的 Windows 命名互斥对象。</summary>
        private readonly Mutex mutex;

        /// <summary>等待资源事务锁，接管退出进程留下的锁后允许观察清单恢复。</summary>
        internal GameTransactionLock(string game)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(game.ToUpperInvariant())));
            mutex = new Mutex(false, @"Local\MasterDuelSwitcher.Resources." + key);
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                {
                    mutex.Dispose();
                    throw new InvalidOperationException("同一游戏的资源事务正在执行，请稍后重试。");
                }
            }
            catch (AbandonedMutexException)
            {
                // 退出进程遗留的锁已由当前线程获得，后续依照持久清单检查真实目录。
            }
        }

        /// <summary>释放当前事务持有的命名锁。</summary>
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
