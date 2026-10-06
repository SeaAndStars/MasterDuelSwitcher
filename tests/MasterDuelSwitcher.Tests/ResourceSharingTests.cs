using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>仅使用临时目录验证资源共享、恢复和路径边界的真实文件测试。</summary>
public sealed class ResourceSharingTests : IDisposable
{
    /// <summary>本次测试隔离目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher.ResourceTests", Guid.NewGuid().ToString("N"));
    /// <summary>临时游戏安装目录。</summary>
    private readonly string game;
    /// <summary>临时事务清单目录。</summary>
    private readonly string state;
    /// <summary>被测资源服务。</summary>
    private readonly ResourceSharingService service;

    /// <summary>创建两个具有不同内容的账号和独立存档。</summary>
    public ResourceSharingTests()
    {
        game = Path.Combine(root, "game");
        state = Path.Combine(root, "state");
        Directory.CreateDirectory(Resource("1234ABCD"));
        Directory.CreateDirectory(Resource("5678EF90"));
        File.WriteAllText(Path.Combine(Resource("1234ABCD"), "bundle.bin"), "main-resource-v1");
        File.WriteAllText(Path.Combine(Resource("5678EF90"), "bundle.bin"), "target-original-v1");
        Directory.CreateDirectory(Path.Combine(game, "LocalSave"));
        File.WriteAllText(Path.Combine(game, "LocalSave", "save.bin"), "independent-save");
        service = new ResourceSharingService(state, () => false);
    }

