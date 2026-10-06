using System.Text.Json.Nodes;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>通过真实文件变更和显式故障注入验证扫描、清单读取及首次保存边界。</summary>
public sealed class ResourceScanFailureTests : IDisposable
{
    /// <summary>本测试独占的临时根目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher.ScanFailures", Guid.NewGuid().ToString("N"));
    /// <summary>包含两个真实账号的临时游戏目录。</summary>
    private readonly string game;
    /// <summary>本测试使用的状态根目录。</summary>
    private readonly string state;
    /// <summary>在真实磁盘操作前实施故障或并发变更的文件系统。</summary>
    private readonly ResourceFailureTests.FaultFiles files = new();
    /// <summary>使用真实文件系统注入边界的被测资源服务。</summary>
    private readonly ResourceSharingService service;

    /// <summary>创建六字节来源和八字节目标资源，隔离每个测试的事务状态。</summary>
    public ResourceScanFailureTests()
    {
        game = Path.Combine(root, "game");
        state = Path.Combine(root, "state");
        Directory.CreateDirectory(Resource("1234ABCD"));
        Directory.CreateDirectory(Resource("5678EF90"));
        File.WriteAllText(Path.Combine(Resource("1234ABCD"), "bundle.bin"), "source");
        File.WriteAllText(Path.Combine(Resource("5678EF90"), "bundle.bin"), "original");
        service = new ResourceSharingService(state, files, () => false);
    }

    /// <summary>安装尚未产生 LocalData 时，扫描应返回空集合且不创建任何账号目录。</summary>
    [Fact]
    public void MissingLocalDataReturnsNoProfiles()
    {
        var localData = Path.Combine(game, "LocalData");
        ResourceFailureTests.DeleteTree(localData);
        Assert.Empty(service.ScanProfiles(game));
        Assert.False(Directory.Exists(localData));
    }

    /// <summary>枚举账号后该账号被并发删除时，应跳过该账号并继续扫描其余账号。</summary>
    [Fact]
    public void AccountRemovedBeforeAttributeReadIsSkipped()
    {
        var account = Path.GetDirectoryName(Resource("1234ABCD"))!;
        files.Before = (operation, path) =>
        {
            if (operation == "Attributes" && path == account) ResourceFailureTests.DeleteTree(account);
        };
        var profile = Assert.Single(service.ScanProfiles(game));
        Assert.Equal("5678EF90", profile.FolderName);
        Assert.Equal(8, profile.Bytes);
        Assert.False(Directory.Exists(account));
    }

    /// <summary>八位十六进制名称的普通文件应保留，但不得被扫描为账号目录。</summary>
    [Fact]
    public void HexadecimalFileIsNotAnAccountDirectory()
    {
        var file = Path.Combine(game, "LocalData", "ABCDEF01");
        File.WriteAllText(file, "keep-file");
        Assert.Equal(2, service.ScanProfiles(game).Count);
        Assert.Equal("keep-file", File.ReadAllText(file));
    }

    /// <summary>八位十六进制名称的账号 junction 应被跳过，扫描不进入外部目标。</summary>
    [Fact]
    public void HexadecimalAccountJunctionIsSkipped()
    {
        var external = Path.Combine(root, "external-account");
        Directory.CreateDirectory(Path.Combine(external, "0000"));
        var externalFile = Path.Combine(external, "0000", "outside.bin");
        File.WriteAllBytes(externalFile, new byte[1000]);
        var link = Path.Combine(game, "LocalData", "ABCDEF01");
        JunctionOperations.Create(link, external);
        Assert.Equal(2, service.ScanProfiles(game).Count);
        Assert.Equal(external, JunctionOperations.GetTarget(link));
        Assert.Equal(1000, new FileInfo(externalFile).Length);
    }

    /// <summary>启用真实共享后，扫描应报告来源容量以及目标链接、来源路径和零重复容量。</summary>
    [Fact]
    public void ScanReportsRealSharedResourceWithoutDoubleCounting()
    {
        service.EnableSharing(game, "1234ABCD", ["5678EF90"]);
        var profiles = service.ScanProfiles(game);
        var source = Assert.Single(profiles, profile => profile.FolderName == "1234ABCD");
        var target = Assert.Single(profiles, profile => profile.FolderName == "5678EF90");
        Assert.False(source.IsLinked);
        Assert.Equal(6, source.Bytes);
        Assert.True(target.IsLinked);
        Assert.Equal(Resource("1234ABCD"), target.LinkTarget);
        Assert.Equal(0, target.Bytes);
        Assert.Equal(Resource("5678EF90"), target.ResourcePath);
    }

