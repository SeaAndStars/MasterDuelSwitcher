using System.Text.Json.Nodes;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>验证还原阶段的备份污染、外部路径替换与部分完成状态。</summary>
public sealed class ResourceRestoreFailureTests : IDisposable
{
    /// <summary>本测试唯一拥有的隔离目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher.RestoreFailures", Guid.NewGuid().ToString("N"));
    /// <summary>临时游戏安装路径。</summary>
    private readonly string game;
    /// <summary>临时应用状态路径。</summary>
    private readonly string state;
    /// <summary>注入实际文件竞态的文件系统。</summary>
    private readonly ResourceFailureTests.FaultFiles files = new();
    /// <summary>被测恢复服务。</summary>
    private readonly ResourceSharingService service;

    /// <summary>准备不同内容的真实来源与目标资源。</summary>
    public ResourceRestoreFailureTests()
    {
        game = Path.Combine(root, "game");
        state = Path.Combine(root, "state");
        WriteResource("1234ABCD", "source");
        WriteResource("5678EF90", "original");
        service = new ResourceSharingService(state, files, () => false);
    }

    /// <summary>已回迁条目会被跳过，未回迁条目继续恢复。</summary>
    [Fact]
    public void PartiallyRestoredTransactionSkipsCompletedEntries()
    {
        WriteResource("AAAABBBB", "second-original");
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90", "AAAABBBB"]));
        var first = backup.Entries[0];
        Directory.Delete(first.ResourcePath, false);
        Directory.Move(first.BackupPath, first.ResourcePath);
        var manifestPath = Manifest(backup.Id);
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["Entries"]![0]!["Restored"] = true;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        service.Restore(backup.Id);
        Assert.Equal("original", Read("5678EF90"));
        Assert.Equal("second-original", Read("AAAABBBB"));
        Assert.True(Assert.Single(service.GetBackups(game)).Restored);
    }

    /// <summary>备份检查后被替换为文件或 junction，恢复保留未知路径和真正原资源。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackupChangedAfterAncestorValidationIsPreserved(bool junction)
    {
        var backup = Share();
        var entry = backup.Entries[0];
        var original = Path.Combine(root, "original-held");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.bin"), "third-party");
        files.Before = (operation, path) =>
        {
            if (operation != "Attributes" || path != entry.BackupPath) return;
            files.Before = null;
            Directory.Move(entry.BackupPath, original);
            if (junction) JunctionOperations.Create(entry.BackupPath, outside);
            else File.WriteAllText(entry.BackupPath, "third-party");
        };
        Assert.Throws<InvalidOperationException>(() => service.Restore(backup.Id));
        Assert.Equal("original", File.ReadAllText(Path.Combine(original, "bundle.bin")));
        Assert.Equal("third-party", File.ReadAllText(junction ? Path.Combine(entry.BackupPath, "keep.bin") : entry.BackupPath));
        Assert.True((File.GetAttributes(entry.ResourcePath) & FileAttributes.ReparsePoint) != 0);
    }