    /// <summary>缺少真实 junction 或错误删除备份时，此测试会失败。</summary>
    [Fact]
    public void EnableAndRestorePreserveOriginalDataAndShareRealWrites()
    {
        var originalHash = Hash(Path.Combine(Resource("5678EF90"), "bundle.bin"));
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        var entry = Assert.Single(backup.Entries);
        Assert.True((File.GetAttributes(entry.ResourcePath) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal("main-resource-v1", File.ReadAllText(Path.Combine(entry.ResourcePath, "bundle.bin")));
        File.WriteAllText(Path.Combine(entry.ResourcePath, "through-link.bin"), "shared-write");
        Assert.Equal("shared-write", File.ReadAllText(Path.Combine(Resource("1234ABCD"), "through-link.bin")));
        Assert.Equal(originalHash, Hash(Path.Combine(entry.BackupPath, "bundle.bin")));
        Assert.Null(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.Single(service.GetBackups(game));
        service.Restore(backup.Id);
        service.Restore(backup.Id);
        Assert.Equal(originalHash, Hash(Path.Combine(entry.ResourcePath, "bundle.bin")));
        Assert.False(Directory.Exists(entry.BackupPath));
        Assert.False((File.GetAttributes(entry.ResourcePath) & FileAttributes.ReparsePoint) != 0);
        Assert.True(Assert.Single(service.GetBackups(game)).Restored);
        Assert.Equal("shared-write", File.ReadAllText(Path.Combine(Resource("1234ABCD"), "through-link.bin")));
        Assert.Equal("independent-save", File.ReadAllText(Path.Combine(game, "LocalSave", "save.bin")));
    }

    /// <summary>账号尚未创建 0000 时，共享和恢复应只管理本次链接。</summary>
    [Fact]
    public void EmptyAccountCanShareAndRestoreWithoutCreatingOriginalDirectory()
    {
        Directory.CreateDirectory(Path.Combine(game, "LocalData", "AAAABBBB"));
        Assert.Contains(service.ScanProfiles(game), profile => profile.FolderName == "AAAABBBB" && profile.Bytes == 0);
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["AAAABBBB"]));
        Assert.False(Assert.Single(backup.Entries).OriginalExisted);
        service.Restore(backup.Id);
        Assert.False(Directory.Exists(Resource("AAAABBBB")));
    }

    /// <summary>无效目录名和路径穿越必须在任何备份产生前被拒绝。</summary>
    [Theory]
    [InlineData("..")]
    [InlineData("1234ABCD/../5678EF90")]
    [InlineData("1234ABCDE")]
    [InlineData("ZZZZZZZZ")]
    [InlineData("1234ABCD ")]
    public void InvalidAccountNamesLeaveResourceDirectoriesUntouched(string name)
    {
        Assert.Throws<ArgumentException>(() => service.EnableSharing(game, "1234ABCD", [name]));
        Assert.Equal("target-original-v1", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
        Assert.False(Directory.Exists(state));
    }

    /// <summary>将来源同时作为目标会破坏原资源，应在事务前拒绝。</summary>
    [Fact]
    public void SourceAsTargetIsRejectedBeforeMutation()
    {
        Assert.Throws<ArgumentException>(() => service.EnableSharing(game, "1234ABCD", ["1234abcd"]));
        Assert.False(Directory.Exists(state));
    }

    /// <summary>目标账号目录不存在时不得擅自创建账号。</summary>
    [Fact]
    public void MissingAccountIsRejectedBeforeMutation()
    {
        Assert.Throws<DirectoryNotFoundException>(() => service.EnableSharing(game, "1234ABCD", ["FFFFFFFF"]));
        Assert.False(Directory.Exists(Resource("FFFFFFFF")));
    }

    /// <summary>空资源目录不得作为共享来源。</summary>
    [Fact]
    public void EmptyResourceSourceIsRejected()
    {
        File.Delete(Path.Combine(Resource("1234ABCD"), "bundle.bin"));
        Assert.Throws<InvalidOperationException>(() => service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.False(Directory.Exists(state));
    }

    /// <summary>游戏运行检查必须拦截共享和恢复的实际文件操作。</summary>
    [Fact]
    public void RunningGamePreventsEnableAndRestore()
    {
        var blocked = new ResourceSharingService(state, () => true);
        Assert.Throws<InvalidOperationException>(() => blocked.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.Throws<InvalidOperationException>(() => blocked.Restore(backup.Id));
        Assert.True((File.GetAttributes(Resource("5678EF90")) & FileAttributes.ReparsePoint) != 0);
    }

    /// <summary>原目录已经移动但 Moved 尚未写入时，应按真实目录恢复。</summary>
    [Fact]
    public void RestoreReconcilesMoveBeforeManifestStepWasSaved()
    {
        var backup = PendingBackup(true);
        Directory.Move(backup.Entries[0].ResourcePath, backup.Entries[0].BackupPath);
        SaveFixtureManifest(backup);
        service.Restore(backup.Id);
        Assert.Equal("target-original-v1", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
        Assert.True(Assert.Single(service.GetBackups(game)).Restored);
    }

    /// <summary>junction 已经创建但 Linked 尚未写入时，恢复仍应识别本事务链接。</summary>
    [Fact]
    public void RestoreReconcilesJunctionBeforeManifestStepWasSaved()
    {
        var backup = PendingBackup(true);
        Directory.Move(backup.Entries[0].ResourcePath, backup.Entries[0].BackupPath);
        CreateFixtureJunction(backup.Entries[0].ResourcePath, backup.SourcePath);
        SaveFixtureManifest(backup);
        service.Restore(backup.Id);
        Assert.Equal("target-original-v1", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
        Assert.False(Directory.Exists(backup.Entries[0].BackupPath));
    }

    /// <summary>有未完成事务时，重复启用不得覆盖保留下来的备份。</summary>
    [Fact]
    public void PendingTransactionBlocksOverlappingTarget()
    {
        var backup = PendingBackup(true);
        SaveFixtureManifest(backup);
        Assert.Throws<InvalidOperationException>(() => service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.Equal("target-original-v1", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
    }

    /// <summary>活动共享的来源不得再次被替换为其他账号的链接。</summary>
    [Fact]
    public void ActiveSharingSourceCannotBecomeAnotherSharingTarget()
    {
        Directory.CreateDirectory(Resource("9ABCDEF0"));
        File.WriteAllText(Path.Combine(Resource("9ABCDEF0"), "bundle.bin"), "third-source");
        service.EnableSharing(game, "1234ABCD", ["5678EF90"]);
        Assert.Throws<InvalidOperationException>(() => service.EnableSharing(game, "9ABCDEF0", ["1234ABCD"]));
        Assert.Equal("main-resource-v1", File.ReadAllText(Path.Combine(Resource("1234ABCD"), "bundle.bin")));
        Assert.Equal("main-resource-v1", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
    }

    /// <summary>junction 在唯一暂存路径完成后才安装到目标，中断时仍可找回原目录。</summary>
    [Fact]
    public void InterruptedStagedJunctionRestoresOriginalWithoutTargetPlaceholder()
    {
        var interrupted = new ResourceSharingService(state, () => Directory.Exists(Path.GetDirectoryName(Resource("5678EF90"))) &&
            Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(Resource("5678EF90"))!, "0000.mdjunction-*").Any());
        Assert.Throws<InvalidOperationException>(() => interrupted.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        var backup = Assert.Single(service.GetBackups(game));
        var staging = Resource("5678EF90") + ".mdjunction-" + backup.Id;
        Assert.False(Directory.Exists(Resource("5678EF90")));
        Assert.True((File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0);
        service.Restore(backup.Id);
        Assert.Equal("target-original-v1", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
        Assert.False(Directory.Exists(staging));
    }

    /// <summary>未知暂存空目录不得误删，同时不会占用实际 0000 的恢复路径。</summary>
    [Fact]
    public void UnknownStagingPlaceholderIsPreservedWhileOriginalRestores()
    {
        var backup = PendingBackup(true);
        Directory.Move(backup.Entries[0].ResourcePath, backup.Entries[0].BackupPath);
        var staging = Resource("5678EF90") + ".mdjunction-" + backup.Id;
        Directory.CreateDirectory(staging);
        SaveFixtureManifest(backup);
        service.Restore(backup.Id);
        Assert.Equal("target-original-v1", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
        Assert.True(Directory.Exists(staging));
        Assert.True(Assert.Single(service.GetBackups(game)).Restored);
    }

    /// <summary>恢复发现第三方新建目录时必须保留它和原始备份。</summary>
    [Fact]
    public void RestorePreservesUnrelatedReplacementDirectory()
    {
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Directory.Delete(Resource("5678EF90"), false);
        Directory.CreateDirectory(Resource("5678EF90"));
        File.WriteAllText(Path.Combine(Resource("5678EF90"), "new.bin"), "unrelated-data");
        Assert.Throws<InvalidOperationException>(() => service.Restore(backup.Id));
        Assert.Equal("unrelated-data", File.ReadAllText(Path.Combine(Resource("5678EF90"), "new.bin")));
        Assert.Equal("target-original-v1", File.ReadAllText(Path.Combine(backup.Entries[0].BackupPath, "bundle.bin")));
    }

    /// <summary>原备份缺失后，第三方目录不得被错误标记成已经找回的原始目录。</summary>
    [Fact]
    public void MissingBackupAndReplacementDirectoryRemainAnUnrestoredConflict()
    {
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Directory.Delete(Resource("5678EF90"), false);
        DeleteFixtureDirectory(backup.Entries[0].BackupPath);
        Directory.CreateDirectory(Resource("5678EF90"));
        File.WriteAllText(Path.Combine(Resource("5678EF90"), "new.bin"), "unrelated-data");
        Assert.Throws<InvalidOperationException>(() => service.Restore(backup.Id));
        Assert.False(Assert.Single(service.GetBackups(game)).Restored);
        Assert.Equal("unrelated-data", File.ReadAllText(Path.Combine(Resource("5678EF90"), "new.bin")));
    }

    /// <summary>原目录回迁完成但清单尚未更新时，新服务应凭真实目录状态完成恢复。</summary>
    [Fact]
    public void RestoreReconcilesOriginalAlreadyMovedBackAfterRestart()
    {
        var expected = Hash(Path.Combine(Resource("5678EF90"), "bundle.bin"));
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Directory.Delete(Resource("5678EF90"), false);
        Directory.Move(backup.Entries[0].BackupPath, Resource("5678EF90"));
        var restarted = new ResourceSharingService(state, () => false);
        restarted.Restore(backup.Id);
        Assert.True(Assert.Single(restarted.GetBackups(game)).Restored);
        Assert.Equal(expected, Hash(Path.Combine(Resource("5678EF90"), "bundle.bin")));
    }

    /// <summary>移动前清单意图已落盘，游戏突然启动时原目录仍能完整恢复。</summary>
    [Fact]
    public void IntentIsPersistedBeforeFirstResourceMove()
    {
        var interrupted = new ResourceSharingService(state, () => Directory.Exists(BackupState));
        Assert.Throws<InvalidOperationException>(() => interrupted.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        var backup = Assert.Single(service.GetBackups(game));
        Assert.False(backup.Restored);
        Assert.Equal("target-original-v1", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
        service.Restore(backup.Id);
        Assert.True(Assert.Single(service.GetBackups(game)).Restored);
    }

    /// <summary>来源资源被移除后，匹配的损坏 junction 仍可非递归移除并恢复原目录。</summary>
    [Fact]
    public void DanglingJunctionCanRestoreOriginalResources()
    {
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        File.Delete(Path.Combine(Resource("1234ABCD"), "bundle.bin"));
        Directory.Delete(Resource("1234ABCD"), false);
        service.Restore(backup.Id);
        Assert.Equal("target-original-v1", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
        Assert.False(Directory.Exists(Resource("1234ABCD")));
    }

    /// <summary>指向第三方来源的 junction 在启用和恢复时均应保留。</summary>
    [Fact]
    public void UnknownJunctionTargetIsNeverRemoved()
    {
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.bin"), "outside-data");
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Directory.Delete(Resource("5678EF90"), false);
        CreateFixtureJunction(Resource("5678EF90"), outside);
        Assert.Throws<InvalidOperationException>(() => service.Restore(backup.Id));
        Assert.Throws<InvalidOperationException>(() => service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.Equal("outside-data", File.ReadAllText(Path.Combine(outside, "keep.bin")));
        Assert.True(Directory.Exists(backup.Entries[0].BackupPath));
    }

    /// <summary>扫描不得跟随任意深度链接，也不得把非法名称视为账号。</summary>
    [Fact]
    public void ScanSkipsNestedLinksAndInvalidAccountDirectories()
    {
        var external = Path.Combine(root, "external");
        Directory.CreateDirectory(external);
        File.WriteAllBytes(Path.Combine(external, "large.bin"), new byte[10000]);
        CreateFixtureJunction(Path.Combine(Resource("1234ABCD"), "nested"), external);
        Directory.CreateDirectory(Path.Combine(game, "LocalData", "not-account"));
        var profiles = service.ScanProfiles(game);
        Assert.Equal(2, profiles.Count);
        Assert.Equal(16, Assert.Single(profiles, profile => profile.FolderName == "1234ABCD").Bytes);
    }

    /// <summary>资源父目录是重解析点时，事务不得沿链接修改外部目录。</summary>
    [Fact]
    public void ReparseAccountAncestorIsRejected()
    {
        var moved = Path.Combine(root, "moved-account");
        Directory.Move(Path.Combine(game, "LocalData", "5678EF90"), moved);
        CreateFixtureJunction(Path.Combine(game, "LocalData", "5678EF90"), moved);
        Assert.Throws<InvalidOperationException>(() => service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.Equal("target-original-v1", File.ReadAllText(Path.Combine(moved, "0000", "bundle.bin")));
    }

    /// <summary>清单损坏必须显式报告并保留原文件。</summary>
    [Fact]
    public void MalformedManifestIsReportedAndPreserved()
    {
        Directory.CreateDirectory(BackupState);
        var manifest = Path.Combine(BackupState, new string('a', 32) + ".json");
        File.WriteAllText(manifest, "{broken");
        Assert.Throws<InvalidDataException>(() => service.GetBackups(game));
        Assert.Equal("{broken", File.ReadAllText(manifest));
    }

    /// <summary>应用设置和资源事务必须独立存放，根目录设置不得误读为事务。</summary>
    [Fact]
    public void ApplicationSettingsAreNotReadAsResourceManifests()
    {
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "settings.json"), "{\"GamePath\":\"fixture\"}");
        Assert.Empty(service.GetBackups(game));
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.True(File.Exists(Path.Combine(BackupState, backup.Id + ".json")));
        Assert.Equal("{\"GamePath\":\"fixture\"}", File.ReadAllText(Path.Combine(state, "settings.json")));
    }

    /// <summary>清单里逃逸的备份路径必须在恢复前被拒绝。</summary>
    [Fact]
    public void ManifestPathEscapeIsRejectedWithoutTouchingExternalData()
    {
        var backup = PendingBackup(true);
        var outside = Path.Combine(root, "outside-backup");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.bin"), "keep");
        backup.Entries[0].BackupPath = outside;
        SaveFixtureManifest(backup);
        Assert.Throws<InvalidDataException>(() => service.Restore(backup.Id));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.bin")));
    }

    /// <summary>完整目录身份或格式版本缺失时，清单应保留并拒绝恢复。</summary>
    [Theory]
    [InlineData("OriginalDirectoryIdentities")]
    [InlineData("FormatVersion")]
    [InlineData("EmptyIdentities")]
    public void MissingManifestIdentityOrVersionIsRejected(string corruption)
    {
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        var path = Path.Combine(BackupState, backup.Id + ".json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (corruption == "EmptyIdentities") manifest["OriginalDirectoryIdentities"] = new JsonObject();
        else manifest.Remove(corruption);
        File.WriteAllText(path, manifest.ToJsonString());
        Assert.Throws<InvalidDataException>(() => new ResourceSharingService(state, () => false).Restore(backup.Id));
        Assert.True((File.GetAttributes(Resource("5678EF90")) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal("target-original-v1", File.ReadAllText(Path.Combine(backup.Entries[0].BackupPath, "bundle.bin")));
    }

    /// <summary>构造一个尚未执行目录操作的真实事务意图。</summary>
    private ShareBackup PendingBackup(bool originalExisted)
    {
        var id = Guid.NewGuid().ToString("N");
        return new ShareBackup
        {
            Id = id,
            CreatedAt = DateTimeOffset.UtcNow,
            GamePath = game,
            SourcePath = Resource("1234ABCD"),
            Entries = [new ShareEntry { ResourcePath = Resource("5678EF90"), BackupPath = Resource("5678EF90") + ".mdbackup-" + id, OriginalExisted = originalExisted }]
        };
    }

    /// <summary>为中断窗口准备原子操作前就已存在的清单文件。</summary>
    private void SaveFixtureManifest(ShareBackup backup)
    {
        if (string.IsNullOrEmpty(backup.Entries[0].BackupPath))
        {
            backup.Entries[0].BackupPath = backup.Entries[0].ResourcePath + ".mdbackup-" + backup.Id;
        }
        Directory.CreateDirectory(BackupState);
        var document = JsonSerializer.SerializeToNode(backup)!.AsObject();
        document["FormatVersion"] = 1;
        var identities = new Dictionary<string, string>();
        foreach (var entry in backup.Entries.Where(entry => entry.OriginalExisted))
        {
            var original = Directory.Exists(entry.BackupPath) ? entry.BackupPath : entry.ResourcePath;
            identities[entry.ResourcePath] = JunctionOperations.GetDirectoryIdentity(original);
        }
        document["OriginalDirectoryIdentities"] = JsonSerializer.SerializeToNode(identities);
        File.WriteAllText(Path.Combine(BackupState, backup.Id + ".json"), document.ToJsonString());
    }

    /// <summary>独立于应用设置的资源事务子目录。</summary>
    private string BackupState => Path.Combine(state, "resource-backups");

    /// <summary>返回临时账号唯一允许被管理的资源目录。</summary>
    private string Resource(string folder) => Path.Combine(game, "LocalData", folder, "0000");

    /// <summary>计算文件哈希，检查原资源是否被无损保留。</summary>
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>使用测试生成的固定临时路径创建真实 NTFS junction。</summary>
    private static void CreateFixtureJunction(string path, string target)
    {
        Assert.DoesNotContain('"', path + target);
        Assert.DoesNotContain('&', path + target);
        var start = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{path}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>清理隔离目录时逐项识别链接，绝不递归进入共享来源。</summary>
    public void Dispose() => DeleteFixtureDirectory(root);

    /// <summary>仅删除本测试拥有的临时目录，遇到链接只删除链接本身。</summary>
    private static void DeleteFixtureDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, false);
            return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(child) & FileAttributes.Directory) != 0) DeleteFixtureDirectory(child);
            else File.Delete(child);
        }
        Directory.Delete(path, false);
    }
}