    /// <summary>0000 被普通文件占用时，扫描容量应为零且保留文件内容。</summary>
    [Fact]
    public void FileOccupyingResourceRootHasNoDirectoryCapacity()
    {
        var source = Resource("1234ABCD");
        ResourceFailureTests.DeleteTree(source);
        File.WriteAllText(source, "resource-is-a-file");
        var profile = Assert.Single(service.ScanProfiles(game), profile => profile.FolderName == "1234ABCD");
        Assert.Equal(0, profile.Bytes);
        Assert.False(profile.IsLinked);
        Assert.Equal("resource-is-a-file", File.ReadAllText(source));
    }

    /// <summary>正常多层目录应累计其中的普通文件，而不是只计算资源根目录的直接文件。</summary>
    [Fact]
    public void NestedRealDirectoriesContributeTheirFileBytes()
    {
        var nested = Path.Combine(Resource("1234ABCD"), "nested", "deeper");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "nested.bin"), [1, 2, 3, 4, 5]);
        Assert.Equal(11, Assert.Single(service.ScanProfiles(game), profile => profile.FolderName == "1234ABCD").Bytes);
        Assert.Equal(8, Assert.Single(service.ScanProfiles(game), profile => profile.FolderName == "5678EF90").Bytes);
    }

    /// <summary>目录进入遍历栈后被移除或替换为链接、文件时，应跳过该目录且不累计替换目标。</summary>
    [Theory]
    [InlineData("removed")]
    [InlineData("junction")]
    [InlineData("file")]
    public void QueuedDirectoryMutationIsSkippedBeforeTraversal(string mutation)
    {
        var nested = Path.Combine(Resource("1234ABCD"), "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "nested.bin"), new byte[200]);
        var external = Path.Combine(root, "external-resource");
        Directory.CreateDirectory(external);
        var externalFile = Path.Combine(external, "outside.bin");
        File.WriteAllBytes(externalFile, new byte[1000]);
        var reads = 0;
        files.Before = (operation, path) =>
        {
            if (operation != "Attributes" || path != nested || ++reads != 2) return;
            ResourceFailureTests.DeleteTree(nested);
            if (mutation == "junction") JunctionOperations.Create(nested, external);
            if (mutation == "file") File.WriteAllText(nested, "replacement-file");
        };
        var profiles = service.ScanProfiles(game);
        Assert.Equal(6, Assert.Single(profiles, profile => profile.FolderName == "1234ABCD").Bytes);
        Assert.Equal(8, Assert.Single(profiles, profile => profile.FolderName == "5678EF90").Bytes);
        Assert.Equal(1000, new FileInfo(externalFile).Length);
        if (mutation == "junction") Assert.Equal(external, JunctionOperations.GetTarget(nested));
        if (mutation == "file") Assert.Equal("replacement-file", File.ReadAllText(nested));
        if (mutation == "removed") Assert.False(Directory.Exists(nested));
    }

    /// <summary>资源根目录在容量遍历时发生磁盘错误，应保留账号并报告零容量。</summary>
    [Fact]
    public void ResourceRootAttributeIoFailureDoesNotAbortOtherProfiles()
    {
        var source = Resource("1234ABCD");
        var reads = 0;
        files.Before = (operation, path) =>
        {
            if (operation == "Attributes" && path == source && ++reads == 2) throw new IOException("fixture-root-attributes");
        };
        var profiles = service.ScanProfiles(game);
        Assert.Equal(0, Assert.Single(profiles, profile => profile.FolderName == "1234ABCD").Bytes);
        Assert.Equal(8, Assert.Single(profiles, profile => profile.FolderName == "5678EF90").Bytes);
        Assert.Equal("source", File.ReadAllText(Path.Combine(source, "bundle.bin")));
    }

    /// <summary>文件枚举后在属性读取前被删除时，应跳过缺失文件并继续扫描其他账号。</summary>
    [Fact]
    public void EnumeratedFileRemovedBeforeAttributeReadIsSkipped()
    {
        var removed = Path.Combine(Resource("1234ABCD"), "bundle.bin");
        files.Before = (operation, path) =>
        {
            if (operation == "Attributes" && path == removed) File.Delete(removed);
        };
        var profiles = service.ScanProfiles(game);
        Assert.Equal(0, Assert.Single(profiles, profile => profile.FolderName == "1234ABCD").Bytes);
        Assert.Equal(8, Assert.Single(profiles, profile => profile.FolderName == "5678EF90").Bytes);
        Assert.False(File.Exists(removed));
    }

    /// <summary>恢复指定清单在属性读取前被删除时，应报告文件缺失并保留真实资源备份。</summary>
    [Fact]
    public void ManifestRemovedBeforeRestoreAttributeReadPreservesResources()
    {
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        var manifest = Manifest(backup);
        files.Before = (operation, path) =>
        {
            if (operation == "Attributes" && path == manifest) File.Delete(manifest);
        };
        Assert.Throws<FileNotFoundException>(() => service.Restore(backup.Id));
        Assert.False(File.Exists(manifest));
        AssertSharedResourcesPreserved(backup);
    }

    /// <summary>清单文件名被目录占用时，恢复应拒绝读取并保留共享链接及原始备份。</summary>
    [Fact]
    public void DirectoryOccupyingManifestIsRejectedBeforeRestore()
    {
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        var manifest = Manifest(backup);
        File.Delete(manifest);
        Directory.CreateDirectory(manifest);
        Assert.Throws<InvalidDataException>(() => service.Restore(backup.Id));
        Assert.True(Directory.Exists(manifest));
        AssertSharedResourcesPreserved(backup);
    }

    /// <summary>原来没有资源目录的目标不应接受伪造原目录身份，即使身份格式合法也应拒绝恢复。</summary>
    [Fact]
    public void IdentityForTargetWithoutOriginalDirectoryIsRejected()
    {
        ResourceFailureTests.DeleteTree(Resource("5678EF90"));
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        var entry = Assert.Single(backup.Entries);
        Assert.False(entry.OriginalExisted);
        var manifest = Manifest(backup);
        var document = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
        document["OriginalDirectoryIdentities"]!.AsObject()[entry.ResourcePath] = JunctionOperations.GetDirectoryIdentity(Resource("1234ABCD"));
        var polluted = document.ToJsonString();
        File.WriteAllText(manifest, polluted);
        Assert.Throws<InvalidDataException>(() => service.Restore(backup.Id));
        Assert.Throws<InvalidDataException>(() => service.GetBackups(game));
        Assert.Equal(polluted, File.ReadAllText(manifest));
        Assert.Equal(Resource("1234ABCD"), JunctionOperations.GetTarget(entry.ResourcePath));
        Assert.False(Directory.Exists(entry.BackupPath));
        Assert.Equal("source", File.ReadAllText(Path.Combine(Resource("1234ABCD"), "bundle.bin")));
    }

    /// <summary>首次保存清单前该路径被目录占用时，应拒绝事务并完整保留两个账号资源。</summary>
    [Fact]
    public void DirectoryOccupyingFirstManifestSavePreventsResourceMutation()
    {
        var backupState = Path.Combine(state, "resource-backups");
        files.Before = (operation, path) =>
        {
            if (operation == "Attributes" && Path.GetDirectoryName(path) == backupState && path.EndsWith(".json", StringComparison.Ordinal))
                Directory.CreateDirectory(path);
        };
        Assert.Throws<InvalidDataException>(() => service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.Equal("source", File.ReadAllText(Path.Combine(Resource("1234ABCD"), "bundle.bin")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
        Assert.Null(JunctionOperations.GetTarget(Resource("5678EF90")));
        Assert.Single(Directory.GetDirectories(backupState, "*.json"));
        Assert.Empty(Directory.GetFiles(backupState, "*.tmp"));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(Resource("5678EF90"))!, "0000.mdbackup-*"));
    }

    /// <summary>核验真实共享链接、来源内容和原始备份未因清单读取失败被改变。</summary>
    private void AssertSharedResourcesPreserved(ShareBackup backup)
    {
        Assert.Equal(Resource("1234ABCD"), JunctionOperations.GetTarget(Resource("5678EF90")));
        Assert.Equal("source", File.ReadAllText(Path.Combine(Resource("1234ABCD"), "bundle.bin")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Assert.Single(backup.Entries).BackupPath, "bundle.bin")));
    }

    /// <summary>返回事务清单的实际文件路径。</summary>
    private string Manifest(ShareBackup backup) => Path.Combine(state, "resource-backups", backup.Id + ".json");

    /// <summary>返回临时账号唯一可管理的资源目录。</summary>
    private string Resource(string folder) => Path.Combine(game, "LocalData", folder, "0000");

    /// <summary>复用不跟随目录链接的清理逻辑，仅清理本测试拥有的临时根目录。</summary>
    public void Dispose() => ResourceFailureTests.DeleteTree(root);
}