    /// <summary>没有原始目录的事务遇到未知备份时，不移动该目录。</summary>
    [Fact]
    public void AccountWithoutOriginalPreservesUnexpectedBackupDirectory()
    {
        Directory.CreateDirectory(Path.Combine(game, "LocalData", "AAAABBBB"));
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["AAAABBBB"]));
        Directory.CreateDirectory(backup.Entries[0].BackupPath);
        File.WriteAllText(Path.Combine(backup.Entries[0].BackupPath, "keep.bin"), "unknown-backup");
        Assert.Throws<InvalidOperationException>(() => service.Restore(backup.Id));
        Assert.Equal("unknown-backup", File.ReadAllText(Path.Combine(backup.Entries[0].BackupPath, "keep.bin")));
        Assert.True((File.GetAttributes(Resource("AAAABBBB")) & FileAttributes.ReparsePoint) != 0);
    }

    /// <summary>原备份缺失时保留现有共享 junction；资源与备份均缺失时保留未完成状态。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOriginalBackupNeverMarksTransactionRestored(bool removeLink)
    {
        var backup = Share();
        ResourceFailureTests.DeleteTree(backup.Entries[0].BackupPath);
        if (removeLink) Directory.Delete(Resource("5678EF90"), false);
        Assert.Throws<InvalidOperationException>(() => service.Restore(backup.Id));
        Assert.False(Assert.Single(service.GetBackups(game)).Restored);
        Assert.Equal("source", Read("1234ABCD"));
        if (!removeLink) Assert.Equal("source", Read("5678EF90"));
    }

    /// <summary>无原目录账号出现第三方文件或目录时，不删除它或误标还原。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AccountWithoutOriginalPreservesReplacement(bool file)
    {
        Directory.CreateDirectory(Path.Combine(game, "LocalData", "AAAABBBB"));
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["AAAABBBB"]));
        Directory.Delete(Resource("AAAABBBB"), false);
        if (file) File.WriteAllText(Resource("AAAABBBB"), "unknown-file");
        else { Directory.CreateDirectory(Resource("AAAABBBB")); File.WriteAllText(Path.Combine(Resource("AAAABBBB"), "keep.bin"), "unknown-file"); }
        Assert.Throws<InvalidOperationException>(() => service.Restore(backup.Id));
        Assert.Equal("unknown-file", File.ReadAllText(file ? Resource("AAAABBBB") : Path.Combine(Resource("AAAABBBB"), "keep.bin")));
        Assert.False(Assert.Single(service.GetBackups(game)).Restored);
    }

    /// <summary>移除共享链接之后、回迁之前出现的目录也应保留。</summary>
    [Fact]
    public void ReplacementCreatedImmediatelyBeforeBackupMoveIsPreserved()
    {
        var backup = Share();
        var targetChecks = 0;
        files.Before = (operation, path) =>
        {
            if (operation != "Attributes" || path != Resource("5678EF90") || ++targetChecks != 2) return;
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "keep.bin"), "racing-directory");
        };
        Assert.Throws<InvalidOperationException>(() => service.Restore(backup.Id));
        Assert.Equal("racing-directory", File.ReadAllText(Path.Combine(Resource("5678EF90"), "keep.bin")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(backup.Entries[0].BackupPath, "bundle.bin")));
    }

    /// <summary>账号父目录在回迁前被移除时，不擅自重新创建账号。</summary>
    [Fact]
    public void AccountRemovedBeforeMutationIsReported()
    {
        var backup = Share();
        var account = Path.GetDirectoryName(Resource("5678EF90"))!;
        var preserved = Path.Combine(root, "preserved-account");
        files.Before = (operation, path) =>
        {
            if (operation != "DirectoryExists" || path != account) return;
            files.Before = null;
            Directory.Move(account, preserved);
        };
        Assert.Throws<DirectoryNotFoundException>(() => service.Restore(backup.Id));
        Assert.False(Directory.Exists(account));
        Assert.Equal("original", File.ReadAllText(Path.Combine(preserved, Path.GetFileName(backup.Entries[0].BackupPath), "bundle.bin")));
    }

    /// <summary>指向其他来源的暂存链接不删除，但实际原资源仍可恢复。</summary>
    [Fact]
    public void UnrelatedStagingJunctionIsPreserved()
    {
        var backup = Share();
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.bin"), "outside");
        var staging = Resource("5678EF90") + ".mdjunction-" + backup.Id;
        JunctionOperations.Create(staging, outside);
        service.Restore(backup.Id);
        Assert.Equal("original", Read("5678EF90"));
        Assert.Equal(outside, JunctionOperations.GetTarget(staging));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "keep.bin")));
    }

    /// <summary>返回默认两个账号的真实共享事务。</summary>
    private ShareBackup Share() => Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
    /// <summary>返回临时资源路径。</summary>
    private string Resource(string folder) => Path.Combine(game, "LocalData", folder, "0000");
    /// <summary>读取账号资源内容。</summary>
    private string Read(string folder) => File.ReadAllText(Path.Combine(Resource(folder), "bundle.bin"));
    /// <summary>写入账号资源夹具。</summary>
    private void WriteResource(string folder, string content) { Directory.CreateDirectory(Resource(folder)); File.WriteAllText(Path.Combine(Resource(folder), "bundle.bin"), content); }
    /// <summary>返回本事务的清单路径。</summary>
    private string Manifest(string id) => Path.Combine(state, "resource-backups", id + ".json");
    /// <summary>仅移除测试拥有的隔离目录，不跟随任何 junction。</summary>
    public void Dispose() => ResourceFailureTests.DeleteTree(root);
}
